using System.Globalization;
using Azure;
using Azure.AI.DocumentIntelligence;
using DocIntel.Core.Evidence;
using DocIntel.Core.Storage;

namespace DocIntel.Core.Ocr;

/// <summary>Uses native Azure prebuilt-read with selected original PDF pages and managed-identity credentials.</summary>
public sealed class AzureDocumentIntelligenceOcrProvider(DocumentIntelligenceClient client, Uri? endpoint = null) : IOcrProvider
{
    public string Name => "AzureDocumentIntelligence:prebuilt-read";
    public string? CacheIdentity => endpoint is null ? null : $"{Name}:2024-11-30:{endpoint.AbsoluteUri}";

    public async Task<IReadOnlyList<OcrPageResult>> RecognizeAsync(
        byte[] originalPdf, IReadOnlyList<int> pageNumbers, CancellationToken cancellationToken = default)
    {
        LocalFileArtifactStore.ValidateOriginal(originalPdf);
        ArgumentNullException.ThrowIfNull(pageNumbers);
        if (pageNumbers.Count is < 1 or > DocumentLayoutCodec.MaxPages ||
            pageNumbers.Any(page => page < 1) || pageNumbers.Distinct().Count() != pageNumbers.Count)
            throw new ArgumentException("OCR requires distinct positive page numbers within the supported page limit.");
        var request = new AnalyzeDocumentOptions("prebuilt-read", BinaryData.FromBytes(originalPdf))
        {
            Pages = String.Join(",", pageNumbers.Order().Select(number => number.ToString(CultureInfo.InvariantCulture)))
        };
        var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, request, cancellationToken);
        var result = operation.Value ?? throw new InvalidDataException("OCR completed without a recognition result.");
        if (result.ModelId != "prebuilt-read")
            throw new InvalidDataException("OCR returned a result from a different recognition model.");
        var pages = new List<OcrPageResult>();
        foreach (var page in result.Pages)
        {
            if (page.Width is not { } width || !Single.IsFinite(width) || width <= 0 ||
                page.Height is not { } height || !Single.IsFinite(height) || height <= 0 ||
                page.Words.Count > DocumentLayoutCodec.MaxWordsPerPage)
                throw new InvalidDataException("OCR returned invalid page dimensions or too many words.");
            var words = page.Words.Select((word, index) =>
            {
                if (word.Polygon.Count != 8 || word.Polygon.Any(coordinate => !Single.IsFinite(coordinate)) ||
                    !Single.IsFinite(word.Confidence) || word.Confidence is < 0 or > 1 ||
                    String.IsNullOrWhiteSpace(word.Content))
                    throw new InvalidDataException("OCR returned an invalid source word or polygon.");
                return new PageWord($"w_{page.PageNumber}_{index}", word.Content,
                    new BoundingBox(page.PageNumber, word.Polygon.Select((coordinate, position) =>
                        Math.Clamp((double)coordinate / (position % 2 == 0 ? width : height), 0, 1)).ToArray()),
                    word.Confidence);
            }).ToList();
            pages.Add(new OcrPageResult(page.PageNumber, width, height, words, Name));
        }
        if (pages.Count != pageNumbers.Count || pages.Select(page => page.PageNumber).Distinct().Count() != pages.Count ||
            pages.Any(page => !pageNumbers.Contains(page.PageNumber)))
            throw new InvalidDataException("OCR did not return exactly the requested original pages.");
        return pages;
    }
}
