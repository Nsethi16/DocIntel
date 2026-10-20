using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Authorization;

namespace DocIntel.Core.Jobs;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed
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

    public required string FileName { get; init; }

    public JobStatus Status { get; set; } = JobStatus.Queued;

    public string? ErrorMessage { get; set; }

    public string? OriginalPdfLocation { get; set; }

    public string? OriginalSha256 { get; set; }

    public ActorIdentity? SubmittedBy { get; init; }

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

public sealed record JobQueueTicket(string JobId, string TenantId, string FileName);

public interface IJobStore
{
    Task UpsertJobAsync(DocumentJob job, CancellationToken cancellationToken = default);

    Task<DocumentJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default, string? tenantId = null);

    Task<IReadOnlyList<DocumentJob>> ListJobsByTenantAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>Searches one tenant using a server-compiled, bounded query plan.</summary>
    Task<DocumentQueryPage> SearchDocumentsAsync(
        string tenantId, DocumentQuery query, CancellationToken cancellationToken = default);
}

public interface IJobQueue
{
    ValueTask EnqueueAsync(JobQueueTicket ticket, CancellationToken cancellationToken = default);

    IAsyncEnumerable<JobQueueTicket> ReadAllAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryJobStore : IJobStore
{
    private readonly ConcurrentDictionary<string, DocumentJob> _jobs = new(StringComparer.OrdinalIgnoreCase);

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
        _jobs[job.JobId] = job;
        return Task.CompletedTask;
    }

    public Task<DocumentJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default, string? tenantId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        _jobs.TryGetValue(jobId, out var job);
        if (job is not null && !string.IsNullOrWhiteSpace(tenantId) &&
            !string.Equals(job.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<DocumentJob?>(null);
        }
        return Task.FromResult(job);
    }

    public Task<IReadOnlyList<DocumentJob>> ListJobsByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        IReadOnlyList<DocumentJob> list = _jobs.Values
            .Where(j => string.Equals(j.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.CreatedAtUtc)
            .ToList();
        return Task.FromResult(list);
    }

}

public sealed class ChannelJobQueue : IJobQueue
{
    private readonly Channel<JobQueueTicket> _channel = Channel.CreateUnbounded<JobQueueTicket>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    public ValueTask EnqueueAsync(JobQueueTicket ticket, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(ticket, cancellationToken);

    public IAsyncEnumerable<JobQueueTicket> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
