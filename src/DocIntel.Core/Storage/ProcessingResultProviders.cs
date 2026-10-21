using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs.Models;
using DocIntel.Core.Jobs;

namespace DocIntel.Core.Storage;

internal static class ProcessingResultCodec
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string ArtifactId(DocumentJob job)
    {
        LocalFileArtifactStore.ValidateDocumentKey(job.JobId, 0);
        var id = job.ArtifactDocumentId ?? job.JobId;
        LocalFileArtifactStore.ValidateDocumentKey(id, 0);
        if (String.IsNullOrWhiteSpace(job.TenantId) || job.Status != JobStatus.Completed ||
            job.CompletedAtUtc is null || job.AppliedHarnessVersion < 1 ||
            job.OriginalSha256 is not { Length: 64 } || !job.OriginalSha256.All(Char.IsAsciiHexDigit))
            throw new InvalidDataException("Only a completed, source-bound processing result can be archived.");
        return id;
    }

    public static byte[] Serialize(DocumentJob job)
    {
        var artifactId = ArtifactId(job);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            snapshotKind = "InitialProcessing",
            job.TenantId,
            job.JobId,
            artifactDocumentId = artifactId,
            job.OriginalSha256,
            job.LayoutSha256,
            job.AppliedHarnessVersion,
            job.CompletedAtUtc,
            job.ExecutionAttempts,
            job.HasDevelopmentResults,
            job.ProcessingHarness,
            job.FileName,
            job.TotalPages,
            job.StrategyUsed,
            job.ModelIdUsed,
            job.PacketSummary,
            job.ExtractedEntities,
            job.DataExtractions,
            job.PromptColumnResults,
            job.Segments,
            job.ExecutionTrace,
            job.ReviewId
        }, Options);
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("The completed processing result exceeds its archive limit.");
        return bytes;
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static async Task<byte[]> ReadAsync(Stream stream, DocumentJob job, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (body.Length + read > MaximumBytes)
                throw new InvalidDataException("The processing snapshot exceeds its archive limit.");
            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var bytes = body.ToArray();
        if (job.ProcessingResultSha256 is not { Length: 64 } ||
            Hash(bytes) != job.ProcessingResultSha256)
            throw new InvalidDataException("The processing snapshot does not match its accepted content hash.");
        using var snapshot = JsonDocument.Parse(bytes);
        var root = snapshot.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
            root.GetProperty("snapshotKind").GetString() != "InitialProcessing" ||
            root.GetProperty("tenantId").GetString() != job.TenantId ||
            root.GetProperty("jobId").GetString() != job.JobId ||
            root.GetProperty("artifactDocumentId").GetString() != ArtifactId(job) ||
            root.GetProperty("originalSha256").GetString() != job.OriginalSha256 ||
            root.GetProperty("layoutSha256").GetString() != job.LayoutSha256 ||
            root.GetProperty("appliedHarnessVersion").GetInt32() != job.AppliedHarnessVersion)
            throw new InvalidDataException("The processing snapshot does not match the requested document/source.");
        return bytes;
    }
}

public sealed partial class LocalFileArtifactStore
{
    public async Task<string> SaveProcessingResultAsync(DocumentJob job, CancellationToken cancellationToken = default)
    {
        var bytes = ProcessingResultCodec.Serialize(job);
        await WriteImmutableAsync(job.TenantId, ProcessingResultCodec.ArtifactId(job),
            "result.json", bytes, cancellationToken);
        return ProcessingResultCodec.Hash(bytes);
    }

    public async Task<byte[]?> GetProcessingResultAsync(DocumentJob job, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_baseDirectory, SanitizeContainerName(job.TenantId),
            ProcessingResultCodec.ArtifactId(job), "result.json");
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await ProcessingResultCodec.ReadAsync(stream, job, cancellationToken);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}

public sealed partial class BlobArtifactStore
{
    public async Task<string> SaveProcessingResultAsync(DocumentJob job, CancellationToken cancellationToken = default)
    {
        var bytes = ProcessingResultCodec.Serialize(job);
        var container = _blobServiceClient.GetBlobContainerClient(LocalFileArtifactStore.SanitizeContainerName(job.TenantId));
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await container.GetBlobClient($"{ProcessingResultCodec.ArtifactId(job)}/result.json").UploadAsync(
            BinaryData.FromBytes(bytes), new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            }, cancellationToken);
        return ProcessingResultCodec.Hash(bytes);
    }

    public async Task<byte[]?> GetProcessingResultAsync(DocumentJob job, CancellationToken cancellationToken = default)
    {
        var blob = _blobServiceClient.GetBlobContainerClient(LocalFileArtifactStore.SanitizeContainerName(job.TenantId))
            .GetBlobClient($"{ProcessingResultCodec.ArtifactId(job)}/result.json");
        try
        {
            var response = await blob.DownloadStreamingAsync(new BlobDownloadOptions(), cancellationToken);
            await using var stream = response.Value.Content;
            return await ProcessingResultCodec.ReadAsync(stream, job, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 404) { return null; }
    }
}
