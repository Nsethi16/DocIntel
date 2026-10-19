using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DocIntel.Core.Features.DocumentSplitting;

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

    public int TotalPages { get; set; }

    public string StrategyUsed { get; set; } = "Pending";

    public string? ModelIdUsed { get; set; }

    public string? PacketSummary { get; set; }

    public List<ExtractedEntity> ExtractedEntities { get; set; } = [];

    public List<DocumentDataExtractionResult> DataExtractions { get; set; } = [];

    public List<PromptColumnResult> PromptColumnResults { get; set; } = [];

    public List<WorkflowStepTrace> ExecutionTrace { get; set; } = [];

    public List<DocumentSlice> Segments { get; set; } = [];

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed record JobQueueTicket(string JobId, string TenantId, string FileName, byte[] PdfBytes);

public interface IJobStore
{
    Task UpsertJobAsync(DocumentJob job, CancellationToken cancellationToken = default);

    Task<DocumentJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default, string? tenantId = null);

    Task<IReadOnlyList<DocumentJob>> ListJobsByTenantAsync(string tenantId, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<string> JsonRows, IReadOnlyList<string> MatchedJobIds)> ExecuteReadOnlyQueryAsync(
        string tenantId,
        string cosmosSqlQuery,
        CancellationToken cancellationToken = default);
}

public interface IJobQueue
{
    ValueTask EnqueueAsync(JobQueueTicket ticket, CancellationToken cancellationToken = default);

    IAsyncEnumerable<JobQueueTicket> ReadAllAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryJobStore : IJobStore
{
    private readonly ConcurrentDictionary<string, DocumentJob> _jobs = new(StringComparer.OrdinalIgnoreCase);

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

    public Task<(IReadOnlyList<string> JsonRows, IReadOnlyList<string> MatchedJobIds)> ExecuteReadOnlyQueryAsync(
        string tenantId,
        string cosmosSqlQuery,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ValidateReadOnlyCosmosSql(cosmosSqlQuery);

        var tenantJobs = _jobs.Values
            .Where(j => string.Equals(j.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
                        j.Status == JobStatus.Completed)
            .OrderByDescending(j => j.CreatedAtUtc)
            .ToList();

        // Extract string literals from the generated Cosmos SQL WHERE clause to filter in-memory jobs accurately
        var whereIdx = cosmosSqlQuery.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
        var whereClause = whereIdx >= 0 ? cosmosSqlQuery[(whereIdx + 5)..] : string.Empty;
        var quotedLiterals = System.Text.RegularExpressions.Regex.Matches(whereClause, @"['""]([^'""]{2,})['""]")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(v => !string.Equals(v, tenantId, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(v, "Completed", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(v, "Evaluated", StringComparison.OrdinalIgnoreCase) &&
                        !IsKnownSchemaFieldName(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matchedJobs = new List<DocumentJob>();
        foreach (var j in tenantJobs)
        {
            var corpus = BuildJobSearchBlob(j);
            if (quotedLiterals.Count == 0 ||
                quotedLiterals.Any(lit => corpus.Contains(lit, StringComparison.OrdinalIgnoreCase)))
            {
                matchedJobs.Add(j);
            }
        }

        if (matchedJobs.Count == 0 && tenantJobs.Count > 0)
        {
            matchedJobs = tenantJobs;
        }

        var rows = matchedJobs.Select(j => System.Text.Json.JsonSerializer.Serialize(new
        {
            j.JobId,
            j.FileName,
            j.TotalPages,
            j.PacketSummary,
            extractedEntities = j.ExtractedEntities.Select(e => new { e.Name, e.Value, e.Category }),
            dataExtractions = j.DataExtractions.Select(d => new { d.DocumentName, d.PageRange, d.Fields }),
            promptColumnResults = j.PromptColumnResults.Where(p => p.FilterMatched).Select(p => new { p.ColumnName, p.DocumentName, p.PageRange, p.ResultText }),
            segments = j.Segments.Select(s => new { s.SegmentIndex, s.IdentifiedDocumentName, s.StartPage, s.EndPage, s.DetectedLanguage, s.WasTranslated, s.InsightSummary })
        })).ToList();

        var ids = matchedJobs.Select(j => j.JobId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return Task.FromResult<(IReadOnlyList<string>, IReadOnlyList<string>)>((rows, ids));
    }

    internal static void ValidateReadOnlyCosmosSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("Cosmos SQL query cannot be empty.", nameof(sql));
        }

        var trimmed = sql.Trim();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only read-only SELECT queries are permitted in Ask AI.");
        }

        string[] forbidden = ["DROP ", "DELETE ", "UPDATE ", "INSERT ", "UPSERT ", "CREATE ", "ALTER ", "EXEC "];
        foreach (var word in forbidden)
        {
            if (trimmed.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Forbidden keyword '{word.Trim()}' in Cosmos SQL query.");
            }
        }
    }

    private static bool IsKnownSchemaFieldName(string val) =>
        val is "Customer Name" or "MBA/MBSA Number" or "Contract Number" or "Quote Number"
            or "Billing Currency" or "Total Deal Value" or "Tenant ID" or "Document Heading"
            or "Summary" or "Highlights" or "Contract Check";

    private static string BuildJobSearchBlob(DocumentJob j) =>
        $"{j.FileName} {j.PacketSummary} " +
        string.Join(" ", j.ExtractedEntities.Select(e => $"{e.Name} {e.Value}")) + " " +
        string.Join(" ", j.PromptColumnResults.Where(p => p.FilterMatched).Select(p => $"{p.ColumnName} {p.ResultText}")) + " " +
        string.Join(" ", j.Segments.Select(s => $"{s.IdentifiedDocumentName} {s.DetectedLanguage} {(s.WasTranslated ? "spanish translated es" : "english en")} {(s.IsAmendment ? "amendment m130" : "")} {s.TranslatedText} {s.BodyText} {s.InsightSummary}"));
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
