using System.Text;
using System.Text.RegularExpressions;
using DocIntel.Core.Harness;
using DocIntel.Core.Evidence;
using DocIntel.Core.Storage;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DocIntel.Core.Features.DocumentSplitting;

public interface IPageLayoutExtractor
{
    Task<IReadOnlyList<PageLayout>> ExtractPagesAsync(
        byte[] pdfBytes,
        SplittingConfig config,
        CancellationToken cancellationToken = default);
}

public interface IPdfSlicer
{
    Task<IReadOnlyList<byte[]>> SlicePdfAsync(
        byte[] pdfBytes,
        IReadOnlyList<(int StartPage, int EndPage)> ranges,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Extracts page layouts (body text, bottom-of-page footer signatures, markdown) and slices PDFs using PdfPig.
/// </summary>
public sealed class PdfPigLayoutExtractor : IPageLayoutExtractor, IPdfSlicer
{
    private static readonly Regex InlineFooterCommentRegex = new(
        @"<!--\s*PageFooter[:""\s]\s*([^"">]+?)\s*""?\s*-->",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PageXOfYRegex = new(
        @"\b(?:Page|Página|Pagina|Seite)\s+(\d+)\s+(?:of|de|von)\s+(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Task<IReadOnlyList<PageLayout>> ExtractPagesAsync(
        byte[] pdfBytes,
        SplittingConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(config);

        using var document = PdfDocument.Open(pdfBytes, new ParsingOptions { ClipPaths = false });
        if (document.NumberOfPages is < 1 or > DocumentLayoutCodec.MaxPages)
            throw new InvalidDataException("The PDF page count exceeds the supported layout limits.");
        var pages = new List<PageLayout>(document.NumberOfPages);

        for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.GetPage(pageNumber);
            pages.Add(ExtractSinglePage(page, pageNumber, config.FootersToIgnore));
        }

        return Task.FromResult<IReadOnlyList<PageLayout>>(pages);
    }

    public Task<IReadOnlyList<byte[]>> SlicePdfAsync(
        byte[] pdfBytes,
        IReadOnlyList<(int StartPage, int EndPage)> ranges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(ranges);

        if (ranges.Count == 0)
        {
            throw new ArgumentException("At least one page range must be supplied.", nameof(ranges));
        }

        using var source = PdfDocument.Open(pdfBytes, new ParsingOptions { ClipPaths = false });
        var totalPages = source.NumberOfPages;

        foreach (var (start, end) in ranges)
        {
            if (start < 1 || end < start || end > totalPages)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ranges),
                    $"Page range {start}-{end} is invalid for a {totalPages}-page PDF.");
            }
        }

        var output = new List<byte[]>(ranges.Count);

        foreach (var (start, end) in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var ms = new MemoryStream();
            using (var builder = new PdfDocumentBuilder(ms, disposeStream: false))
            {
                for (var p = start; p <= end; p++)
                {
                    builder.AddPage(source, p);
                }

                builder.Build();
            }

            output.Add(ms.ToArray());
        }

        return Task.FromResult<IReadOnlyList<byte[]>>(output);
    }

    public static byte[] CreateSamplePdf(IReadOnlyList<(string Body, string Footer)> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        using var ms = new MemoryStream();
        using (var builder = new PdfDocumentBuilder(ms, disposeStream: false))
        {
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);

            foreach (var (body, footer) in pages)
            {
                var page = builder.AddPage(PageSize.Letter);
                var rawLines = (body ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var y = 720.0;

                foreach (var rawLine in rawLines)
                {
                    var cleanLine = SanitizeStandard14Text(rawLine.Trim('\r'));
                    if (string.IsNullOrWhiteSpace(cleanLine))
                    {
                        continue;
                    }

                    foreach (var chunk in WrapLine(cleanLine, 84))
                    {
                        if (y < 65.0)
                        {
                            if (!string.IsNullOrWhiteSpace(footer))
                            {
                                page.AddText(SanitizeStandard14Text(footer.Trim()), 9, new PdfPoint(50, 35), font);
                            }

                            page = builder.AddPage(PageSize.Letter);
                            y = 720.0;
                        }

                        page.AddText(chunk, 10.5, new PdfPoint(50, y), font);
                        y -= 16.0;
                    }
                }

                if (!string.IsNullOrWhiteSpace(footer))
                {
                    page.AddText(SanitizeStandard14Text(footer.Trim()), 9, new PdfPoint(50, 35), font);
                }
            }

            builder.Build();
        }

        return ms.ToArray();
    }

    private static IEnumerable<string> WrapLine(string line, int maxChars)
    {
        if (line.Length <= maxChars)
        {
            yield return line;
            yield break;
        }

        var idx = 0;
        while (idx < line.Length)
        {
            var len = Math.Min(maxChars, line.Length - idx);
            if (idx + len < line.Length)
            {
                var lastSpace = line.LastIndexOf(' ', idx + len, len);
                if (lastSpace > idx + 20)
                {
                    len = lastSpace - idx;
                }
            }

            yield return line.Substring(idx, len).Trim();
            idx += len;
            while (idx < line.Length && line[idx] == ' ')
            {
                idx++;
            }
        }
    }

    private static string SanitizeStandard14Text(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var normalized = input
            .Replace('—', '-')
            .Replace('–', '-')
            .Replace('•', '*')
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace('‘', '\'')
            .Replace('’', '\'')
            .Normalize(NormalizationForm.FormD);

        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (ch >= 0x20 && ch <= 0x7E)
            {
                sb.Append(ch);
            }
            else if (ch == '\t')
            {
                sb.Append("  ");
            }
        }

        return sb.ToString();
    }

    private static PageLayout ExtractSinglePage(
        Page page,
        int pageNumber,
        IReadOnlyList<string> footersToIgnore)
    {
        if (!Double.IsFinite(page.Width) || page.Width <= 0 || !Double.IsFinite(page.Height) || page.Height <= 0)
            throw new InvalidDataException("The PDF page has invalid dimensions.");
        var extractedWords = page.Rotation.Value == 0
            ? page.GetWords() : page.GetWords(NearestNeighbourWordExtractor.Instance);
        var words = extractedWords.Where(word => !String.IsNullOrWhiteSpace(word.Text))
            .Take(DocumentLayoutCodec.MaxWordsPerPage + 1)
            .OrderByDescending(word => word.BoundingBox.Top)
            .ThenBy(word => word.BoundingBox.Left).ToList();
        if (words.Count > DocumentLayoutCodec.MaxWordsPerPage)
            throw new InvalidDataException("The PDF word count exceeds the supported per-page layout limit.");
        var footerBoundaryY = page.Height * 0.10; // Bottom 10% of page height

        var footerWords = new List<string>();
        var bodyLines = new List<string>();
        int? subDocPageNumber = null;
        int? subDocTotalPages = null;

        var lineGroups = words
            .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3.0) * 3.0)
            .OrderByDescending(g => g.Key);

        foreach (var lineGroup in lineGroups)
        {
            var lineText = string.Join(" ", lineGroup.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)).Trim();
            if (string.IsNullOrWhiteSpace(lineText))
            {
                continue;
            }

            if (lineGroup.Key <= footerBoundaryY)
            {
                // Detect "Page X of Y" before stripping it so we preserve form-internal pagination signals
                var pageMatch = PageXOfYRegex.Match(lineText);
                if (pageMatch.Success)
                {
                    subDocPageNumber = int.Parse(pageMatch.Groups[1].Value);
                    subDocTotalPages = int.Parse(pageMatch.Groups[2].Value);
                    lineText = PageXOfYRegex.Replace(lineText, string.Empty).Trim();
                }

                if (!string.IsNullOrWhiteSpace(lineText) && !IsIgnoredFooter(lineText, footersToIgnore))
                {
                    footerWords.Add(lineText.Replace(" ", string.Empty, StringComparison.Ordinal));
                }
            }
            else
            {
                bodyLines.Add(lineText);
            }
        }

        var bodyText = string.Join(Environment.NewLine, bodyLines);
        var footerText = string.Join(",", footerWords.Where(f => !string.IsNullOrEmpty(f)));

        if (string.IsNullOrEmpty(footerText))
        {
            var match = InlineFooterCommentRegex.Match(bodyText);
            if (match.Success)
            {
                footerText = match.Groups[1].Value.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
            }
        }

        var md = new StringBuilder(bodyText);
        if (!string.IsNullOrEmpty(footerText))
        {
            md.AppendLine();
            md.Append($"<!-- PageFooter=\"{footerText}\" -->");
        }

        return new PageLayout
        {
            PageNumber = pageNumber,
            PageWidth = page.Width,
            PageHeight = page.Height,
            ImageAreaRatio = Math.Clamp(page.GetImages().Sum(image => image.BoundingBox.Area) / (page.Width * page.Height), 0, 1),
            TextSource = "EmbeddedText",
            TextProvider = "PdfPig",
            Words = words.Select((word, index) => new PageWord($"w_{pageNumber}_{index}", word.Text,
                new BoundingBox(pageNumber, new[]
                {
                    word.BoundingBox.TopLeft, word.BoundingBox.TopRight,
                    word.BoundingBox.BottomRight, word.BoundingBox.BottomLeft
                }.SelectMany(point => new[]
                {
                    Math.Clamp(point.X / page.Width, 0, 1), Math.Clamp(1 - point.Y / page.Height, 0, 1)
                }).ToArray()))).ToArray(),
            SubDocPageNumber = subDocPageNumber,
            SubDocTotalPages = subDocTotalPages,
            BodyText = bodyText,
            FooterText = footerText,
            Markdown = md.ToString()
        };
    }

    private static bool IsIgnoredFooter(string text, IReadOnlyList<string> ignoredValues)
    {
        var normalized = NormalizeFooter(text);
        if (string.IsNullOrEmpty(normalized)) return true;

        foreach (var ignored in ignoredValues)
        {
            var normalizedIgnored = NormalizeFooter(ignored);
            if (string.IsNullOrEmpty(normalizedIgnored)) continue;

            // Ignore bare "Page" or "CTM" only when the entire remaining footer equals that token
            if (normalizedIgnored is "ctm" or "page")
            {
                if (normalized == normalizedIgnored) return true;
                continue;
            }

            if (normalized.Contains(normalizedIgnored, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeFooter(string value) =>
        string.Concat((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
}
