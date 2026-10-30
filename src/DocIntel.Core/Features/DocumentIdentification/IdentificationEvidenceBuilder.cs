using System.Text;
using DocIntel.Core.Features.DocumentSplitting;

namespace DocIntel.Core.Features.DocumentIdentification;

/// <summary>Builds a source-located representative packet, not an unbounded whole-document prompt.</summary>
internal static class IdentificationEvidenceBuilder
{
    private const int MaximumEvidenceBytes = 7_000;

    internal static string Build(DocumentSlice slice, IReadOnlyList<PageLayout> sourcePages)
    {
        var pages = sourcePages.Where(page => page.PageNumber >= slice.StartPage && page.PageNumber <= slice.EndPage)
            .OrderBy(page => page.PageNumber).ToArray();
        var text = new StringBuilder($"Logical document pages {slice.StartPage}-{slice.EndPage}.\n" +
            "The following are sampled original headings and footers, not the complete document. " +
            "Use undefined when this evidence is insufficient.\n");
        if (pages.Length == 0)
        {
            text.AppendLine("Original leading text:");
            foreach (var line in PageTextLayout.LeadingLines(slice.BodyText, 6, 384))
                text.AppendLine(Prefix(line, 384));
            foreach (var footer in slice.Footers.Take(6)) text.AppendLine("Footer: " + Prefix(footer, 256));
        }
        else
        {
            var indices = new[] { 0, Math.Min(1, pages.Length - 1), pages.Length - 1, pages.Length / 2,
                pages.Length / 3, pages.Length * 2 / 3 }.Distinct().ToArray();
            text.AppendLine($"Sampled {indices.Length} of {pages.Length} source pages.");
            foreach (var index in indices)
            {
                var page = pages[index];
                text.AppendLine($"Source page {page.PageNumber}:");
                foreach (var line in PageTextLayout.LeadingLines(page.BodyText, 4, 256))
                    text.AppendLine(Prefix(line, 192));
                if (!String.IsNullOrWhiteSpace(page.FooterText))
                    text.AppendLine("Footer: " + Prefix(page.FooterText, 160));
            }
        }
        return Prefix(text.ToString(), MaximumEvidenceBytes);
    }

    private static string Prefix(string text, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maximumBytes) return text;
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumBytes) break;
            result.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
}
