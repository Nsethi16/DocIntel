using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using Microsoft.Azure.Cosmos;

namespace DocIntel.Core.Storage;

/// <summary>
/// Persists binary PDF artifacts (source PDFs and split PDF slices) to per-tenant Blob containers
/// (<c>tenant-{tenantId}</c>) in Azure Blob Storage, or to local disk when running offline.
/// </summary>
public interface IArtifactStore
{
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
public sealed class LocalFileArtifactStore : IArtifactStore
{
    private readonly string _baseDirectory;

    public LocalFileArtifactStore(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _baseDirectory = baseDirectory;
    }

    public async Task<string> SaveSlicePdfAsync(
        string tenantId,
        string documentId,
        int sliceIndex,
        byte[] pdfBytes,
        CancellationToken cancellationToken = default,
        bool isTranslated = false)
    {
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
        var cleaned = new string((tenantId ?? "default")
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');
        var name = $"tenant-{cleaned}";
        return name.Length > 63 ? name[..63].TrimEnd('-') : name;
    }
}

/// <summary>
/// Azure Blob Storage implementation of <see cref="IArtifactStore"/> using Managed Identity (<see cref="TokenCredential"/>).
/// Stores each tenant's artifacts inside a dedicated <c>tenant-{tenantId}</c> container.
/// </summary>
public sealed class BlobArtifactStore : IArtifactStore
{
    private readonly BlobServiceClient _blobServiceClient;

    public BlobArtifactStore(BlobServiceClient blobServiceClient)
    {
        _blobServiceClient = blobServiceClient ?? throw new ArgumentNullException(nameof(blobServiceClient));
    }

    public async Task<string> SaveSlicePdfAsync(
        string tenantId,
        string documentId,
        int sliceIndex,
        byte[] pdfBytes,
        CancellationToken cancellationToken = default,
        bool isTranslated = false)
    {
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
public sealed class CosmosJobStore : IJobStore
{
    private readonly Container _container;

    public CosmosJobStore(CosmosClient cosmosClient, string databaseName = "DocIntel", string containerName = "jobs")
    {
        ArgumentNullException.ThrowIfNull(cosmosClient);
        _container = cosmosClient.GetContainer(databaseName, containerName);
    }

    public async Task UpsertJobAsync(DocumentJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.TenantId);

        await _container.UpsertItemAsync(
            job,
            new PartitionKey(job.TenantId),
            cancellationToken: cancellationToken).ConfigureAwait(false);
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
                return ptResponse.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        var query = new QueryDefinition("SELECT * FROM c WHERE c.id = @jobId")
            .WithParameter("@jobId", jobId);

        using var iterator = _container.GetItemQueryIterator<DocumentJob>(query);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            var match = page.FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

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

    public async Task<(IReadOnlyList<string> JsonRows, IReadOnlyList<string> MatchedJobIds)> ExecuteReadOnlyQueryAsync(
        string tenantId,
        string cosmosSqlQuery,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        InMemoryJobStore.ValidateReadOnlyCosmosSql(cosmosSqlQuery);

        var rows = new List<string>();
        var jobIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var queryDef = new QueryDefinition(cosmosSqlQuery);
            using var iterator = _container.GetItemQueryIterator<JsonElement>(
                queryDef,
                requestOptions: new QueryRequestOptions
                {
                    PartitionKey = new PartitionKey(tenantId),
                    MaxItemCount = 50
                });

            while (iterator.HasMoreResults && rows.Count < 50)
            {
                var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                foreach (var item in page)
                {
                    var rawJson = item.GetRawText();
                    rows.Add(rawJson);

                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        if (item.TryGetProperty("jobId", out var jIdProp) && jIdProp.ValueKind == JsonValueKind.String)
                        {
                            jobIds.Add(jIdProp.GetString()!);
                        }
                        else if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                        {
                            jobIds.Add(idProp.GetString()!);
                        }
                    }
                }
            }
        }
        catch (CosmosException)
        {
            // Fallback to partition-scoped full job scan if LLM-generated SQL had an unsupported Cosmos syntax construct
            var allJobs = await ListJobsByTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
            var memStore = new InMemoryJobStore();
            foreach (var j in allJobs)
            {
                await memStore.UpsertJobAsync(j, cancellationToken).ConfigureAwait(false);
            }
            return await memStore.ExecuteReadOnlyQueryAsync(tenantId, cosmosSqlQuery, cancellationToken).ConfigureAwait(false);
        }

        return (rows, jobIds.ToList());
    }
}
