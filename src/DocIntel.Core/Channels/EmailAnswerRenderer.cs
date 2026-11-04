using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocIntel.Core.Features.AskAi;

namespace DocIntel.Core.Channels;

/// <summary>Renders answer-only, encoded HTML with Outlook-compatible analytical tables and bars.</summary>
public static class EmailAnswerRenderer
{
    public const int MaximumBodyCharacters = 128_000;
    private const string BodyStyle =
        "font-family:Segoe UI,Arial,sans-serif;font-size:15px;line-height:1.55;color:#172033;max-width:760px;";
    private const string CellStyle =
        "padding:9px 10px;border-bottom:1px solid #dce3ed;vertical-align:top;word-break:break-word;";
    private static readonly Regex InlineTokens = new(
        @"`[^`\r\n]+`|\*\*[^*\r\n]+\*\*|\*[^*\r\n]+\*|\[[0-9]{1,3}\]",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ListItem = new(
        @"^(?:[-*+] |[0-9]{1,3}[.)] )(?<text>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex TableDivider = new(
        @"^:?-{3,}:?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Uses only server-built application links; model/source content can never supply HTML or URLs.</summary>
    public static string Render(AskAiRun run, string? publicOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (String.IsNullOrWhiteSpace(run.Answer))
            throw new InvalidOperationException("A completed answer is required.");
        if (publicOrigin is not null &&
            (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin) ||
             origin.Scheme != "https" || !origin.IsDefaultPort || origin.AbsolutePath != "/" ||
             origin.UserInfo.Length > 0 || origin.Query.Length > 0 || origin.Fragment.Length > 0))
            throw new ArgumentException("Email links require the configured HTTPS application origin.", nameof(publicOrigin));
        var result = Build(run, publicOrigin?.TrimEnd('/'), compact: false);
        if (result.Length <= MaximumBodyCharacters) return result;
        result = Build(run, publicOrigin?.TrimEnd('/'), compact: true);
        if (result.Length > MaximumBodyCharacters)
            throw new InvalidDataException("The email answer exceeds the supported message size.");
        return result;
    }

    private static string Build(AskAiRun run, string? origin, bool compact)
    {
        var html = new StringBuilder("<div style=\"" + BodyStyle + "\">");
        var citations = run.Citations.Select(citation => citation.CitationNumber).ToHashSet();
        AppendMarkdown(html, compact ? Shorten(run.Answer!, 8_000) : run.Answer!, citations);
        foreach (var analysis in run.Analyses.Take(4))
            AppendAnalysis(html, analysis, citations, compact);
        if (compact)
            html.Append("<p><em>Email preview shortened to fit message limits. Open the full answer for all text and rows.</em></p>");
        if (run.Citations.Count > 0)
        {
            html.Append("<h2 style=\"font-size:17px;margin:24px 0 10px;\">Sources</h2><ol style=\"padding-left:22px;\">");
            foreach (var citation in run.Citations.Take(48))
            {
                var job = Uri.EscapeDataString(citation.JobId);
                var tenant = Uri.EscapeDataString(run.TenantId);
                var pdf = origin + $"/api/harness/tenants/{tenant}/jobs/{job}/slices/" +
                    citation.SliceIndex.ToString(CultureInfo.InvariantCulture) + "/pdf";
                var workspace = origin + $"/?tenant={tenant}&job={job}";
                html.Append("<li id=\"source-").Append(citation.CitationNumber.ToString(CultureInfo.InvariantCulture))
                    .Append("\" value=\"").Append(citation.CitationNumber.ToString(CultureInfo.InvariantCulture))
                    .Append("\" style=\"margin-bottom:10px;font-size:13px;\">")
                    .Append(Encode(Shorten(citation.FileName, 300))).Append(" &mdash; ")
                    .Append(Encode(Shorten(citation.DocumentName, 200))).Append(", ")
                    .Append(Encode(citation.PageRange)).Append("<br>");
                AppendLink(html, pdf, "Open PDF");
                html.Append(" &middot; ");
                AppendLink(html, workspace, "Open in Workspace");
                html.Append("</li>");
            }
            html.Append("</ol>");
        }
        html.Append("<p style=\"margin:20px 0 0;font-size:13px;\">");
        AppendLink(html, origin + "/ask-run.html?tenant=" + Uri.EscapeDataString(run.TenantId) +
            "&run=" + Uri.EscapeDataString(run.RunId), "View full answer and original sources");
        return html.Append(" (sign-in required)</p></div>").ToString();
    }

    private static void AppendMarkdown(StringBuilder html, string text, HashSet<int> citations)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var code = new StringBuilder();
                while (++index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    code.AppendLine(lines[index]);
                html.Append("<pre style=\"font-family:Consolas,monospace;font-size:13px;white-space:pre-wrap;")
                    .Append("word-break:break-word;background:#f3f6fa;padding:12px;\">")
                    .Append(Encode(code.ToString())).Append("</pre>");
                continue;
            }
            if (index + 1 < lines.Length && line.Contains('|') &&
                TableCells(lines[index + 1]) is { Length: >= 2 and <= 9 } dividers &&
                dividers.All(cell => TableDivider.IsMatch(cell)) &&
                TableCells(line) is { } headings && headings.Length == dividers.Length)
            {
                BeginTable(html);
                html.Append("<thead><tr>");
                foreach (var heading in headings) AppendCell(html, heading, citations, header: true, markdown: true);
                html.Append("</tr></thead><tbody>");
                index += 2;
                var count = 0;
                while (index < lines.Length && lines[index].Contains('|') &&
                    TableCells(lines[index]) is { } cells && cells.Length == headings.Length)
                {
                    if (count++ < 100)
                    {
                        html.Append("<tr>");
                        foreach (var cell in cells) AppendCell(html, cell, citations, markdown: true);
                        html.Append("</tr>");
                    }
                    index++;
                }
                html.Append("</tbody></table>");
                if (count > 100) html.Append("<p><em>Showing 100 table rows; open the full answer for the remainder.</em></p>");
                index--;
                continue;
            }
            var headingLength = line.TakeWhile(character => character == '#').Count();
            if (headingLength is >= 1 and <= 6 && line.Length > headingLength && line[headingLength] == ' ')
            {
                html.Append("<h2 style=\"font-size:18px;margin:22px 0 10px;\">");
                AppendInline(html, line[(headingLength + 1)..], citations);
                html.Append("</h2>");
                continue;
            }
            if (ListItem.IsMatch(line))
            {
                var ordered = Char.IsAsciiDigit(line[0]);
                html.Append(ordered ? "<ol style=\"padding-left:24px;\">" : "<ul style=\"padding-left:24px;\">");
                do
                {
                    html.Append("<li style=\"margin-bottom:6px;\">");
                    AppendInline(html, ListItem.Match(lines[index].Trim()).Groups["text"].Value, citations);
                    html.Append("</li>");
                    index++;
                } while (index < lines.Length && ListItem.IsMatch(lines[index].Trim()) &&
                    Char.IsAsciiDigit(lines[index].Trim()[0]) == ordered);
                html.Append(ordered ? "</ol>" : "</ul>");
                index--;
                continue;
            }
            html.Append("<p style=\"margin:0 0 12px;\">");
            AppendInline(html, line, citations);
            while (index + 1 < lines.Length && lines[index + 1].Trim().Length > 0 &&
                !lines[index + 1].TrimStart().StartsWith('#') &&
                !lines[index + 1].TrimStart().StartsWith("```", StringComparison.Ordinal) &&
                !ListItem.IsMatch(lines[index + 1].Trim()) && !lines[index + 1].Contains('|'))
            {
                html.Append("<br>");
                AppendInline(html, lines[++index].Trim(), citations);
            }
            html.Append("</p>");
        }
    }

    private static void AppendInline(StringBuilder html, string text, HashSet<int> citations)
    {
        var offset = 0;
        foreach (Match token in InlineTokens.Matches(text))
        {
            html.Append(Encode(text[offset..token.Index]));
            if (token.Value[0] == '`')
                html.Append("<code>").Append(Encode(token.Value[1..^1])).Append("</code>");
            else if (token.Value.StartsWith("**", StringComparison.Ordinal))
                html.Append("<strong>").Append(Encode(token.Value[2..^2])).Append("</strong>");
            else if (token.Value[0] == '*')
                html.Append("<em>").Append(Encode(token.Value[1..^1])).Append("</em>");
            else if (Int32.TryParse(token.Value.AsSpan(1, token.Length - 2),
                NumberStyles.None, CultureInfo.InvariantCulture, out var number) && citations.Contains(number))
                AppendLink(html, "#source-" + number.ToString(CultureInfo.InvariantCulture), token.Value);
            else html.Append(Encode(token.Value));
            offset = token.Index + token.Length;
        }
        html.Append(Encode(text[offset..]));
    }

    private static void AppendAnalysis(
        StringBuilder html, AskAiAnalysis analysis, HashSet<int> citations, bool compact)
    {
        html.Append("<h2 style=\"font-size:18px;margin:24px 0 10px;\">")
            .Append(Encode(analysis.Title)).Append("</h2>");
        if (analysis.Visualization == "bar") AppendBars(html, analysis, citations, compact ? 5 : 20);
        else if (analysis.Visualization == "line")
            html.Append("<p><em>Values are tabulated below; the interactive line chart is available in the full answer.</em></p>");
        var columns = analysis.Columns.Take(9).ToArray();
        var rows = analysis.Rows.Take(compact ? 5 : 25).ToArray();
        BeginTable(html);
        html.Append("<caption style=\"text-align:left;font-weight:600;padding:8px 0;\">Supporting data</caption><thead><tr>");
        foreach (var column in columns) AppendCell(html, column.Label, citations, header: true);
        AppendCell(html, "Sources", citations, header: true);
        html.Append("</tr></thead><tbody>");
        for (var index = 0; index < rows.Length; index++)
        {
            html.Append("<tr style=\"background:").Append(index % 2 == 0 ? "#ffffff" : "#f3f6fa").Append(";\">");
            foreach (var column in columns)
                AppendCell(html, Shorten(DisplayCell(rows[index], column), compact ? 100 : 500),
                    citations, numeric: column.Kind == "number");
            AppendCell(html, String.Join(" ", rows[index].CitationNumbers.Select(number =>
                "[" + number.ToString(CultureInfo.InvariantCulture) + "]")), citations, markdown: true);
            html.Append("</tr>");
        }
        html.Append("</tbody>");
        AppendTotal(html, analysis, columns, citations);
        html.Append("</table>");
        if (rows.Length < analysis.Rows.Count || columns.Length < analysis.Columns.Count)
            html.Append("<p><em>Showing ").Append(rows.Length.ToString(CultureInfo.InvariantCulture)).Append(" of ")
                .Append(analysis.Rows.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" rows. Open the full answer for the complete table.</em></p>");
        var coverage = analysis.Coverage;
        html.Append("<p style=\"font-size:13px;color:#46536a;\">")
            .Append(coverage.RecordsIncluded.ToString(CultureInfo.InvariantCulture)).Append(" source records; ")
            .Append(coverage.DuplicatesExcluded.ToString(CultureInfo.InvariantCulture)).Append(" duplicates excluded; ")
            .Append(coverage.MissingValues.ToString(CultureInfo.InvariantCulture)).Append(" records with missing values.");
        if (coverage.DocumentsMatched > 0)
            html.Append(" Inspected ").Append(coverage.DocumentsInspected.ToString(CultureInfo.InvariantCulture))
                .Append(" of ").Append(coverage.DocumentsMatched.ToString(CultureInfo.InvariantCulture))
                .Append(" matched documents.");
        if (coverage.HasMoreDocuments) html.Append(" Additional documents were not inspected.");
        html.Append("</p>");
        foreach (var note in analysis.Notes.Take(20))
            html.Append("<p style=\"font-size:13px;margin:4px 0;\">")
                .Append(Encode(Shorten(note, compact ? 200 : 1_000))).Append("</p>");
        if (analysis.Notes.Count > 20)
            html.Append("<p><em>Additional analytical notes are available in the full answer.</em></p>");
    }

    private static void AppendBars(
        StringBuilder html, AskAiAnalysis analysis, HashSet<int> citations, int maximumRows)
    {
        var units = analysis.Rows.Select(row => TextValue(row.Cells.GetValueOrDefault("unit")))
            .Distinct(StringComparer.Ordinal).ToArray();
        var unit = units.Length == 1 ? units[0] : null;
        if (analysis.XColumn is null || analysis.YColumn is null || analysis.Rows.Count == 0 ||
            String.IsNullOrWhiteSpace(unit) ||
            analysis.Rows.Any(row => !TryNumber(row.Cells.GetValueOrDefault(analysis.YColumn), out var value) ||
                value < 0 || String.IsNullOrWhiteSpace(TextValue(row.Cells.GetValueOrDefault(analysis.XColumn)))))
        {
            html.Append("<p><em>A table is used instead of bars for missing, negative or mixed-unit values.</em></p>");
            return;
        }
        var maximum = analysis.Rows.Max(row =>
        {
            TryNumber(row.Cells[analysis.YColumn], out var value);
            return value;
        });
        html.Append("<div role=\"group\" aria-label=\"Bar chart\" style=\"margin:12px 0 20px;\">");
        foreach (var row in analysis.Rows.Take(maximumRows))
        {
            TryNumber(row.Cells[analysis.YColumn], out var value);
            var width = maximum == 0 ? 0 : Decimal.Round(value / maximum * 100, 2);
            html.Append("<p style=\"margin:12px 0 4px;\">")
                .Append(Encode(Shorten(TextValue(row.Cells[analysis.XColumn])!, 200)))
                .Append(" &mdash; <strong>")
                .Append(Encode(value.ToString("#,##0.############################", CultureInfo.InvariantCulture)))
                .Append(" ").Append(Encode(unit)).Append("</strong> ");
            AppendInline(html, String.Join(" ", row.CitationNumbers.Select(number =>
                "[" + number.ToString(CultureInfo.InvariantCulture) + "]")), citations);
            html.Append("</p><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" ")
                .Append("style=\"border-collapse:collapse;table-layout:fixed;\"><tr>");
            if (width > 0)
                html.Append("<td bgcolor=\"#2563eb\" height=\"12\" width=\"")
                    .Append(width.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append("%\" style=\"font-size:1px;line-height:12px;\">&nbsp;</td>");
            if (width < 100)
                html.Append("<td bgcolor=\"#e7edf5\" height=\"12\" width=\"")
                    .Append((100 - width).ToString("0.##", CultureInfo.InvariantCulture))
                    .Append("%\" style=\"font-size:1px;line-height:12px;\">&nbsp;</td>");
            html.Append("</tr></table>");
        }
        html.Append("<p style=\"font-size:12px;color:#46536a;\">Bars share a zero baseline and scale to the largest value.")
            .Append(" Very small values may not be visible; exact values are in the table.");
        if (analysis.Rows.Count > maximumRows)
            html.Append(" Showing ").Append(maximumRows.ToString(CultureInfo.InvariantCulture)).Append(" of ")
                .Append(analysis.Rows.Count.ToString(CultureInfo.InvariantCulture)).Append(" categories.");
        html.Append("</p></div>");
    }

    private static string DisplayCell(AskAiAnalysisRow row, AskAiAnalysisColumn column)
    {
        var value = row.Cells.GetValueOrDefault(column.Key);
        if (value is null || value is JsonElement { ValueKind: JsonValueKind.Null }) return "Not available";
        if (column.Kind != "number") return TextValue(value) ?? "Not available";
        if (!TryNumber(value, out var number)) return "Not available";
        if (column.Key == "sharePercent") return number.ToString("0.00", CultureInfo.InvariantCulture) + "%";
        return number.ToString(column.Key == "value" && TextValue(row.Cells.GetValueOrDefault("unit")) != "records"
            ? "#,##0.00##########################" : "#,##0.############################",
            CultureInfo.InvariantCulture);
    }

    private static void AppendTotal(
        StringBuilder html, AskAiAnalysis analysis, AskAiAnalysisColumn[] columns, HashSet<int> citations)
    {
        if (analysis.Operation is not ("sum" or "count") || analysis.Rows.Count == 0 ||
            !columns.Any(column => column.Key == "value") ||
            analysis.Rows.Select(row => TextValue(row.Cells.GetValueOrDefault("unit")))
                .Distinct(StringComparer.Ordinal).Count() != 1 ||
            analysis.Rows.Any(row => !TryNumber(row.Cells.GetValueOrDefault("value"), out _))) return;
        var total = analysis.Rows.Sum(row =>
        {
            TryNumber(row.Cells["value"], out var value);
            return value;
        });
        var summary = new AskAiAnalysisRow
        {
            Cells = new()
            {
                ["category"] = "Total",
                ["unit"] = analysis.Rows[0].Cells.GetValueOrDefault("unit"),
                ["value"] = total,
                ["records"] = analysis.Coverage.RecordsIncluded,
                ["sharePercent"] = total > 0 && analysis.Rows.All(row =>
                    TryNumber(row.Cells.GetValueOrDefault("sharePercent"), out _)) ? 100m : null
            }
        };
        html.Append("<tfoot><tr style=\"font-weight:600;background:#eaf0f8;\">");
        foreach (var column in columns)
            AppendCell(html, DisplayCell(summary, column), citations, numeric: column.Kind == "number");
        AppendCell(html, String.Join(" ", analysis.Rows.SelectMany(row => row.CitationNumbers).Distinct()
            .Select(number => "[" + number.ToString(CultureInfo.InvariantCulture) + "]")),
            citations, markdown: true);
        html.Append("</tr></tfoot>");
    }

    private static bool TryNumber(object? value, out decimal number)
    {
        number = 0;
        if (value is JsonElement element)
            return element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out number);
        return value is decimal or int or long or short or byte or double or float &&
            Decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static string? TextValue(object? value) => value is JsonElement element
        ? element.ValueKind == JsonValueKind.String ? element.GetString() : null
        : value as string;
    private static string[] TableCells(string line)
    {
        line = line.Trim();
        if (line.StartsWith('|')) line = line[1..];
        if (line.EndsWith('|') && !line.EndsWith("\\|", StringComparison.Ordinal)) line = line[..^1];
        var cells = new List<string>();
        var value = new StringBuilder();
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '\\' && index + 1 < line.Length && line[index + 1] == '|')
                value.Append(line[++index]);
            else if (line[index] == '|')
            {
                cells.Add(value.ToString().Trim());
                value.Clear();
            }
            else value.Append(line[index]);
        }
        cells.Add(value.ToString().Trim());
        return cells.ToArray();
    }
    private static string Encode(string text) => WebUtility.HtmlEncode(text);
    private static string Shorten(string text, int limit) =>
        text.Length > limit ? text[..(Char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit)] + "..." : text;
    private static void BeginTable(StringBuilder html) =>
        html.Append("<table width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" ")
            .Append("style=\"border-collapse:collapse;font-size:13px;margin:12px 0;\">");
    private static void AppendCell(
        StringBuilder html, string text, HashSet<int> citations,
        bool header = false, bool numeric = false, bool markdown = false)
    {
        html.Append(header ? "<th scope=\"col\" style=\"" : "<td style=\"").Append(CellStyle)
            .Append(numeric ? "text-align:right;" : "text-align:left;")
            .Append(header ? "background:#eaf0f8;font-weight:600;\">" : "\">");
        if (markdown) AppendInline(html, text, citations);
        else html.Append(Encode(text));
        html.Append(header ? "</th>" : "</td>");
    }
    private static void AppendLink(StringBuilder html, string url, string label) =>
        html.Append("<a href=\"").Append(Encode(url)).Append("\" style=\"color:#1558b0;text-decoration:underline;\">")
            .Append(Encode(label)).Append("</a>");
}
