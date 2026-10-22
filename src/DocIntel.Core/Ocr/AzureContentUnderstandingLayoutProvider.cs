using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.ContentUnderstanding;
using DocIntel.Core.Evidence;
using DocIntel.Core.Storage;

namespace DocIntel.Core.Ocr;

/// <summary>Native GA Content Understanding layout; Markdown and geometry remain tied to original PDF pages.</summary>
public sealed class AzureContentUnderstandingLayoutProvider(ContentUnderstandingClient client, Uri endpoint) : IOcrProvider
{
    public const string AnalyzerId = "prebuilt-layout";
    public const string ApiVersion = "2025-11-01";
    public string Name => "AzureContentUnderstanding:" + AnalyzerId;
    public bool UsesFullLayout => true;
    public string CacheIdentity => $"{Name}:{ApiVersion}:mapping-v1:{endpoint.AbsoluteUri}";

    public async Task<IReadOnlyList<OcrPageResult>> RecognizeAsync(
        byte[] originalPdf, IReadOnlyList<int> pageNumbers, CancellationToken cancellationToken = default)
    {
        LocalFileArtifactStore.ValidateOriginal(originalPdf);
        if (pageNumbers.Count is < 1 or > DocumentLayoutCodec.MaxPages ||
            pageNumbers.Any(page => page is < 1 or > DocumentLayoutCodec.MaxPages) ||
            pageNumbers.Distinct().Count() != pageNumbers.Count)
            throw new ArgumentException("Layout analysis requires distinct original PDF page numbers.");
        var range = ContentRange.Combine(pageNumbers.Order().Select(ContentRange.Page).ToArray());
        var operation = await client.AnalyzeBinaryAsync(WaitUntil.Completed, AnalyzerId,
            BinaryData.FromBytes(originalPdf), range, "application/pdf", cancellationToken: cancellationToken);
        var result = operation.Value ?? throw new InvalidDataException("Content Understanding returned no layout result.");
        if (result.AnalyzerId != AnalyzerId || result.ApiVersion != ApiVersion || result.Warnings.Count != 0)
            throw new InvalidDataException("Content Understanding returned an unexpected analyzer/API or analysis warnings.");
        var documents = result.Contents.OfType<DocumentContent>().ToList();
        if (documents.Count != 1 || result.Contents.Count != 1)
            throw new InvalidDataException("PDF layout analysis must return one original document.");
        var document = documents[0];
        var pages = new List<OcrPageResult>();
        foreach (var page in document.Pages)
        {
            if (!pageNumbers.Contains(page.PageNumber) || page.Width is not { } width || width <= 0 ||
                !Single.IsFinite(width) || page.Height is not { } height || height <= 0 || !Single.IsFinite(height) ||
                page.Words.Count > DocumentLayoutCodec.MaxWordsPerPage)
                throw new InvalidDataException("Content Understanding returned invalid original-page geometry.");
            var words = page.Words.Select((word, index) =>
            {
                if (String.IsNullOrWhiteSpace(word.Content) ||
                    word.Confidence is { } confidence && (!Single.IsFinite(confidence) || confidence is < 0 or > 1))
                    throw new InvalidDataException("Content Understanding returned an invalid source word.");
                var sources = DocumentSource.Parse(word.Source);
                var source = sources.Length == 1 ? sources[0] : null;
                if (source?.PageNumber != page.PageNumber || source.Polygon is not { Count: 4 } polygon)
                    throw new InvalidDataException("A layout word lacks its exact original-page quadrilateral.");
                var coordinates = polygon.SelectMany(point => new[] { (double)point.X / width, (double)point.Y / height }).ToArray();
                if (coordinates.Any(value => !Double.IsFinite(value) || value is < -0.001 or > 1.001))
                    throw new InvalidDataException("A layout word lies outside its original-page bounds.");
                return new PageWord($"w_{page.PageNumber}_{index}", word.Content,
                    new BoundingBox(page.PageNumber, coordinates.Select(value => Math.Clamp(value, 0, 1)).ToArray()),
                    word.Confidence);
            }).ToList();
            var markdown = new StringBuilder();
            foreach (var span in page.Spans.OrderBy(span => span.Offset))
                markdown.Append(Slice(document.Markdown, span.Offset, span.Length, result.StringEncoding));
            if (page.Spans.Count == 0 || String.IsNullOrWhiteSpace(markdown.ToString()) && words.Count > 0)
                throw new InvalidDataException("Layout page Markdown is not mapped to source spans.");
            var native = JsonSerializer.Serialize(new
            {
                analyzerId = AnalyzerId,
                apiVersion = ApiVersion,
                stringEncoding = result.StringEncoding,
                page = ModelReaderWriter.Write(page).ToObjectFromJson<JsonElement>(),
                paragraphs = document.Paragraphs.Where(paragraph =>
                    DocumentSource.Parse(paragraph.Source).Any(source => source.PageNumber == page.PageNumber))
                    .Select(paragraph => ModelReaderWriter.Write(paragraph).ToObjectFromJson<JsonElement>()),
                tables = document.Tables.Where(table =>
                    DocumentSource.Parse(table.Source).Any(source => source.PageNumber == page.PageNumber))
                    .Select(table => ModelReaderWriter.Write(table).ToObjectFromJson<JsonElement>())
            });
            pages.Add(new OcrPageResult(page.PageNumber, width, height, words, Name, markdown.ToString(), native));
        }
        if (pages.Count != pageNumbers.Count || pages.Select(page => page.PageNumber).Distinct().Count() != pages.Count)
            throw new InvalidDataException("Content Understanding did not return exactly the requested original pages.");
        return pages.OrderBy(page => page.PageNumber).ToList();
    }

    private static string Slice(string text, int offset, int length, string encoding)
    {
        if (offset < 0 || length < 0) throw new InvalidDataException("Layout Markdown has an invalid span.");
        if (encoding == "utf16")
        {
            if ((long)offset + length > text.Length) throw new InvalidDataException("Layout Markdown span exceeds its content.");
            return text.Substring(offset, length);
        }
        if (encoding == "codePoint")
        {
            var runes = text.EnumerateRunes().ToArray();
            if ((long)offset + length > runes.Length) throw new InvalidDataException("Layout Markdown span exceeds its content.");
            return String.Concat(runes.Skip(offset).Take(length).Select(rune => rune.ToString()));
        }
        throw new InvalidDataException("Unsupported layout Markdown string encoding.");
    }
}
