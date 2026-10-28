using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Jobs;

namespace DocIntel.Api.Endpoints;

/// <summary>Filters already-authorized records and bounds response size, not the underlying store scan.</summary>
internal static class DocumentListProjection
{
    internal static string? Validate(HttpRequest request, int? offset, int? limit, string? query, string? status)
    {
        if (new[] { "offset", "limit", "q", "status" }.Any(name => request.Query[name].Count > 1))
            return "Supply each document query parameter only once.";
        if (offset is < 0) return "Offset must be nonnegative.";
        if (limit is < 1 or > 100) return "Limit must be between 1 and 100.";
        if (query?.Length > 100 || query?.Any(Char.IsControl) == true)
            return "Search must contain at most 100 characters and no control characters.";
        if (!String.IsNullOrEmpty(status) &&
            !Enum.GetNames<JobStatus>().Contains(status, StringComparer.OrdinalIgnoreCase))
            return "Status must be a named document job status.";
        return null;
    }

    internal static object Create(
        string tenantId, IReadOnlyList<DocumentJob> visibleJobs, int offset, int limit, string? query, string? status)
    {
        var term = query?.Trim();
        var selectedStatus = String.IsNullOrEmpty(status) ? (JobStatus?)null : Enum.Parse<JobStatus>(status, true);
        var matches = visibleJobs.Where(job =>
                String.Equals(job.TenantId, tenantId, StringComparison.Ordinal) && job.Status != JobStatus.Deleted &&
                (selectedStatus is null || job.Status == selectedStatus) &&
                (String.IsNullOrEmpty(term) || SearchableText(job).Any(text =>
                    text?.Contains(term, StringComparison.OrdinalIgnoreCase) == true)))
            .OrderByDescending(job => job.CreatedAtUtc).ThenBy(job => job.JobId, StringComparer.Ordinal).ToList();
        var items = matches.Skip(offset).Take(limit).Select(job => new
        {
            job.JobId,
            job.TenantId,
            job.FileName,
            status = job.Status.ToString(),
            job.TotalPages,
            job.CreatedAtUtc,
            job.ErrorMessage,
            executionTrace = job.ExecutionTrace.Take(64).Select(step => new
            {
                step.ExecutorName, step.Executed, step.Succeeded, step.ModelIdUsed, step.DurationMs
            }),
            job.HasDevelopmentResults,
            segmentCount = job.Segments.Count
        }).ToArray();
        return new
        {
            items,
            total = matches.Count,
            nextOffset = (long)offset + items.Length < matches.Count ? (int?)(offset + items.Length) : null
        };
    }

    private static IEnumerable<string?> SearchableText(DocumentJob job)
    {
        yield return job.FileName;
        yield return job.JobId;
        yield return job.PacketSummary;
        foreach (var entity in job.ExtractedEntities)
            yield return $"{entity.Name} {entity.Value}";
        foreach (var extraction in job.DataExtractions)
            foreach (var text in ExtractionText(extraction)) yield return text;
        foreach (var result in job.PromptColumnResults)
            yield return $"{result.ColumnName} {result.ResultText}";
        foreach (var slice in job.Segments)
        {
            yield return slice.IdentifiedDocumentName;
            yield return slice.DocumentTypeName;
            yield return slice.Markdown;
            yield return slice.BodyText;
            yield return slice.TranslatedText;
            yield return slice.InsightSummary;
            yield return String.Join(" ", slice.Footers);
            foreach (var entity in slice.ExtractedEntities)
                yield return $"{entity.Name} {entity.Value}";
            if (slice.DataExtraction is not null)
                foreach (var text in ExtractionText(slice.DataExtraction)) yield return text;
            foreach (var result in slice.PromptColumnResults)
                yield return $"{result.ColumnName} {result.ResultText}";
        }
    }

    private static IEnumerable<string> ExtractionText(DocumentDataExtractionResult result)
    {
        yield return result.DocumentName;
        foreach (var field in result.Fields)
            yield return $"{field.FieldName} {field.FieldValue}";
        foreach (var table in result.Tables)
        {
            yield return table.TableName;
            foreach (var row in table.Rows)
                foreach (var field in row.Fields)
                    yield return $"{field.FieldName} {field.FieldValue}";
        }
    }
}
