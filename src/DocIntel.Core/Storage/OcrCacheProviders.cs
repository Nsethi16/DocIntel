using Azure;
using Azure.Storage.Blobs.Models;
using DocIntel.Core.Ocr;

namespace DocIntel.Core.Storage;

public sealed partial class LocalFileArtifactStore
{
    public async Task<OcrPageResult?> GetOcrCacheAsync(string tenantId, string originalSha256,
        string providerFingerprint, int pageNumber, CancellationToken cancellationToken = default)
    {
        var name = OcrCacheCodec.FileName(originalSha256, providerFingerprint, pageNumber);
        var path = Path.Combine(_baseDirectory, SanitizeContainerName(tenantId), "_ocr-cache", name);
        try
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length > DocumentLayoutCodec.MaxBytes) throw new InvalidDataException("OCR cache evidence is too large.");
            return OcrCacheCodec.Deserialize(await DocumentLayoutCodec.ReadAsync(source, cancellationToken),
                tenantId, originalSha256, providerFingerprint, pageNumber);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task<bool> TrySaveOcrCacheAsync(OcrCacheEntry entry, CancellationToken cancellationToken = default)
    {
        var bytes = OcrCacheCodec.Serialize(entry);
        try
        {
            await WriteImmutableAsync(entry.TenantId, "_ocr-cache",
                OcrCacheCodec.FileName(entry.OriginalSha256, entry.ProviderFingerprint, entry.Result.PageNumber),
                bytes, cancellationToken);
            return true;
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 17 or 80 or 183)
        {
            if (await GetOcrCacheAsync(entry.TenantId, entry.OriginalSha256, entry.ProviderFingerprint,
                entry.Result.PageNumber, cancellationToken) is null)
                throw new InvalidDataException("An existing OCR cache publication is unavailable.");
            return false;
        }
    }
}

public sealed partial class BlobArtifactStore
{
    public async Task<OcrPageResult?> GetOcrCacheAsync(string tenantId, string originalSha256,
        string providerFingerprint, int pageNumber, CancellationToken cancellationToken = default)
    {
        var name = OcrCacheCodec.FileName(originalSha256, providerFingerprint, pageNumber);
        var blob = _blobServiceClient.GetBlobContainerClient(LocalFileArtifactStore.SanitizeContainerName(tenantId))
            .GetBlobClient($"_ocr-cache/{name}");
        try
        {
            var result = await blob.DownloadStreamingAsync(new BlobDownloadOptions(), cancellationToken);
            await using var source = result.Value.Content;
            if (result.Value.Details.ContentLength > DocumentLayoutCodec.MaxBytes)
                throw new InvalidDataException("OCR cache evidence is too large.");
            return OcrCacheCodec.Deserialize(await DocumentLayoutCodec.ReadAsync(source, cancellationToken),
                tenantId, originalSha256, providerFingerprint, pageNumber);
        }
        catch (RequestFailedException exception) when (exception.Status == 404) { return null; }
    }

    public async Task<bool> TrySaveOcrCacheAsync(OcrCacheEntry entry, CancellationToken cancellationToken = default)
    {
        var bytes = OcrCacheCodec.Serialize(entry);
        var container = _blobServiceClient.GetBlobContainerClient(LocalFileArtifactStore.SanitizeContainerName(entry.TenantId));
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var name = OcrCacheCodec.FileName(entry.OriginalSha256, entry.ProviderFingerprint, entry.Result.PageNumber);
        try
        {
            await container.GetBlobClient($"_ocr-cache/{name}").UploadAsync(BinaryData.FromBytes(bytes),
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
                }, cancellationToken);
            return true;
        }
        catch (RequestFailedException exception) when (exception.Status == 412 ||
            exception.Status == 409 && exception.ErrorCode == "BlobAlreadyExists")
        {
            if (await GetOcrCacheAsync(entry.TenantId, entry.OriginalSha256, entry.ProviderFingerprint,
                entry.Result.PageNumber, cancellationToken) is null)
                throw new InvalidDataException("An existing OCR cache publication is unavailable.");
            return false;
        }
    }
}
