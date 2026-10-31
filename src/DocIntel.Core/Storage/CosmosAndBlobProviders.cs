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
using DocIntel.Core.Authorization;
using DocIntel.Core.Evidence;
using DocIntel.Core.Ocr;
using DocIntel.Core.Export;
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
    /// <summary>Archives complete checkpoint/current outputs, reusing an identical completed result when available.</summary>
    Task<DocumentContentReference> SaveDocumentContentAsync(
        DocumentJob job, CancellationToken cancellationToken = default);

    /// <summary>Reads only the job's content-addressed, source/harness/attempt-bound full output.</summary>
    Task<byte[]?> GetDocumentContentAsync(DocumentJob job, CancellationToken cancellationToken = default);

    /// <summary>Archives the completed initial processing result independently of the job database.</summary>
    Task<string> SaveProcessingResultAsync(DocumentJob job, CancellationToken cancellationToken = default);

    /// <summary>Retrieves only the active attempt's source-bound, content-verified processing snapshot.</summary>
    Task<byte[]?> GetProcessingResultAsync(DocumentJob job, CancellationToken cancellationToken = default);

    /// <summary>Publishes an immutable, reference-only export record before bytes are released to the caller.</summary>
    Task SaveExportAuditAsync(ExportAuditEvent audit, CancellationToken cancellationToken = default);

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

    internal static byte[] Serialize<T>(T input) => JsonSerializer.SerializeToUtf8Bytes(input, Options);

    internal static T Deserialize<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Options)!;

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
    private readonly HarnessBootstrapOptions _bootstrap;
    private readonly SemaphoreSlim _seedGate = new(1, 1);
    private bool _seeded;

    public CosmosHarnessStore(
        CosmosClient cosmosClient, string databaseName = "DocIntel", string containerName = "harnesses",
        HarnessBootstrapOptions? bootstrapOptions = null)
    {
        ArgumentNullException.ThrowIfNull(cosmosClient);
        _bootstrap = bootstrapOptions ?? new HarnessBootstrapOptions();
        _bootstrap.Validate();
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
        await EnsureSeededAsync(cancellationToken).ConfigureAwait(false);

        var saved = JsonSerializer.Deserialize<TenantHarness>(JsonSerializer.Serialize(harness))!;
        var existing = await GetTenantWithoutSeedCheckAsync(harness.TenantId, cancellationToken);
        saved.UpdatedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            if (existing is null)
            {
                if (harness.HarnessVersion != 0) throw new HarnessConflictException();
                saved.HarnessVersion = 1;
                var created = await _container.CreateItemAsync(saved, new PartitionKey(saved.TenantId),
                    cancellationToken: cancellationToken);
                return created.Resource;
            }
            if (harness.HarnessVersion != existing.Resource.HarnessVersion) throw new HarnessConflictException();
            if (String.IsNullOrWhiteSpace(existing.ETag) || existing.ETag == "*")
                throw new InvalidDataException("The stored harness has no usable concurrency version.");
            saved.HarnessVersion = checked(existing.Resource.HarnessVersion + 1);
            var replaced = await _container.ReplaceItemAsync(saved, saved.TenantId, new PartitionKey(saved.TenantId),
                new ItemRequestOptions { IfMatchEtag = existing.ETag }, cancellationToken);
            return replaced.Resource;
        }
        catch (CosmosException exception) when (exception.StatusCode is
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            throw new HarnessConflictException();
        }
    }

    private async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _seeded)) return;
        await _seedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_seeded) return;
            var existing = new List<string>();
            using (var iterator = _container.GetItemQueryIterator<string>(
                new QueryDefinition("SELECT VALUE c.tenantId FROM c")))
            {
                while (iterator.HasMoreResults)
                {
                    var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                    existing.AddRange(page);
                }
            }

            foreach (var tenant in _bootstrap.CreateMissingTenants(existing))
            {
                try
                {
                    await _container.CreateItemAsync(tenant, new PartitionKey(tenant.TenantId),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
                {
                    // Another replica has already initialized this tenant; never reset its configuration.
                }
            }
            Volatile.Write(ref _seeded, true);
        }
        finally
        {
            _seedGate.Release();
        }
    }

    private async Task<ItemResponse<TenantHarness>?> GetTenantWithoutSeedCheckAsync(
        string tenantId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _container.ReadItemAsync<TenantHarness>(
                tenantId,
                new PartitionKey(tenantId),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return response;
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
    private readonly IArtifactStore? _jobArtifacts;
    private readonly ITenantAccessResolver? _jobAccess;

    public CosmosJobStore(
        CosmosClient cosmosClient, string databaseName = "DocIntel", string containerName = "jobs",
        TimeProvider? clock = null, ILogger<CosmosJobStore>? logger = null, IArtifactStore? artifacts = null,
        ITenantAccessResolver? access = null)
    {
        ArgumentNullException.ThrowIfNull(cosmosClient);
        _container = cosmosClient.GetContainer(databaseName, containerName);
        _jobClock = clock ?? TimeProvider.System;
        _jobLogger = logger ?? NullLogger<CosmosJobStore>.Instance;
        _jobArtifacts = artifacts;
        _jobAccess = access;
    }

    public CosmosJobStore(Container container, TimeProvider? clock = null, ILogger<CosmosJobStore>? logger = null,
        IArtifactStore? artifacts = null, ITenantAccessResolver? access = null)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _jobClock = clock ?? TimeProvider.System;
        _jobLogger = logger ?? NullLogger<CosmosJobStore>.Instance;
        _jobArtifacts = artifacts;
        _jobAccess = access;
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
        if (job.ETag is not null) RequireETag(job.ETag);

        if (job.ProcessingOwner is not null)
        {
            if (job.ProcessingLeaseUntilUtc is null || job.ProcessingLeaseUntilUtc > _jobClock.GetUtcNow())
                throw new InvalidOperationException("Save an owned document through its processing lease.");
            job.ProcessingOwner = null;
            job.ProcessingLeaseUntilUtc = null;
        }
        var prepared = await PrepareJobAsync(job, cancellationToken);
        var response = await WriteJobAsync(prepared, job.ETag, cancellationToken);
        ApplyStoredState(job, prepared.Job, RequireETag(response.ETag));
        ThrowIfDocumentLimit(prepared);
    }

    public async Task<DocumentJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default, string? tenantId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            var stored = await GetStoredJobAsync(jobId, tenantId, cancellationToken);
            return stored is null || stored.Status == JobStatus.Deleted
                ? null : await HydrateJobAsync(stored, cancellationToken);
        }

        var query = new QueryDefinition("SELECT TOP 2 * FROM c WHERE c.id = @jobId AND c.status != @deleted")
            .WithParameter("@jobId", jobId).WithParameter("@deleted", nameof(JobStatus.Deleted));

        using var iterator = _container.GetItemQueryIterator<DocumentJob>(query);
        var matches = new List<DocumentJob>();
        while (iterator.HasMoreResults && matches.Count < 2)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            matches.AddRange(page.Take(2 - matches.Count));
        }
        if (matches.Count > 1) throw new InvalidOperationException("A tenant ID is required for this document ID.");
        var match = matches.FirstOrDefault(job => job.Status != JobStatus.Deleted);
        if (match is null) return null;
        if (match.JobId != jobId || String.IsNullOrWhiteSpace(match.TenantId))
            throw new InvalidDataException("Stored document identity does not match the requested document.");
        return await HydrateJobAsync(match, cancellationToken);
    }

    private static string RequireETag(string? etag) =>
        !String.IsNullOrWhiteSpace(etag) && etag != "*" ? etag :
            throw new InvalidDataException("A specific document version is required for a fenced storage operation.");

    public async Task<IReadOnlyList<DocumentJob>> ListJobsByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var query = new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @tenantId AND c.status != @deleted")
            .WithParameter("@tenantId", tenantId).WithParameter("@deleted", nameof(JobStatus.Deleted));

        using var iterator = _container.GetItemQueryIterator<DocumentJob>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<DocumentJob>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            if (page.Any(job => job.TenantId != tenantId))
                throw new InvalidDataException("Stored document identity does not match its partition key.");
            foreach (var job in page)
            {
                if (job.Status != JobStatus.Deleted)
                    results.Add(await HydrateJobAsync(job, cancellationToken));
            }
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
        return new DocumentQueryPage(page.ToList(), page.ContinuationToken)
        {
            TextSearchNotice = String.IsNullOrWhiteSpace(query.SearchText) ? null :
                "Text search covers complete inline text and bounded previews of artifact-backed documents. " +
                "A missing text match does not rule out content in a document; open it to inspect the full evidence."
        };
    }

}
