using DocIntel.Core.Evidence;

namespace DocIntel.Core.Ocr;

/// <summary>Recognizes selected original PDF pages without model-generated text or an untrusted source URL.</summary>
public interface IOcrProvider
{
    string Name { get; }
    /// <summary>Stable model/API/endpoint identity, or null when persistent reuse is unsupported.</summary>
    string? CacheIdentity { get; }
    Task<IReadOnlyList<OcrPageResult>> RecognizeAsync(
        byte[] originalPdf, IReadOnlyList<int> pageNumbers, CancellationToken cancellationToken = default);
}

/// <summary>Actual provider output; dimensions and polygons share the provider's page coordinate system.</summary>
public sealed record OcrPageResult(
    int PageNumber, double PageWidth, double PageHeight, IReadOnlyList<PageWord> Words, string ProviderName);
