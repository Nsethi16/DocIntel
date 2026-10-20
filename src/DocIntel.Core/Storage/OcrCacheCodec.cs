using System.Globalization;
using System.Text.Json;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Ocr;

namespace DocIntel.Core.Storage;

internal static class OcrCacheCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private sealed record Envelope(OcrCacheEntry Entry, string ContentSha256);

    public static string FileName(string originalSha256, string providerFingerprint, int pageNumber)
    {
        if (new[] { originalSha256, providerFingerprint }.Any(hash =>
                hash is not { Length: 64 } || !hash.All(Char.IsAsciiHexDigit)) ||
            pageNumber is < 1 or > DocumentLayoutCodec.MaxPages)
            throw new ArgumentException("OCR cache keys require source/provider hashes and a valid original page.");
        return $"{providerFingerprint}-{originalSha256}-{pageNumber.ToString(CultureInfo.InvariantCulture)}.json";
    }

    public static byte[] Serialize(OcrCacheEntry entry)
    {
        Validate(entry);
        var payload = JsonSerializer.SerializeToUtf8Bytes(entry, Options);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(entry, DocumentLayoutCodec.Hash(payload)), Options);
        if (bytes.Length > DocumentLayoutCodec.MaxBytes) throw new InvalidDataException("OCR cache evidence is too large.");
        return bytes;
    }

    public static OcrPageResult Deserialize(byte[] bytes, string tenantId, string originalSha256,
        string providerFingerprint, int pageNumber)
    {
        if (bytes.Length > DocumentLayoutCodec.MaxBytes) throw new InvalidDataException("OCR cache evidence is too large.");
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Options)
            ?? throw new InvalidDataException("The OCR cache entry is empty.");
        Validate(envelope.Entry);
        var entry = envelope.Entry;
        if (entry.TenantId != tenantId || entry.OriginalSha256 != originalSha256 ||
            entry.ProviderFingerprint != providerFingerprint || entry.Result.PageNumber != pageNumber ||
            DocumentLayoutCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(entry, Options)) != envelope.ContentSha256)
            throw new InvalidDataException("The OCR cache content or input/provider/tenant identity does not match.");
        return entry.Result;
    }

    private static void Validate(OcrCacheEntry entry)
    {
        if (entry is null || entry.SchemaVersion != 1 || entry.Result is null ||
            String.IsNullOrWhiteSpace(entry.TenantId) || String.IsNullOrWhiteSpace(entry.Result.ProviderName))
            throw new InvalidDataException("The OCR cache entry has an invalid identity or schema.");
        FileName(entry.OriginalSha256, entry.ProviderFingerprint, entry.Result.PageNumber);
        DocumentLayoutCodec.ValidatePage(new PageLayout
        {
            PageNumber = entry.Result.PageNumber,
            PageWidth = entry.Result.PageWidth,
            PageHeight = entry.Result.PageHeight,
            Words = entry.Result.Words
        });
    }
}
