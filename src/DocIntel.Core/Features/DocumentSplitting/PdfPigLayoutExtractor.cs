using System.Text;
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
        var layout = new PageLayout
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
        };
        PageTextLayout.Apply(layout, words.Select(word =>
            new LayoutTextWord(word.Text, word.BoundingBox.Left, word.BoundingBox.Bottom)), footersToIgnore);
        return layout;
    }
}
