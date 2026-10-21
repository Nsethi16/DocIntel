using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Authorization;
using DocIntel.Core.Harness;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Jobs;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Deleted
}

/// <summary>
/// Represents an asynchronous document processing job tracked across steps.
/// </summary>
public sealed class DocumentJob
{
    /// <summary>
    /// Cosmos DB document id (mirrors <see cref="JobId"/>).
    /// </summary>
    [JsonPropertyName("id")]
    public string Id
    {
        get => JobId;
        init => JobId = value;
    }

    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    public required string TenantDisplayName { get; init; }

    public required int AppliedHarnessVersion { get; init; }

    public TenantHarness? ProcessingHarness { get; init; }

    public required string FileName { get; init; }

    public JobStatus Status { get; set; } = JobStatus.Queued;

    public string? ErrorMessage { get; set; }

    public string? OriginalPdfLocation { get; set; }

    public string? OriginalSha256 { get; set; }

    public string? ArtifactDocumentId { get; set; }

    public string? LayoutSha256 { get; set; }

    public string? ProcessingResultSha256 { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }
    public ActorIdentity? DeletedBy { get; set; }
    public bool DeletionInDevelopment { get; set; }

    public string? ReviewId { get; set; }

    public ActorIdentity? SubmittedBy { get; init; }

    [JsonIgnore]
    public long StoreVersion { get; set; }

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    public string? ProcessingOwner { get; set; }

    public DateTimeOffset? ProcessingLeaseUntilUtc { get; set; }

    public int ExecutionAttempts { get; set; }

    public int TotalPages { get; set; }

    public string StrategyUsed { get; set; } = "Pending";

    public string? ModelIdUsed { get; set; }

    public bool? HasDevelopmentResults { get; set; }

    public string? PacketSummary { get; set; }

    public List<ExtractedEntity> ExtractedEntities { get; set; } = [];

    public List<DocumentDataExtractionResult> DataExtractions { get; set; } = [];

    public List<PromptColumnResult> PromptColumnResults { get; set; } = [];

    public List<WorkflowStepTrace> ExecutionTrace { get; set; } = [];

    public List<DocumentSlice> Segments { get; set; } = [];

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed record JobQueueTicket(string JobId, string TenantId);

public interface IJobStore
{
    Task UpsertJobAsync(DocumentJob job, CancellationToken cancellationToken = default);

    Task<DocumentJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default, string? tenantId = null);

    Task<IReadOnlyList<DocumentJob>> ListJobsByTenantAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>Searches one tenant using a server-compiled, bounded query plan.</summary>
    Task<DocumentQueryPage> SearchDocumentsAsync(
        string tenantId, DocumentQuery query, CancellationToken cancellationToken = default);

    Task<IDocumentJobLease?> TryAcquireJobAsync(
        string tenantId, string jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JobQueueTicket>> ListRecoverableJobsAsync(
        int maxCount, CancellationToken cancellationToken = default);
}

public interface IJobQueue
{
    ValueTask<bool> TryEnqueueAsync(JobQueueTicket ticket, CancellationToken cancellationToken = default);

    IAsyncEnumerable<JobQueueTicket> ReadAllAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryJobStore : IJobStore
{
    private readonly ConcurrentDictionary<(string Tenant, string Id), DocumentJob> _jobs = new();
    private readonly ConcurrentDictionary<(string Tenant, string Id), SemaphoreSlim> _processing = new();

    /// <inheritdoc />
    public Task<DocumentQueryPage> SearchDocumentsAsync(
        string tenantId, DocumentQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentQueryCompiler.Compile(tenantId, query);
        var offset = 0;
        if (query.ContinuationToken is not null &&
            (!Int32.TryParse(query.ContinuationToken,
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out offset) || offset < 0))
        {
            throw new ArgumentException("Invalid development-store continuation token.");
        }

        var matches = _jobs.Values.Where(j => DocumentQueryCompiler.Matches(j, tenantId, query))
            .OrderByDescending(j => j.CreatedAtUtc).ThenBy(j => j.JobId, StringComparer.Ordinal)
            .Skip(offset).Take(query.PageSize + 1).ToList();
        var references = matches.Take(query.PageSize)
            .Select(j => new DocumentReference(j.JobId, j.FileName, j.TotalPages, j.Segments.Count)).ToList();
        var nextToken = matches.Count > query.PageSize
            ? (offset + query.PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return Task.FromResult(new DocumentQueryPage(references, nextToken));
    }

    public Task UpsertJobAsync(DocumentJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        var key = (job.TenantId, job.JobId);
        var gate = _processing.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0)) throw new InvalidOperationException("Save an owned document through its lease.");
        try
        {
            if (_jobs.TryGetValue(key, out var existing) && existing.Status == JobStatus.Deleted &&
                job.Status != JobStatus.Deleted)
                throw new InvalidOperationException("A deleted document cannot be overwritten by an earlier result.");
            _jobs[key] = job;
        }
        finally { gate.Release(); }
        return Task.CompletedTask;
    }

    public Task<DocumentJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default, string? tenantId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        DocumentJob? job;
        if (tenantId is not null) _jobs.TryGetValue((tenantId, jobId), out job);
        else
        {
            var matches = _jobs.Where(item => item.Key.Id == jobId).Select(item => item.Value).Take(2).ToList();
            if (matches.Count > 1) throw new InvalidOperationException("A tenant ID is required for this document ID.");
            job = matches.FirstOrDefault();
        }
        if (job is not null && !string.IsNullOrWhiteSpace(tenantId) &&
            !string.Equals(job.TenantId, tenantId, StringComparison.Ordinal))
        {
            return Task.FromResult<DocumentJob?>(null);
        }
        return Task.FromResult(job?.Status == JobStatus.Deleted ? null : job);
    }

    public Task<IReadOnlyList<DocumentJob>> ListJobsByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        IReadOnlyList<DocumentJob> list = _jobs.Values
            .Where(j => j.Status != JobStatus.Deleted && string.Equals(j.TenantId, tenantId, StringComparison.Ordinal))
            .OrderByDescending(j => j.CreatedAtUtc)
            .ToList();
        return Task.FromResult(list);
    }

    public async Task<IDocumentJobLease?> TryAcquireJobAsync(
        string tenantId, string jobId, CancellationToken cancellationToken = default)
    {
        var key = (tenantId, jobId);
        var gate = _processing.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken)) return null;
        if (!_jobs.TryGetValue(key, out var job) || job.Status is not (JobStatus.Queued or JobStatus.Running))
        {
            gate.Release();
            return null;
        }
        job.ExecutionAttempts++;
        return new DocumentJobLease(job, ct =>
        {
            ct.ThrowIfCancellationRequested();
            _jobs[key] = job;
            return Task.FromResult(true);
        }, null, () =>
        {
            gate.Release();
            return Task.CompletedTask;
        });
    }

    public Task<IReadOnlyList<JobQueueTicket>> ListRecoverableJobsAsync(
        int maxCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxCount, 100);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<JobQueueTicket> tickets = _jobs
            .Where(item => item.Value.Status is JobStatus.Queued or JobStatus.Running &&
                (!_processing.TryGetValue(item.Key, out var gate) || gate.CurrentCount > 0))
            .OrderBy(item => item.Value.CreatedAtUtc).Take(maxCount)
            .Select(item => new JobQueueTicket(item.Value.JobId, item.Value.TenantId)).ToList();
        return Task.FromResult(tickets);
    }

}

public sealed class ChannelJobQueue(ILogger<ChannelJobQueue> logger) : IJobQueue
{
    private readonly Channel<JobQueueTicket> _channel = Channel.CreateBounded<JobQueueTicket>(100);

    public ValueTask<bool> TryEnqueueAsync(JobQueueTicket ticket, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_channel.Writer.TryWrite(ticket)) return ValueTask.FromResult(true);
        logger.LogDebug("Document dispatch hints are full; persisted job {JobId} must be rediscovered.", ticket.JobId);
        return ValueTask.FromResult(false);
    }

    public IAsyncEnumerable<JobQueueTicket> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
