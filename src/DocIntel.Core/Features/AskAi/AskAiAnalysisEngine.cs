using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocIntel.Core.Features.AskAi;

/// <summary>Original, inspected text and a stable identity used to reject duplicate source records.</summary>
public sealed record AskAiAnalysisEvidence(string SourceText, string DocumentIdentity);

/// <summary>Validates source-backed rows and computes bounded, unit-separated analytical results.</summary>
public static class AskAiAnalysisEngine
{
    private static readonly HashSet<string> Operations = ["rows", "sum", "average", "count", "min", "max", "outliers"];
    private static readonly HashSet<string> DateFormats =
        ["yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "dd-MM-yyyy", "MM-dd-yyyy", "d MMM yyyy", "d MMMM yyyy"];

    /// <summary>No values or citations are accepted unless supported by an inspected original-source quotation.</summary>
    public static AskAiAnalysis Compute(
        string analysisId, AskAiAnalysisRequest request, IReadOnlyDictionary<string, AskAiAnalysisEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(evidence);
        if (String.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 160 ||
            !Operations.Contains(request.Operation) || request.Visualization is not ("table" or "bar" or "line") ||
            request.Columns is null || request.Columns.Count is < 1 or > 8 ||
            request.Rows is null || request.Rows.Count is < 1 or > 100)
            throw new ArgumentException("Analysis requires a bounded title, supported operation, 1-8 columns and 1-100 rows.");
        if (request.Columns.Any(column => column is null ||
                !Regex.IsMatch(column.Key, "^[a-z][a-z0-9_]{0,31}$", RegexOptions.CultureInvariant) ||
                column.Key is "constructor" or "prototype" ||
                String.IsNullOrWhiteSpace(column.Label) || column.Label.Length > 100 ||
                column.Kind is not ("text" or "number" or "date") ||
                column.NumberFormat is not ("dot-decimal" or "comma-decimal") ||
                !DateFormats.Contains(column.DateFormat)) ||
            request.Columns.Select(column => column.Key).Distinct(StringComparer.Ordinal).Count() != request.Columns.Count)
            throw new ArgumentException("Analysis columns require unique safe keys, labels, types and explicit parsing formats.");
        var groupColumn = request.Columns.FirstOrDefault(column => column.Key == request.GroupBy);
        var valueColumn = request.Columns.FirstOrDefault(column => column.Key == request.ValueColumn);
        if (request.GroupBy is not null && groupColumn is null ||
            request.ValueColumn is not null && valueColumn is null ||
            request.Operation is not ("rows" or "count") && valueColumn?.Kind != "number" ||
            request.DateBucket is not ("none" or "month" or "quarter" or "year") ||
            request.DateBucket != "none" && groupColumn?.Kind != "date" ||
            request.Visualization == "line" && groupColumn?.Kind != "date" ||
            request.Operation == "rows" && request.Visualization != "table")
            throw new ArgumentException("Aggregation and charts require compatible grouping, date and numeric columns.");

        var records = new List<SourceRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;
        var missing = 0;
        var allEvidence = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in request.Rows)
        {
            if (row is null || !evidence.TryGetValue(row.EvidenceId, out var source) ||
                String.IsNullOrWhiteSpace(row.SourceQuote) || row.SourceQuote.Length > 2_000 ||
                row.Cells is null || row.Cells.Keys.Any(key => !request.Columns.Exists(column => column.Key == key)))
                throw new ArgumentException("Each record requires inspected evidence, an exact source quote and declared cells.");
            var quote = Normalize(row.SourceQuote);
            var text = Normalize(source.SourceText);
            var quoteOffset = text.IndexOf(quote, StringComparison.OrdinalIgnoreCase);
            if (quoteOffset < 0 || text.IndexOf(quote, quoteOffset + 1, StringComparison.OrdinalIgnoreCase) >= 0)
                throw new ArgumentException("A record quote must identify one occurrence in inspected original text.");
            var unit = Normalize(row.Unit ?? String.Empty);
            if (unit.Length > 64 || unit is "$" or "\u20ac" or "\u00a3" ||
                unit.Length > 0 && !ContainsValue(quote, unit, false))
                throw new ArgumentException("Use an explicit source-supported unit; ambiguous currency symbols cannot be aggregated.");
            var cells = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var column in request.Columns)
            {
                row.Cells.TryGetValue(column.Key, out var raw);
                if (String.IsNullOrWhiteSpace(raw))
                {
                    cells[column.Key] = null;
                    continue;
                }
                raw = Normalize(raw);
                if (raw.Length > 500 || !ContainsValue(quote, raw, column.Kind == "number"))
                    throw new ArgumentException("Every supplied cell must occur in that record's original-source quote.");
                cells[column.Key] = column.Kind switch
                {
                    "number" => ParseNumber(raw, unit, column.NumberFormat),
                    "date" => ParseDate(raw, column.DateFormat),
                    _ => raw
                };
            }
            var key = source.DocumentIdentity + "\n" +
                JsonSerializer.Serialize(cells.OrderBy(cell => cell.Key, StringComparer.Ordinal)) + "\n" + unit;
            if (!seen.Add(key))
            {
                duplicates++;
                continue;
            }
            allEvidence.Add(row.EvidenceId);
            var category = groupColumn is null ? "All Records" : cells[groupColumn.Key] is null
                ? null : Convert.ToString(cells[groupColumn.Key], CultureInfo.InvariantCulture);
            if (category is not null && groupColumn?.Kind == "date")
                category = BucketDate(category, request.DateBucket);
            decimal? number = valueColumn is not null && cells[valueColumn.Key] is decimal value ? value : null;
            if (request.Operation != "rows" && (category is null ||
                request.Operation != "count" && (number is null || unit.Length == 0)))
            {
                missing++;
                continue;
            }
            records.Add(new SourceRecord(cells, category ?? String.Empty, unit, number, row.EvidenceId));
        }

        var notes = new List<string>
        {
            "Computed from cited, inspected source records; this is not a certified complete-workspace total."
        };
        var rows = new List<AskAiAnalysisRow>();
        List<AskAiAnalysisColumn> columns;
        if (request.Operation == "rows")
        {
            columns = request.Columns.Select(column =>
                new AskAiAnalysisColumn(column.Key, column.Label, column.Kind)).ToList();
            rows = records.Select(record => new AskAiAnalysisRow
            {
                Cells = record.Cells, EvidenceIds = [record.EvidenceId]
            }).ToList();
            missing = records.Count(record => record.Cells.Values.Any(value => value is null));
        }
        else
        {
            columns =
            [
                new("category", groupColumn?.Label ?? "Group", groupColumn?.Kind == "date" ? "date" : "text"),
                new("unit", "Unit", "text"),
                new("value", request.Operation == "count" ? "Count" : valueColumn!.Label, "number"),
                new("records", "Records", "number")
            ];
            if (request.Operation == "outliers")
            {
                notes.Add("Outliers use Tukey's 1.5xIQR rule within each unit, with at least four source records.");
                foreach (var group in records.GroupBy(record => record.Unit, StringComparer.OrdinalIgnoreCase))
                {
                    if (group.Count() < 4)
                    {
                        notes.Add($"Insufficient records for outlier detection in {group.Key}.");
                        continue;
                    }
                    var values = group.Select(record => record.Number!.Value).Order().ToArray();
                    var firstQuartile = Quantile(values, 0.25m);
                    var thirdQuartile = Quantile(values, 0.75m);
                    var spread = (thirdQuartile - firstQuartile) * 1.5m;
                    foreach (var record in group.Where(record =>
                        record.Number < firstQuartile - spread || record.Number > thirdQuartile + spread))
                        rows.Add(ResultRow(record.Category, record.Unit, record.Number!.Value, [record]));
                }
                if (rows.Count == 0) notes.Add("No outliers were identified by this rule in the usable records.");
            }
            else
            {
                foreach (var group in records.GroupBy(record =>
                    (record.Category, Unit: request.Operation == "count" ? "records" : record.Unit)))
                {
                    var values = group.Select(record => record.Number ?? 0m).ToArray();
                    var value = request.Operation switch
                    {
                        "count" => group.Count(),
                        "sum" => values.Sum(),
                        "average" => values.Average(),
                        "min" => values.Min(),
                        "max" => values.Max(),
                        _ => throw new ArgumentException("Unsupported analytical operation.")
                    };
                    rows.Add(ResultRow(group.Key.Category, group.Key.Unit, value, group));
                }
            }
            rows = groupColumn?.Kind == "date"
                ? rows.OrderBy(row => (string)row.Cells["category"]!, StringComparer.Ordinal).ToList()
                : rows.OrderByDescending(row => (decimal)row.Cells["value"]!).ToList();
        }
        var visualization = request.Visualization;
        if (rows.Count == 0 || rows.Count > 40 ||
            visualization == "line" && rows.Select(row => row.Cells["unit"]?.ToString())
                .Distinct(StringComparer.Ordinal).Count() != 1)
        {
            if (visualization != "table") notes.Add("A table is used because the chart size or unit constraints are not met.");
            visualization = "table";
        }
        if (visualization == "line" && rows.Count > 1)
        {
            var first = (decimal)rows[0].Cells["value"]!;
            var last = (decimal)rows[^1].Cells["value"]!;
            var difference = last - first;
            notes.Add($"First-to-last change: {difference.ToString("0.####", CultureInfo.InvariantCulture)} " +
                $"{rows[0].Cells["unit"]}" + (first == 0 ? "; percentage change is undefined from zero."
                    : $" ({((difference / Math.Abs(first)) * 100).ToString("0.##", CultureInfo.InvariantCulture)}%)."));
        }
        if (missing > 0) notes.Add($"{missing} records contain missing required values or units; no missing value became zero.");
        if (duplicates > 0) notes.Add($"{duplicates} duplicate source records were excluded.");
        return new AskAiAnalysis
        {
            AnalysisId = analysisId, Title = request.Title, Operation = request.Operation,
            Visualization = visualization, Columns = columns, Rows = rows, Notes = notes,
            XColumn = request.Operation == "rows" ? null : "category",
            YColumn = request.Operation == "rows" ? null : "value",
            EvidenceIds = allEvidence.ToList(),
            Coverage = new() { RecordsIncluded = records.Count, MissingValues = missing, DuplicatesExcluded = duplicates }
        };
    }

    private static AskAiAnalysisRow ResultRow(
        string category, string unit, decimal value, IEnumerable<SourceRecord> records) => new()
    {
        Cells = new(StringComparer.Ordinal)
        {
            ["category"] = category, ["unit"] = unit, ["value"] = value, ["records"] = records.Count()
        },
        EvidenceIds = records.Select(record => record.EvidenceId).Distinct(StringComparer.Ordinal).ToList()
    };

    private static decimal Quantile(decimal[] values, decimal quantile)
    {
        var position = (values.Length - 1) * quantile;
        var lower = (int)Decimal.Floor(position);
        var upper = (int)Decimal.Ceiling(position);
        return values[lower] + (values[upper] - values[lower]) * (position - lower);
    }

    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    private static bool ContainsValue(string text, string value, bool numeric)
    {
        var index = -1;
        while ((index = text.IndexOf(value, index + 1, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = index > 0 ? text[index - 1] : ' ';
            var after = index + value.Length < text.Length ? text[index + value.Length] : ' ';
            if ((!Char.IsLetterOrDigit(value[0]) || !Char.IsLetterOrDigit(before)) &&
                (!Char.IsLetterOrDigit(value[^1]) || !Char.IsLetterOrDigit(after)) &&
                (!numeric || before is not ('.' or ',') && after is not ('.' or ',')))
                return true;
        }
        return false;
    }

    private static decimal ParseNumber(string raw, string unit, string format)
    {
        var value = unit.Length == 0 ? raw : raw.Replace(unit, String.Empty, StringComparison.OrdinalIgnoreCase);
        value = new string(value.Where(character =>
            Char.GetUnicodeCategory(character) != UnicodeCategory.CurrencySymbol && character != '%').ToArray()).Trim();
        if (value.StartsWith('(') && value.EndsWith(')')) value = "-" + value[1..^1];
        var pattern = format == "dot-decimal"
            ? @"^[+-]?(?:\d{1,3}(?:[ ,]\d{3})+|\d+)(?:\.\d+)?$"
            : @"^[+-]?(?:\d{1,3}(?:[ .]\d{3})+|\d+)(?:,\d+)?$";
        if (!Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant))
            throw new ArgumentException("A numeric cell does not match its explicit decimal/grouping format.");
        var culture = format == "dot-decimal" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("de-DE");
        if (!Decimal.TryParse(value.Replace(" ", String.Empty, StringComparison.Ordinal),
            NumberStyles.Number, culture, out var number))
            throw new ArgumentException("A numeric cell exceeds the supported decimal range.");
        return number;
    }

    private static string ParseDate(string raw, string format) =>
        DateOnly.TryParseExact(raw, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : throw new ArgumentException("A date cell does not match its explicit date format.");

    private static string BucketDate(string value, string bucket)
    {
        var date = DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return bucket switch
        {
            "month" => new DateOnly(date.Year, date.Month, 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "quarter" => new DateOnly(date.Year, ((date.Month - 1) / 3) * 3 + 1, 1)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "year" => new DateOnly(date.Year, 1, 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => value
        };
    }

    private sealed record SourceRecord(
        Dictionary<string, object?> Cells, string Category, string Unit, decimal? Number, string EvidenceId);
}
