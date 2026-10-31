using Azure;
using Azure.Storage.Blobs.Models;
using DocIntel.Core.Jobs;

namespace DocIntel.Core.Storage;

internal static class DocumentContentPublication
{
    public static (byte[] Bytes, DocumentContentReference Reference) Prepare(DocumentJob job)
    {
        if (job.Status == JobStatus.Completed && job.ProcessingResultSha256 is not null)
        {
            var initial = ProcessingResultCodec.Serialize(job);
            if (ProcessingResultCodec.Hash(initial) == job.ProcessingResultSha256)
                return (initial,
                    ProcessingResultCodec.Reference(job, initial, ProcessingResultCodec.InitialProcessing));
        }
        var bytes = ProcessingResultCodec.Serialize(job, ProcessingResultCodec.DocumentContent);
        return (bytes, ProcessingResultCodec.Reference(job, bytes, ProcessingResultCodec.DocumentContent));
    }
}

public sealed partial class LocalFileArtifactStore
{
    public async Task<DocumentContentReference> SaveDocumentContentAsync(
        DocumentJob job, CancellationToken cancellationToken = default)
    {
        var (bytes, reference) = DocumentContentPublication.Prepare(job);
        var fileName = ProcessingResultCodec.ContentFileName(job, reference);
        var path = Path.Combine(_baseDirectory, SanitizeContainerName(job.TenantId),
            ProcessingResultCodec.ArtifactId(job, completedOnly: false), fileName);
        if (reference.Kind == ProcessingResultCodec.InitialProcessing)
        {
            await VerifyExistingAsync();
            return reference;
        }
        try
        {
            await WriteImmutableAsync(job.TenantId, ProcessingResultCodec.ArtifactId(job, completedOnly: false),
                fileName, bytes, cancellationToken);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 17 or 80 or 183)
        {
            await VerifyExistingAsync();
        }
        return reference;

        async Task VerifyExistingAsync()
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await ProcessingResultCodec.ReadAsync(stream, job, cancellationToken, reference);
        }
    }

    public async Task<byte[]?> GetDocumentContentAsync(DocumentJob job, CancellationToken cancellationToken = default)
    {
        var reference = job.ContentSnapshot
            ?? throw new InvalidDataException("The document has no content reference.");
        var path = Path.Combine(_baseDirectory, SanitizeContainerName(job.TenantId),
            ProcessingResultCodec.ArtifactId(job, completedOnly: false),
            ProcessingResultCodec.ContentFileName(job, reference));
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await ProcessingResultCodec.ReadAsync(stream, job, cancellationToken, reference);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}

public sealed partial class BlobArtifactStore
{
    public async Task<DocumentContentReference> SaveDocumentContentAsync(
        DocumentJob job, CancellationToken cancellationToken = default)
    {
        var (bytes, reference) = DocumentContentPublication.Prepare(job);
        var container = _blobServiceClient.GetBlobContainerClient(
            LocalFileArtifactStore.SanitizeContainerName(job.TenantId));
        var blob = container.GetBlobClient(
            $"{ProcessingResultCodec.ArtifactId(job, completedOnly: false)}/" +
            ProcessingResultCodec.ContentFileName(job, reference));
        if (reference.Kind == ProcessingResultCodec.InitialProcessing)
        {
            await VerifyExistingAsync();
            return reference;
        }
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        try
        {
            await blob.UploadAsync(BinaryData.FromBytes(bytes), new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            }, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            await VerifyExistingAsync();
        }
        return reference;

        async Task VerifyExistingAsync()
        {
            var response = await blob.DownloadStreamingAsync(new BlobDownloadOptions(), cancellationToken);
            await using var stream = response.Value.Content;
            await ProcessingResultCodec.ReadAsync(stream, job, cancellationToken, reference);
        }
    }

    public async Task<byte[]?> GetDocumentContentAsync(DocumentJob job, CancellationToken cancellationToken = default)
    {
        var reference = job.ContentSnapshot
            ?? throw new InvalidDataException("The document has no content reference.");
        var blob = _blobServiceClient.GetBlobContainerClient(
            LocalFileArtifactStore.SanitizeContainerName(job.TenantId)).GetBlobClient(
                $"{ProcessingResultCodec.ArtifactId(job, completedOnly: false)}/" +
                ProcessingResultCodec.ContentFileName(job, reference));
        try
        {
            var response = await blob.DownloadStreamingAsync(new BlobDownloadOptions(), cancellationToken);
            await using var stream = response.Value.Content;
            return await ProcessingResultCodec.ReadAsync(stream, job, cancellationToken, reference);
        }
        catch (RequestFailedException exception) when (exception.Status == 404) { return null; }
    }
}
