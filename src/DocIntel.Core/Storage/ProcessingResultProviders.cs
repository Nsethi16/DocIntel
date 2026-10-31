using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Storage.Blobs.Models;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;

namespace DocIntel.Core.Storage;

internal static class ProcessingResultCodec
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    public const string InitialProcessing = "InitialProcessing";
    public const string DocumentContent = "DocumentContent";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly string[] ContentProperties =
    [
        "processingHarness", "tenantDisplayName", "fileName", "totalPages", "strategyUsed", "modelIdUsed",
        "hasDevelopmentResults", "packetSummary", "extractedEntities", "dataExtractions",
        "promptColumnResults", "segments", "executionTrace", "reviewId"
    ];

    public static string ArtifactId(DocumentJob job, bool completedOnly = true)
    {
        ArgumentNullException.ThrowIfNull(job);
        LocalFileArtifactStore.ValidateDocumentKey(job.JobId, 0);
        var id = job.ArtifactDocumentId ?? job.JobId;
        LocalFileArtifactStore.ValidateDocumentKey(id, 0);
        if (String.IsNullOrWhiteSpace(job.TenantId) || job.AppliedHarnessVersion < 1 ||
            job.ExecutionAttempts < 0 || !IsHash(job.OriginalSha256) ||
            (completedOnly && (job.Status != JobStatus.Completed || job.CompletedAtUtc is null)) ||
            (job.ProcessingHarness is { } harness &&
                (harness.TenantId != job.TenantId || harness.HarnessVersion != job.AppliedHarnessVersion)))
            throw new InvalidDataException("The processing snapshot requires a matching source and accepted harness.");
        return id;
    }

    public static byte[] Serialize(DocumentJob job, string kind = InitialProcessing)
    {
        if (kind is not (InitialProcessing or DocumentContent))
            throw new InvalidDataException("The processing snapshot kind is unsupported.");
        var artifactId = ArtifactId(job, completedOnly: kind == InitialProcessing);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            snapshotKind = kind,
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
            job.TenantDisplayName,
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
            throw new DocumentStorageLimitException(DocumentStorageLimitException.ContentTooLarge,
                "The complete processing output exceeds the 16 MiB document-content archive limit.");
        return bytes;
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(Char.IsAsciiHexDigit);

    public static string HarnessHash(TenantHarness? harness) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(harness, Options));

    public static DocumentContentReference Reference(DocumentJob job, byte[] bytes, string kind) =>
        new(Hash(bytes), bytes.Length, HarnessHash(job.ProcessingHarness), kind);

    public static void ValidateReference(DocumentJob job, DocumentContentReference? reference)
    {
        if (reference is null || !IsHash(reference.Sha256) || !IsHash(reference.HarnessSha256) ||
            reference.SizeBytes is < 1 or > MaximumBytes ||
            reference.Kind is not (InitialProcessing or DocumentContent) ||
            (reference.Kind == InitialProcessing && reference.Sha256 != job.ProcessingResultSha256))
            throw new InvalidDataException("The document content reference is invalid or is not the active result.");
        ArtifactId(job, completedOnly: false);
    }

    public static void VerifyAcknowledgement(DocumentJob job, DocumentContentReference reference)
    {
        ValidateReference(job, reference);
        var expected = Reference(job, Serialize(job, reference.Kind), reference.Kind);
        if (reference != expected)
            throw new InvalidDataException("The complete document-content snapshot was not acknowledged.");
    }

    public static string ContentFileName(DocumentJob job, DocumentContentReference reference)
    {
        ValidateReference(job, reference);
        return reference.Kind == InitialProcessing ? "result.json" : $"content-{reference.Sha256}.json";
    }

    public static async Task<byte[]> ReadAsync(Stream stream, DocumentJob job, CancellationToken cancellationToken,
        DocumentContentReference? reference = null)
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
        ValidateSnapshot(bytes, job, reference);
        return bytes;
    }

    private static void ValidateSnapshot(byte[] bytes, DocumentJob job, DocumentContentReference? reference)
    {
        if (reference is not null) ValidateReference(job, reference);
        var expectedHash = reference?.Sha256 ?? job.ProcessingResultSha256;
        if (bytes.Length > MaximumBytes || !IsHash(expectedHash) || Hash(bytes) != expectedHash ||
            (reference is not null && bytes.Length != reference.SizeBytes))
            throw new InvalidDataException("The processing snapshot does not match its accepted content hash.");
        using var snapshot = JsonDocument.Parse(bytes);
        var root = snapshot.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
            root.GetProperty("snapshotKind").GetString() != (reference?.Kind ?? InitialProcessing) ||
            root.GetProperty("tenantId").GetString() != job.TenantId ||
            root.GetProperty("jobId").GetString() != job.JobId ||
            root.GetProperty("artifactDocumentId").GetString() != ArtifactId(job, completedOnly: reference is null) ||
            root.GetProperty("originalSha256").GetString() != job.OriginalSha256 ||
            root.GetProperty("layoutSha256").GetString() != job.LayoutSha256 ||
            root.GetProperty("appliedHarnessVersion").GetInt32() != job.AppliedHarnessVersion ||
            root.GetProperty("executionAttempts").GetInt32() != job.ExecutionAttempts)
            throw new InvalidDataException("The processing snapshot does not match the requested document/source.");
        var harness = root.GetProperty("processingHarness").Deserialize<TenantHarness>(Options);
        var harnessHash = HarnessHash(harness);
        if ((reference is not null && harnessHash != reference.HarnessSha256) ||
            (job.ProcessingHarness is not null && harnessHash != HarnessHash(job.ProcessingHarness)) ||
            (harness is not null &&
                (harness.TenantId != job.TenantId || harness.HarnessVersion != job.AppliedHarnessVersion)))
            throw new InvalidDataException("The processing snapshot does not match the accepted harness.");
    }

    public static DocumentJob Hydrate(DocumentJob metadata, byte[] bytes)
    {
        var reference = metadata.ContentSnapshot
            ?? throw new InvalidDataException("The document has no content reference.");
        ValidateSnapshot(bytes, metadata, reference);
        var hydrated = JsonSerializer.SerializeToNode(metadata, Options)!.AsObject();
        var content = JsonNode.Parse(bytes)!.AsObject();
        foreach (var property in ContentProperties)
        {
            if (content.TryGetPropertyValue(property, out var value))
                hydrated[property] = value?.DeepClone();
        }
        var job = hydrated.Deserialize<DocumentJob>(Options)
            ?? throw new InvalidDataException("The document-content snapshot is empty.");
        job.StoreVersion = metadata.StoreVersion;
        return job;
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
