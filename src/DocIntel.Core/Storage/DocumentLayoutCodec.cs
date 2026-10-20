using System.Security.Cryptography;
using System.Text.Json;
using DocIntel.Core.Evidence;
using DocIntel.Core.Features.DocumentSplitting;

namespace DocIntel.Core.Storage;

internal static class DocumentLayoutCodec
{
    public const int MaxBytes = 16 * 1024 * 1024;
    public const int MaxPages = 1_000;
    public const int MaxWordsPerPage = 50_000;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(DocumentLayout layout)
    {
        Validate(layout);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(layout, Options);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("The source layout exceeds its storage limit.");
        return bytes;
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static async Task<byte[]> ReadAsync(Stream source, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (body.Length + read > MaxBytes)
                throw new InvalidDataException("The persisted source evidence exceeds its storage limit.");
            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return body.ToArray();
    }

    public static DocumentLayout Deserialize(byte[] bytes, string tenantId, string documentId,
        string expectedSha256, string originalSha256)
    {
        if (bytes.Length > MaxBytes || Hash(bytes) != expectedSha256)
            throw new InvalidDataException("The persisted layout does not match its accepted content hash.");
        var layout = JsonSerializer.Deserialize<DocumentLayout>(bytes, Options)
            ?? throw new InvalidDataException("The persisted source layout is empty.");
        Validate(layout);
        if (layout.TenantId != tenantId || layout.DocumentId != documentId || layout.OriginalSha256 != originalSha256)
            throw new InvalidDataException("The persisted layout does not match the requested document source.");
        return layout;
    }

    public static void Validate(DocumentLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(layout.TenantId);
        LocalFileArtifactStore.ValidateDocumentKey(layout.DocumentId, 0);
        if (layout.SchemaVersion != 1 || layout.OriginalSha256 is not { Length: 64 } ||
            !layout.OriginalSha256.All(Char.IsAsciiHexDigit) || layout.Pages is null ||
            layout.Pages.Count is < 1 or > MaxPages || layout.Pages.Any(page => page is null))
            throw new InvalidDataException("The source layout has an invalid schema, source hash, or page collection.");
        var wordIds = new HashSet<string>(StringComparer.Ordinal);
        var pageNumbers = new HashSet<int>();
        long textLength = 0;
        foreach (var page in layout.Pages)
        {
            if (page.PageNumber != pageNumbers.Count + 1 || !pageNumbers.Add(page.PageNumber))
                throw new InvalidDataException("The source layout has invalid page geometry or duplicate pages.");
            ValidatePage(page, wordIds);
            textLength += page.BodyText?.Length ?? 0;
            textLength += page.Markdown?.Length ?? 0;
            textLength += page.Words.Sum(word => (long)word.Text.Length);
            if (textLength > MaxBytes)
                throw new InvalidDataException("The source layout text exceeds its storage limit.");
        }
    }

    public static void ValidatePage(PageLayout page, HashSet<string>? wordIds = null)
    {
        if (page.PageNumber is < 1 or > MaxPages || !Double.IsFinite(page.PageWidth) || page.PageWidth <= 0 ||
            !Double.IsFinite(page.PageHeight) || page.PageHeight <= 0 ||
            !Double.IsFinite(page.ImageAreaRatio) || page.ImageAreaRatio is < 0 or > 1 ||
            page.Words is null || page.Words.Count > MaxWordsPerPage)
            throw new InvalidDataException("The source evidence has invalid page geometry or too many words.");
        wordIds ??= new HashSet<string>(StringComparer.Ordinal);
        long length = 0;
        foreach (var word in page.Words)
        {
            if (word is null || String.IsNullOrWhiteSpace(word.Id) || !wordIds.Add(word.Id) ||
                String.IsNullOrEmpty(word.Text) || word.Box is null || word.Box.Page != page.PageNumber ||
                word.OcrConfidence is { } score && (!Double.IsFinite(score) || score is < 0 or > 1))
                throw new InvalidDataException("The source evidence has invalid word identity, geometry, or confidence.");
            length += word.Text.Length;
        }
        if (length > MaxBytes) throw new InvalidDataException("The source evidence text exceeds its storage limit.");
    }
}
