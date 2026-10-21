using System.Net;
using System.Text.Json;
using DocIntel.Core.Authorization;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Jobs;

/// <summary>Policy is explicit; this release never converts a retention setting into automatic permanent erasure.</summary>
public sealed class DataRetentionPolicy
{
    public int? DocumentDays { get; set; }
    public int? ConversationDays { get; set; }
    public int? AuditDays { get; set; }
    public bool LegalHold { get; set; }
    public bool PermanentPurgeEnabled { get; set; }

    public bool IsValid => !PermanentPurgeEnabled &&
        new[] { DocumentDays, ConversationDays, AuditDays }.All(days => days is null or >= 1 and <= 36_500);
}

public sealed class DocumentLifecycleConflictException() : InvalidOperationException(
    "The document changed or is being processed. Reload it and retry only after processing has stopped.");

/// <summary>Fences soft deletion without removing originals, review history or archived results.</summary>
public sealed class DocumentLifecycleService(IJobStore jobs, ILogger<DocumentLifecycleService> logger)
{
    public async Task<DocumentJob?> SoftDeleteAsync(string tenantId, string jobId,
        ActorIdentity? actor, bool development, CancellationToken cancellationToken = default)
    {
        if (!development && actor is null) throw new UnauthorizedAccessException("A verified deletion actor is required.");
        var current = await jobs.GetJobAsync(jobId, cancellationToken, tenantId);
        if (current is null) return null;
        var changed = JsonSerializer.Deserialize<DocumentJob>(JsonSerializer.Serialize(current))!;
        changed.StoreVersion = current.StoreVersion;
        changed.Status = JobStatus.Deleted;
        changed.DeletedAtUtc = DateTimeOffset.UtcNow;
        changed.DeletedBy = actor;
        changed.DeletionInDevelopment = development;
        try
        {
            await jobs.UpsertJobAsync(changed, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw new DocumentLifecycleConflictException();
        }
        catch (CosmosException exception) when (exception.StatusCode is
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            throw new DocumentLifecycleConflictException();
        }
        logger.LogInformation("Document {JobId} in tenant {TenantId} was soft-deleted; retained data was not purged.",
            jobId, tenantId);
        return changed;
    }
}
