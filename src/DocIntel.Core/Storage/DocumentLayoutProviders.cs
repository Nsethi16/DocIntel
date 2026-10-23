using Azure;
using Azure.Storage.Blobs.Models;
using DocIntel.Core.Evidence;

namespace DocIntel.Core.Storage;

public sealed partial class LocalFileArtifactStore
{
    public async Task<string> SaveLayoutAsync(DocumentLayout layout, CancellationToken cancellationToken = default)
    {
        var bytes = DocumentLayoutCodec.Serialize(layout);
        await WriteImmutableAsync(layout.TenantId, layout.DocumentId, "layout.json", bytes, cancellationToken);
        return DocumentLayoutCodec.Hash(bytes);
    }

    private async Task<string> WriteImmutableAsync(string tenantId, string documentId,
        string fileName, byte[] bytes, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_baseDirectory, SanitizeContainerName(tenantId), documentId);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".upload");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, fileName);
            // Unix Move(false) can race between its existence check and rename; coordinate all local publishers.
            await using var publicationLock = await AcquirePublicationLockAsync(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
            return path;
        }
        finally { File.Delete(temporary); }
    }

    private static async Task<FileStream> AcquirePublicationLockAsync(
        string path, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                // Keep the lock file: unlinking it could give contenders different inodes for the same target.
                return new FileStream(path + ".publish.lock", FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
        }
    }

    public async Task<DocumentLayout?> GetLayoutAsync(string tenantId, string documentId,
        string expectedSha256, string originalSha256, CancellationToken cancellationToken = default)
    {
        ValidateDocumentKey(documentId, 0);
        var path = Path.Combine(_baseDirectory, SanitizeContainerName(tenantId), documentId, "layout.json");
        try
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length > DocumentLayoutCodec.MaxBytes)
                throw new InvalidDataException("The persisted source layout exceeds its storage limit.");
            return DocumentLayoutCodec.Deserialize(await DocumentLayoutCodec.ReadAsync(source, cancellationToken),
                tenantId, documentId, expectedSha256, originalSha256);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}

public sealed partial class BlobArtifactStore
{
    public async Task<string> SaveLayoutAsync(DocumentLayout layout, CancellationToken cancellationToken = default)
    {
        var bytes = DocumentLayoutCodec.Serialize(layout);
        var container = _blobServiceClient.GetBlobContainerClient(
            LocalFileArtifactStore.SanitizeContainerName(layout.TenantId));
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await container.GetBlobClient($"{layout.DocumentId}/layout.json").UploadAsync(
            BinaryData.FromBytes(bytes), new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            }, cancellationToken);
        return DocumentLayoutCodec.Hash(bytes);
    }

    public async Task<DocumentLayout?> GetLayoutAsync(string tenantId, string documentId,
        string expectedSha256, string originalSha256, CancellationToken cancellationToken = default)
    {
        LocalFileArtifactStore.ValidateDocumentKey(documentId, 0);
        var blob = _blobServiceClient.GetBlobContainerClient(LocalFileArtifactStore.SanitizeContainerName(tenantId))
            .GetBlobClient($"{documentId}/layout.json");
        try
        {
            var result = await blob.DownloadStreamingAsync(new BlobDownloadOptions(), cancellationToken);
            await using var source = result.Value.Content;
            if (result.Value.Details.ContentLength > DocumentLayoutCodec.MaxBytes)
                throw new InvalidDataException("The persisted source layout exceeds its storage limit.");
            return DocumentLayoutCodec.Deserialize(await DocumentLayoutCodec.ReadAsync(source, cancellationToken),
                tenantId, documentId, expectedSha256, originalSha256);
        }
        catch (RequestFailedException exception) when (exception.Status == 404) { return null; }
    }
}
