using System.Text;
using System.Text.RegularExpressions;

namespace DocIntel.Core.Features.DocumentSplitting;

internal sealed record LayoutTextWord(string Text, double Left, double Bottom);

/// <summary>Applies the same footer, pagination, and body-text rules to native and recognized source words.</summary>
internal static class PageTextLayout
{
    internal static IEnumerable<string> LeadingLines(string text, int count, int maximumCharacters = 512)
    {
        var offset = 0;
        var emitted = 0;
        while (offset < text.Length && emitted < count)
        {
            var end = text.IndexOfAny(['\r', '\n'], offset);
            if (end < 0) end = text.Length;
            var length = Math.Min(end - offset, maximumCharacters);
            if (length > 0 && offset + length < text.Length && Char.IsHighSurrogate(text[offset + length - 1]))
                length--;
            var line = text.Substring(offset, length).Trim();
            offset = end + 1;
            if (line.Length == 0) continue;
            emitted++;
            yield return line;
        }
    }

    internal static readonly Regex FooterCommentRegex = new(
        @"<!--\s*PageFooter(?:\s*[:=]\s*""?|\s+""?|"")\s*([^"">]+?)\s*""?\s*-->",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PageXOfYRegex = new(
        @"\b(?:Page|Página|Pagina|Seite)\s+(\d+)\s+(?:of|de|von)\s+(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void Apply(PageLayout page, IEnumerable<LayoutTextWord> words, IReadOnlyList<string> ignoredFooters)
    {
        var footerWords = new List<string>();
        var bodyLines = new List<string>();
        page.SubDocPageNumber = null;
        page.SubDocTotalPages = null;
        foreach (var line in words.GroupBy(word => Math.Round(word.Bottom / 3.0) * 3.0)
                     .OrderByDescending(group => group.Key))
        {
            var text = String.Join(" ", line.OrderBy(word => word.Left).Select(word => word.Text)).Trim();
            if (String.IsNullOrWhiteSpace(text)) continue;
            if (line.Key > page.PageHeight * 0.10) { bodyLines.Add(text); continue; }
            var pagination = PageXOfYRegex.Match(text);
            if (pagination.Success)
            {
                page.SubDocPageNumber = Int32.Parse(pagination.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                page.SubDocTotalPages = Int32.Parse(pagination.Groups[2].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                text = PageXOfYRegex.Replace(text, String.Empty).Trim();
            }
            if (!String.IsNullOrWhiteSpace(text) && !IsIgnored(text, ignoredFooters))
                footerWords.Add(text.Replace(" ", String.Empty, StringComparison.Ordinal));
        }
        page.BodyText = String.Join(Environment.NewLine, bodyLines);
        page.FooterText = String.Join(",", footerWords.Where(footer => !String.IsNullOrEmpty(footer)));
        if (String.IsNullOrEmpty(page.FooterText))
        {
            var inlineFooter = FooterCommentRegex.Match(page.BodyText);
            if (inlineFooter.Success)
                page.FooterText = inlineFooter.Groups[1].Value.Trim().Replace(" ", String.Empty, StringComparison.Ordinal);
        }
        var markdown = new StringBuilder(page.BodyText);
        if (!String.IsNullOrEmpty(page.FooterText))
        {
            markdown.AppendLine();
            markdown.Append($"<!-- PageFooter=\"{page.FooterText}\" -->");
        }
        page.Markdown = markdown.ToString();
    }

    private static bool IsIgnored(string text, IReadOnlyList<string> ignoredFooters)
    {
        static string Normalize(string? value) =>
            String.Concat((value ?? String.Empty).Where(character => !Char.IsWhiteSpace(character))).ToLowerInvariant();
        var normalized = Normalize(text);
        if (String.IsNullOrEmpty(normalized)) return true;
        foreach (var ignored in ignoredFooters)
        {
            var value = Normalize(ignored);
            if (String.IsNullOrEmpty(value)) continue;
            if (value is "ctm" or "page")
            {
                if (normalized == value) return true;
            }
            else if (normalized.Contains(value, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
