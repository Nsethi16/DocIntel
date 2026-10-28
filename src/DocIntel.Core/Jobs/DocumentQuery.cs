using System.Text;
using System.Text.Json.Serialization;

namespace DocIntel.Core.Jobs;

/// <summary>
/// An exact entity condition; multiple conditions are combined with AND across the document's slices.
/// </summary>
public sealed record DocumentEntityCondition(string Name, string Value);

/// <summary>
/// A bounded, read-only query plan. Tenant identity and query syntax are never supplied by the model.
/// </summary>
public sealed class DocumentQuery
{
    [JsonIgnore]
    internal IReadOnlySet<string>? AuthorizedJobIds { get; init; }

    /// <summary>Applies a server-owned document scope without adding model-controllable arguments.</summary>
    public DocumentQuery WithAccess(IReadOnlySet<string>? jobIds) => new()
    {
        SearchText = SearchText,
        DocumentType = DocumentType,
        Entities = Entities,
        PageSize = PageSize,
        ContinuationToken = ContinuationToken,
        AuthorizedJobIds = jobIds
    };

    public string? SearchText { get; init; }

    public string? DocumentType { get; init; }

    public List<DocumentEntityCondition> Entities { get; init; } = [];

    public int PageSize { get; init; } = 10;

    public string? ContinuationToken { get; init; }

    /// <summary>Rejects oversized or invalid tool arguments before accessing storage.</summary>
    public void Validate()
    {
        if (PageSize is < 1 or > 20 || Entities is null || Entities.Count > 10)
        {
            throw new ArgumentException("Search requires a page size of 1-20 and at most 10 entity conditions.");
        }

        if (SearchText?.Length > 200 || DocumentType?.Length > 200 || ContinuationToken?.Length > 16_384)
        {
            throw new ArgumentException("A document search argument exceeds the supported length.");
        }

        if (Entities.Any(e => e is null || String.IsNullOrWhiteSpace(e.Name) ||
                              String.IsNullOrWhiteSpace(e.Value) || e.Name.Length > 200 || e.Value.Length > 500))
        {
            throw new ArgumentException("Each entity condition requires a bounded name and value.");
        }
    }
}

/// <summary>A document-list projection without document text or PDF bytes.</summary>
public sealed record DocumentReference(string JobId, string FileName, int TotalPages, int SliceCount);

/// <summary>A single search page; a non-null continuation token means more results may exist.</summary>
public sealed record DocumentQueryPage(
    IReadOnlyList<DocumentReference> Documents,
    string? ContinuationToken);

/// <summary>Server-owned Cosmos query text and bound parameters.</summary>
public sealed record CompiledDocumentQuery(string Sql, IReadOnlyDictionary<string, object> Parameters);

/// <summary>Compiles the supported search plan into parameterized Cosmos SQL.</summary>
public static class DocumentQueryCompiler
{
    public static CompiledDocumentQuery Compile(string tenantId, DocumentQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();

        var sql = new StringBuilder(
            "SELECT c.jobId, c.fileName, c.totalPages, ARRAY_LENGTH(c.segments) AS sliceCount " +
            "FROM c WHERE c.tenantId = @tenantId AND c.status = @status");
        var parameters = new Dictionary<string, object>
        {
            ["@tenantId"] = tenantId,
            ["@status"] = nameof(JobStatus.Completed)
        };

        if (query.AuthorizedJobIds is not null)
        {
            sql.Append(" AND ARRAY_CONTAINS(@authorizedJobIds, c.jobId)");
            parameters["@authorizedJobIds"] = query.AuthorizedJobIds.ToArray();
        }

        if (!String.IsNullOrWhiteSpace(query.DocumentType))
        {
            sql.Append(" AND EXISTS(SELECT VALUE s FROM s IN c.segments " +
                       "WHERE STRINGEQUALS(s.identifiedDocumentName, @documentType, true))");
            parameters["@documentType"] = query.DocumentType;
        }

        if (!String.IsNullOrWhiteSpace(query.SearchText))
        {
            sql.Append(" AND (CONTAINS(c.fileName, @text, true) OR CONTAINS(c.packetSummary, @text, true) OR EXISTS(" +
                       "SELECT VALUE s FROM s IN c.segments WHERE " +
                       "CONTAINS(s.bodyText, @text, true) OR CONTAINS(s.markdown, @text, true) OR " +
                       "CONTAINS(s.translatedText, @text, true) OR CONTAINS(s.insightSummary, @text, true)) OR " +
                       "EXISTS(SELECT VALUE p FROM p IN c.promptColumnResults " +
                       "WHERE p.filterMatched = true AND CONTAINS(p.resultText, @text, true)))");
            parameters["@text"] = query.SearchText;
        }

        for (var i = 0; i < query.Entities.Count; i++)
        {
            sql.Append($" AND EXISTS(SELECT VALUE s FROM s IN c.segments JOIN e IN s.extractedEntities " +
                       $"WHERE STRINGEQUALS(e.name, @name{i}, true) AND STRINGEQUALS(e.value, @value{i}, true))");
            parameters[$"@name{i}"] = query.Entities[i].Name;
            parameters[$"@value{i}"] = query.Entities[i].Value;
        }

        sql.Append(" ORDER BY c.createdAtUtc DESC");
        return new CompiledDocumentQuery(sql.ToString(), parameters);
    }

    /// <summary>Applies the same supported plan to the development store without interpreting SQL.</summary>
    public static bool Matches(DocumentJob job, string tenantId, DocumentQuery query)
    {
        if (!String.Equals(job.TenantId, tenantId, StringComparison.Ordinal) ||
            (query.AuthorizedJobIds is not null && !query.AuthorizedJobIds.Contains(job.JobId)) ||
            job.Status != JobStatus.Completed)
        {
            return false;
        }

        if (!String.IsNullOrWhiteSpace(query.DocumentType) &&
            !job.Segments.Any(s => String.Equals(
                s.IdentifiedDocumentName, query.DocumentType, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (query.Entities.Any(condition => !job.Segments.Any(s => s.ExtractedEntities.Any(e =>
                String.Equals(e.Name, condition.Name, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(e.Value, condition.Value, StringComparison.OrdinalIgnoreCase)))))
        {
            return false;
        }

        if (String.IsNullOrWhiteSpace(query.SearchText))
        {
            return true;
        }

        var text = query.SearchText;
        return job.FileName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               (job.PacketSummary?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
               job.Segments.Any(s =>
                   s.BodyText.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                   s.Markdown.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                   (s.TranslatedText?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (s.InsightSummary?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)) ||
               job.PromptColumnResults.Any(p =>
                   p.FilterMatched && p.ResultText.Contains(text, StringComparison.OrdinalIgnoreCase));
    }
}
