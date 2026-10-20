using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using DocIntel.Core.Authorization;
using DocIntel.Core.Jobs;
using DocIntel.Core.Review;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace DocIntel.Core.Export;

/// <summary>Flattened typed values with explicit original/effective and source/review distinctions.</summary>
public sealed record ExportField(
    string FieldId, string Label, int SliceIndex, string OriginalValue, string EffectiveValue,
    string Outcome, double? OriginalModelScore, string OriginalSourceStatus, string OriginalSourcePages,
    bool EffectiveValueMatchesOriginal);

/// <summary>An authorized document snapshot; review approval never implicitly changes original extraction data.</summary>
public sealed record DocumentExport(
    string ExportId, string TenantId, string JobId, string FileName, int HarnessVersion,
    string? OriginalSha256, string View, string ReviewStatus, long? ReviewVersion,
    bool? HasDevelopmentResults, bool? HasDevelopmentReview,
    DateTimeOffset CreatedAtUtc, IReadOnlyList<ExportField> Fields);

/// <summary>Reference-only application audit metadata written before export bytes are released.</summary>
public sealed record ExportAuditEvent(
    string ExportId, string TenantId, string JobId, ActorIdentity? Actor, string Format,
    string View, long? ReviewVersion, string? OriginalSha256, string ContentSha256,
    int FieldCount, DateTimeOffset AtUtc, bool DevelopmentIdentity);

/// <summary>Signals explicit output limits rather than silently truncating exported field values.</summary>
public sealed class ExportLimitException(string message) : InvalidOperationException(message);

/// <summary>Produces formula-safe spreadsheet artifacts and source-bound JSON without fetching external destinations.</summary>
public static class DocumentExporter
{
    private static readonly string[] Headers =
    [
        "JobId", "FileName", "FieldId", "Label", "SliceIndex", "OriginalValue", "EffectiveValue",
        "ReviewOutcome", "OriginalModelScoreUncalibrated", "OriginalSourceStatus", "OriginalSourcePages",
        "EffectiveValueMatchesOriginal", "View", "ReviewStatus", "ReviewVersion", "OriginalSha256", "HarnessVersion",
        "HasDevelopmentResults", "HasDevelopmentReview"
    ];
    private const int MaximumBytes = 16 * 1024 * 1024;

    public static DocumentExport Snapshot(DocumentJob job, ReviewSession? review, bool original, DateTimeOffset now)
    {
        if (job.Status != JobStatus.Completed) throw new ReviewConflictException("Complete processing before exporting.");
        if (!original && (review?.Status != ReviewStatus.Approved || review.JobId != job.JobId ||
            review.TenantId != job.TenantId || review.Id != job.ReviewId ||
            review.SourceFingerprint != ReviewEngine.Fingerprint(job)))
            throw new ReviewConflictException("Complete source-matching human review before exporting effective values.");
        var fields = original ? ReviewEngine.Fields(job) : review!.Fields;
        if (fields.Sum(field => (long)field.OriginalValue.Length + field.CurrentValue.Length + field.Label.Length) > 4_000_000)
            throw new ExportLimitException("The requested export exceeds the supported text size; narrow the document.");
        return new DocumentExport(Guid.NewGuid().ToString("N"), job.TenantId, job.JobId, job.FileName,
            job.AppliedHarnessVersion, job.OriginalSha256, original ? "OriginalUnreviewed" : "HumanReviewed",
            original ? "UnreviewedSource" : review!.Status.ToString(), original ? null : review!.Version,
            job.HasDevelopmentResults, original ? null : review!.HasDevelopmentActivity, now,
            fields.Select(field => new ExportField(field.Id, field.Label, field.SliceIndex,
                field.OriginalValue, field.CurrentValue, original ? "Unreviewed" : field.Outcome.ToString(),
                field.ConfidenceSignals?.ModelScore, field.OriginalProvenance?.Status.ToString() ?? "Unavailable",
                String.Join(",", field.OriginalProvenance?.Boxes.Select(box => box.Page).Distinct() ?? []),
                field.CurrentValue == field.OriginalValue)).ToList());
    }

    public static byte[] Write(DocumentExport export, string format)
    {
        var bytes = format switch
        {
            "json" => JsonSerializer.SerializeToUtf8Bytes(export, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            "csv" => Csv(export),
            "xlsx" => Workbook(export),
            _ => throw new ArgumentException("Supported export formats are csv, xlsx and json.")
        };
        if (bytes.Length > MaximumBytes) throw new ExportLimitException("The generated export exceeds 16 MiB.");
        return bytes;
    }

    public static string CsvCell(string value)
    {
        if (value.Contains('\0')) throw new ExportLimitException("CSV cannot safely represent embedded null characters.");
        var first = 0;
        while (first < value.Length && (Char.IsWhiteSpace(value[first]) || Char.IsControl(value[first]) ||
            CharUnicodeInfo.GetUnicodeCategory(value, first) == UnicodeCategory.Format)) first++;
        if (value.Length > 0 && Char.IsControl(value[0]) ||
            first < value.Length && value[first] is '=' or '+' or '-' or '@')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static IEnumerable<string[]> Rows(DocumentExport export) => export.Fields.Select(field => new[]
    {
        export.JobId, export.FileName, field.FieldId, field.Label,
        field.SliceIndex.ToString(CultureInfo.InvariantCulture), field.OriginalValue, field.EffectiveValue,
        field.Outcome, field.OriginalModelScore?.ToString(CultureInfo.InvariantCulture) ?? String.Empty,
        field.OriginalSourceStatus, field.OriginalSourcePages, field.EffectiveValueMatchesOriginal ? "true" : "false",
        export.View, export.ReviewStatus, export.ReviewVersion?.ToString(CultureInfo.InvariantCulture) ?? String.Empty,
        export.OriginalSha256 ?? String.Empty, export.HarnessVersion.ToString(CultureInfo.InvariantCulture),
        export.HasDevelopmentResults?.ToString() ?? "Unknown", export.HasDevelopmentReview?.ToString() ?? "Unknown"
    });

    private static byte[] Csv(DocumentExport export)
    {
        var text = new StringBuilder();
        text.Append(String.Join(",", Headers.Select(CsvCell))).Append("\r\n");
        foreach (var row in Rows(export)) text.Append(String.Join(",", row.Select(CsvCell))).Append("\r\n");
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(text.ToString())).ToArray();
    }

    private static byte[] Workbook(DocumentExport export)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: true))
        {
            var book = document.AddWorkbookPart();
            book.Workbook = new Workbook();
            var part = book.AddNewPart<WorksheetPart>();
            var data = new SheetData();
            part.Worksheet = new Worksheet(data);
            Row Row(string[] values) => new(values.Select(value =>
            {
                if (value.Length > 32_767 || value.Contains('\0'))
                    throw new ExportLimitException("An Excel cell exceeds its text limit. Use JSON instead.");
                try { XmlConvert.VerifyXmlChars(value); }
                catch (XmlException) { throw new ExportLimitException("An Excel cell contains invalid XML characters. Use JSON instead."); }
                return new Cell
                {
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve })
                };
            }));
            data.Append(Row(Headers));
            foreach (var row in Rows(export)) data.Append(Row(row));
            book.Workbook.Append(new Sheets(new Sheet { Id = book.GetIdOfPart(part), SheetId = 1, Name = "Fields" }));
        }
        return stream.ToArray();
    }
}
