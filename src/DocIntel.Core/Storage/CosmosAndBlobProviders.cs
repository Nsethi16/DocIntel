using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Evidence;
using DocIntel.Core.Ocr;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocIntel.Core.Storage;

/// <summary>
/// Persists binary PDF artifacts (source PDFs and split PDF slices) to per-tenant Blob containers
/// (<c>tenant-{tenantId}</c>) in Azure Blob Storage, or to local disk when running offline.
/// </summary>
public interface IArtifactStore
{
    Task<OcrPageResult?> GetOcrCacheAsync(string tenantId, string originalSha256, string providerFingerprint,
        int pageNumber, CancellationToken cancellationToken = default);

    Task<bool> TrySaveOcrCacheAsync(OcrCacheEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Publishes immutable source-layout evidence and returns its SHA-256.</summary>
    Task<string> SaveLayoutAsync(DocumentLayout layout, CancellationToken cancellationToken = default);

    /// <summary>Reads source-bound layout evidence, rejecting content or identity mismatches.</summary>
    Task<DocumentLayout?> GetLayoutAsync(string tenantId, string documentId,
        string expectedSha256, string originalSha256, CancellationToken cancellationToken = default);

    Task<string> SaveOriginalPdfAsync(
        string tenantId, string documentId, byte[] pdfBytes, CancellationToken cancellationToken = default);

    Task<byte[]?> GetOriginalPdfAsync(
        string tenantId, string documentId, CancellationToken cancellationToken = default);

    Task<string> SaveSlicePdfAsync(
        string tenantId,
        string documentId,
        int sliceIndex,
        byte[] pdfBytes,
        CancellationToken cancellationToken = default,
        bool isTranslated = false);

    Task<byte[]?> GetSlicePdfAsync(
        string tenantId,
        string documentId,
        int sliceIndex,
        CancellationToken cancellationToken = default,
        bool isTranslated = false);
}

/// <summary>
/// Local file-system implementation of <see cref="IArtifactStore"/> used for zero-Docker local development.
/// </summary>
public sealed partial class LocalFileArtifactStore : IArtifactStore
{
    private readonly string _baseDirectory;

    public LocalFileArtifactStore(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _baseDirectory = baseDirectory;
    }

    public async Task<string> SaveOriginalPdfAsync(
        string tenantId, string documentId, byte[] pdfBytes, CancellationToken cancellationToken = default)
    {
        ValidateDocumentKey(documentId, 0);
        ValidateOriginal(pdfBytes);
        return await WriteImmutableAsync(tenantId, documentId, "original.pdf", pdfBytes, cancellationToken);
    }

    public async Task<byte[]?> GetOriginalPdfAsync(
        string tenantId, string documentId, CancellationToken cancellationToken = default)
    {
        ValidateDocumentKey(documentId, 0);
        var path = Path.Combine(_baseDirectory, SanitizeContainerName(tenantId), documentId, "original.pdf");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 25 * 1024 * 1024)
            throw new InvalidDataException("The stored original PDF exceeds the supported size.");
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    internal static void ValidateOriginal(byte[] pdfBytes)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        if (pdfBytes.Length is < 5 or > 25 * 1024 * 1024 || !pdfBytes.AsSpan().StartsWith("%PDF-"u8))
            throw new InvalidDataException("An original artifact must be a PDF of at most 25 MiB.");
    }

    public async Task<string> SaveSlicePdfAsync(
        string tenantId,
        string documentId,
        int sliceIndex,
        byte[] pdfBytes,
        CancellationToken cancellationToken = default,
        bool isTranslated = false)
    {
        ValidateDocumentKey(documentId, sliceIndex);
        var containerFolder = Path.Combine(_baseDirectory, SanitizeContainerName(tenantId), documentId);
        Directory.CreateDirectory(containerFolder);

        var fileName = isTranslated ? $"slice-{sliceIndex + 1}-translated.pdf" : $"slice-{sliceIndex + 1}.pdf";
        var filePath = Path.Combine(containerFolder, fileName);
        await File.WriteAllBytesAsync(filePath, pdfBytes, cancellationToken).ConfigureAwait(false);
        return filePath;
    }

    public async Task<byte[]?> GetSlicePdfAsync(
        string tenantId,
        string documentId,
        int sliceIndex,
        CancellationToken cancellationToken = default,
        bool isTranslated = false)
    {
        ValidateDocumentKey(documentId, sliceIndex);
        var fileName = isTranslated ? $"slice-{sliceIndex + 1}-translated.pdf" : $"slice-{sliceIndex + 1}.pdf";
        var filePath = Path.Combine(_baseDirectory, SanitizeContainerName(tenantId), documentId, fileName);
        if (!File.Exists(filePath))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
    }

    internal static string SanitizeContainerName(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tenantId.Length, 512);
        var canonical = tenantId.Length <= 56 && tenantId[0] != '-' && tenantId[^1] != '-' &&
            !tenantId.Contains("--", StringComparison.Ordinal) && !tenantId.StartsWith("v2-", StringComparison.Ordinal) &&
            tenantId.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
        return canonical ? $"tenant-{tenantId}" :
            "tenant-v2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tenantId)))[..52]
                .ToLowerInvariant();
    }

    internal static void ValidateDocumentKey(string documentId, int sliceIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        if (documentId.Length > 128 || documentId.Any(c => !Char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        {
            throw new ArgumentException("The document ID must be an opaque alphanumeric, '-' or '_' key.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(sliceIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sliceIndex, Int32.MaxValue - 1);
    }
}

/// <summary>
/// Azure Blob Storage implementation of <see cref="IArtifactStore"/> using Managed Identity (<see cref="TokenCredential"/>).
/// Stores each tenant's artifacts inside a dedicated <c>tenant-{tenantId}</c> container.
/// </summary>
public sealed partial class BlobArtifactStore : IArtifactStore
{
    private readonly BlobServiceClient _blobServiceClient;

    public BlobArtifactStore(BlobServiceClient blobServiceClient)
    {
        _blobServiceClient = blobServiceClient ?? throw new ArgumentNullException(nameof(blobServiceClient));
    }

    public async Task<string> SaveOriginalPdfAsync(
        string tenantId, string documentId, byte[] pdfBytes, CancellationToken cancellationToken = default)
    {
        LocalFileArtifactStore.ValidateDocumentKey(documentId, 0);
        LocalFileArtifactStore.ValidateOriginal(pdfBytes);
        var container = _blobServiceClient.GetBlobContainerClient(LocalFileArtifactStore.SanitizeContainerName(tenantId));
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var blob = container.GetBlobClient($"{documentId}/original.pdf");
        await blob.UploadAsync(BinaryData.FromBytes(pdfBytes), new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = Azure.ETag.All },
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf" }
        }, cancellationToken);
        return blob.Uri.ToString();
    }

    public async Task<byte[]?> GetOriginalPdfAsync(
        string tenantId, string documentId, CancellationToken cancellationToken = default)
    {
        LocalFileArtifactStore.ValidateDocumentKey(documentId, 0);
        var blob = _blobServiceClient.GetBlobContainerClient(LocalFileArtifactStore.SanitizeContainerName(tenantId))
            .GetBlobClient($"{documentId}/original.pdf");
        try
        {
            var downloaded = await blob.DownloadStreamingAsync(new BlobDownloadOptions(), cancellationToken);
            await using var stream = downloaded.Value.Content;
            if (downloaded.Value.Details.ContentLength > 25 * 1024 * 1024)
                throw new InvalidDataException("The stored original PDF exceeds the supported size.");
            using var content = new MemoryStream();
            await stream.CopyToAsync(content, cancellationToken);
            return content.ToArray();
        }
        catch (Azure.RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task<string> SaveSlicePdfAsync(
        string tenantId,
        string documentId,
        int sliceIndex,
        byte[] pdfBytes,
        CancellationToken cancellationToken = default,
        bool isTranslated = false)
    {
        LocalFileArtifactStore.ValidateDocumentKey(documentId, sliceIndex);
        var containerName = LocalFileArtifactStore.SanitizeContainerName(tenantId);
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var suffix = isTranslated ? "-translated" : string.Empty;
        var blobName = $"{documentId}/slices/slice-{sliceIndex + 1}{suffix}.pdf";
        var blobClient = containerClient.GetBlobClient(blobName);

        using var ms = new MemoryStream(pdfBytes);
        await blobClient.UploadAsync(ms, overwrite: true, cancellationToken: cancellationToken).ConfigureAwait(false);

        return blobClient.Uri.ToString();
    }

    public async Task<byte[]?> GetSlicePdfAsync(
        string tenantId,
        string documentId,
        int sliceIndex,
        CancellationToken cancellationToken = default,
        bool isTranslated = false)
    {
        LocalFileArtifactStore.ValidateDocumentKey(documentId, sliceIndex);
        var containerName = LocalFileArtifactStore.SanitizeContainerName(tenantId);
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        var suffix = isTranslated ? "-translated" : string.Empty;
        var blobName = $"{documentId}/slices/slice-{sliceIndex + 1}{suffix}.pdf";
        var blobClient = containerClient.GetBlobClient(blobName);

        if (!await blobClient.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        using var ms = new MemoryStream();
        await blobClient.DownloadToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }
}

/// <summary>
/// Custom <see cref="CosmosSerializer"/> backed by <see cref="System.Text.Json"/> so C# records and
/// <see cref="JsonPropertyNameAttribute"/> annotations serialize identically in Cosmos DB and Minimal APIs.
/// </summary>
public sealed class SystemTextJsonCosmosSerializer : CosmosSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public override T FromStream<T>(Stream stream)
    {
        using (stream)
        {
            if (stream.CanSeek && stream.Length == 0)
            {
                return default!;
            }

            return JsonSerializer.Deserialize<T>(stream, Options)!;
        }
    }

    public override Stream ToStream<T>(T input)
    {
        var ms = new MemoryStream();
        JsonSerializer.Serialize(ms, input, Options);
        ms.Position = 0;
        return ms;
    }
}

/// <summary>
/// Azure Cosmos DB implementation of <see cref="IHarnessStore"/> using a shared <c>harnesses</c> container
/// partitioned by <c>/tenantId</c>.
/// </summary>
public sealed class CosmosHarnessStore : IHarnessStore
{
    private readonly Container _container;
    private readonly string _endpointDescription;
    private bool _seeded;

    public CosmosHarnessStore(CosmosClient cosmosClient, string databaseName = "DocIntel", string containerName = "harnesses")
    {
        ArgumentNullException.ThrowIfNull(cosmosClient);
        _container = cosmosClient.GetContainer(databaseName, containerName);
        _endpointDescription = $"CosmosDB ({cosmosClient.Endpoint} -> {databaseName}/{containerName} [PK: /tenantId])";
    }

    public string StorageDescription => _endpointDescription;

    public async Task<TenantHarness?> GetTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        await EnsureSeededAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var response = await _container.ReadItemAsync<TenantHarness>(
                tenantId,
                new PartitionKey(tenantId),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var resource = response.Resource;
            if (resource is not null)
            {
                TenantHarness.EnsureDefaultCapabilities(resource);
            }
            return resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<TenantHarness>> ListTenantsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken).ConfigureAwait(false);

        var query = new QueryDefinition("SELECT * FROM c");
        using var iterator = _container.GetItemQueryIterator<TenantHarness>(query);
        var results = new List<TenantHarness>();

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in page)
            {
                TenantHarness.EnsureDefaultCapabilities(item);
                results.Add(item);
            }
        }

        return results.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<TenantHarness> UpsertTenantAsync(TenantHarness harness, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentException.ThrowIfNullOrWhiteSpace(harness.TenantId);

        var existing = await GetTenantWithoutSeedCheckAsync(harness.TenantId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            harness.HarnessVersion = existing.HarnessVersion + 1;
        }
        else if (harness.HarnessVersion < 1)
        {
            harness.HarnessVersion = 1;
        }

        harness.Id = harness.TenantId;
        harness.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var response = await _container.UpsertItemAsync(
            harness,
            new PartitionKey(harness.TenantId),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return response.Resource;
    }

    private async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        if (_seeded) return;

        var query = new QueryDefinition("SELECT VALUE COUNT(1) FROM c");
        using var iterator = _container.GetItemQueryIterator<int>(query);
        var count = 0;
        if (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            count = page.FirstOrDefault();
        }

        if (count == 0)
        {
            foreach (var tenant in FileHarnessStore.CreateDefaultTenants())
            {
                tenant.Id = tenant.TenantId;
                await _container.UpsertItemAsync(
                    tenant,
                    new PartitionKey(tenant.TenantId),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        _seeded = true;
    }

    private async Task<TenantHarness?> GetTenantWithoutSeedCheckAsync(string tenantId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _container.ReadItemAsync<TenantHarness>(
                tenantId,
                new PartitionKey(tenantId),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}

/// <summary>
/// Azure Cosmos DB implementation of <see cref="IJobStore"/> using a shared <c>jobs</c> container
/// partitioned by <c>/tenantId</c>. Persists step-by-step workflow state transitions and results.
/// </summary>
public sealed partial class CosmosJobStore : IJobStore
{
    private readonly Container _container;
    private readonly TimeProvider _jobClock;
    private readonly ILogger _jobLogger;

    public CosmosJobStore(
        CosmosClient cosmosClient, string databaseName = "DocIntel", string containerName = "jobs",
        TimeProvider? clock = null, ILogger<CosmosJobStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(cosmosClient);
        _container = cosmosClient.GetContainer(databaseName, containerName);
        _jobClock = clock ?? TimeProvider.System;
        _jobLogger = logger ?? NullLogger<CosmosJobStore>.Instance;
    }

    public CosmosJobStore(Container container, TimeProvider? clock = null, ILogger<CosmosJobStore>? logger = null)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _jobClock = clock ?? TimeProvider.System;
        _jobLogger = logger ?? NullLogger<CosmosJobStore>.Instance;
    }

    /// <summary>Checks that the configured metadata container exists and is accessible to the service identity.</summary>
    public async Task CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        await _container.ReadContainerAsync(cancellationToken: cancellationToken);
    }

    public async Task UpsertJobAsync(DocumentJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.JobId);

        if (job.ProcessingOwner is not null)
        {
            if (job.ProcessingLeaseUntilUtc is null || job.ProcessingLeaseUntilUtc > _jobClock.GetUtcNow())
                throw new InvalidOperationException("Save an owned document through its processing lease.");
            job.ProcessingOwner = null;
            job.ProcessingLeaseUntilUtc = null;
        }
        var response = job.ETag is null
            ? await _container.CreateItemAsync(job, new PartitionKey(job.TenantId), cancellationToken: cancellationToken)
            : await _container.ReplaceItemAsync(job, job.JobId, new PartitionKey(job.TenantId),
                new ItemRequestOptions { IfMatchEtag = RequireETag(job.ETag) }, cancellationToken);
        job.ETag = RequireETag(response.ETag);
    }

    public async Task<DocumentJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default, string? tenantId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            try
            {
                var ptResponse = await _container.ReadItemAsync<DocumentJob>(
                    jobId,
                    new PartitionKey(tenantId),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (ptResponse.Resource.TenantId != tenantId || ptResponse.Resource.JobId != jobId)
                    throw new InvalidDataException("Stored document identity does not match its partition key.");
                ptResponse.Resource.ETag = RequireETag(ptResponse.ETag);
                return ptResponse.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        var query = new QueryDefinition("SELECT TOP 2 * FROM c WHERE c.id = @jobId")
            .WithParameter("@jobId", jobId);

        using var iterator = _container.GetItemQueryIterator<DocumentJob>(query);
        var matches = new List<DocumentJob>();
        while (iterator.HasMoreResults && matches.Count < 2)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            matches.AddRange(page.Take(2 - matches.Count));
        }
        if (matches.Count > 1) throw new InvalidOperationException("A tenant ID is required for this document ID.");
        return matches.FirstOrDefault();
    }

    private static string RequireETag(string? etag) =>
        !String.IsNullOrWhiteSpace(etag) && etag != "*" ? etag :
            throw new InvalidDataException("A specific document version is required for a fenced storage operation.");

    public async Task<IReadOnlyList<DocumentJob>> ListJobsByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var query = new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @tenantId")
            .WithParameter("@tenantId", tenantId);

        using var iterator = _container.GetItemQueryIterator<DocumentJob>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<DocumentJob>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            results.AddRange(page);
        }

        return results.OrderByDescending(j => j.CreatedAtUtc).ToList();
    }

    /// <inheritdoc />
    public async Task<DocumentQueryPage> SearchDocumentsAsync(
        string tenantId, DocumentQuery query, CancellationToken cancellationToken = default)
    {
        var compiled = DocumentQueryCompiler.Compile(tenantId, query);
        var definition = new QueryDefinition(compiled.Sql);
        foreach (var parameter in compiled.Parameters)
        {
            definition.WithParameter(parameter.Key, parameter.Value);
        }

        using var iterator = _container.GetItemQueryIterator<DocumentReference>(
            definition, query.ContinuationToken,
            new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId), MaxItemCount = query.PageSize });
        var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
        return new DocumentQueryPage(page.ToList(), page.ContinuationToken);
    }

}
