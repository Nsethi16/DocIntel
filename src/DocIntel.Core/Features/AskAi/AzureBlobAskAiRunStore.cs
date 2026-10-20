using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using DocIntel.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Features.AskAi;

/// <summary>Shared run snapshots with storage-enforced ownership across replicas.</summary>
public sealed class AzureBlobAskAiRunStore(
    BlobContainerClient container,
    ILogger<AzureBlobAskAiRunStore> logger,
    Func<BlobClient, string, BlobLeaseClient>? leaseFactory = null) : IAskAiRunStore
{
    private const int MaximumSnapshotBytes = 2 * 1024 * 1024;

    public async Task UpsertRunAsync(AskAiRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var blob = container.GetBlobClient(Name(run.TenantId, run.RunId));
        await SaveAsync(blob, run, null, cancellationToken);
    }

    public Task<AskAiRun?> GetRunAsync(
        string tenantId, string runId, CancellationToken cancellationToken = default) =>
        ReadAsync(container.GetBlobClient(Name(tenantId, runId)), tenantId, runId, cancellationToken);

    public async Task<IReadOnlyList<AskAiRun>> ListRunsByTenantAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        var result = new List<AskAiRun>();
        await foreach (var item in container.GetBlobsAsync(
            BlobTraits.Metadata, BlobStates.None, $"v1/{Hash(tenantId)}/", cancellationToken))
        {
            if (!TryIdentifiers(item, out var tenant, out var runId) ||
                !String.Equals(tenantId, tenant, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Stored Ask AI run metadata is invalid.");
            }
            var run = await GetRunAsync(tenant, runId, cancellationToken);
            if (run is not null) result.Add(run);
        }
        return result.OrderByDescending(run => run.CreatedAtUtc).Take(50).ToList();
    }

    public async Task<IAskAiRunLease?> TryAcquireAsync(
        string tenantId, string runId, CancellationToken cancellationToken = default)
    {
        var blob = container.GetBlobClient(Name(tenantId, runId));
        var id = Guid.NewGuid().ToString("D");
        var lease = leaseFactory?.Invoke(blob, id) ?? blob.GetBlobLeaseClient(id);
        try
        {
            await lease.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: cancellationToken);
        }
        catch (RequestFailedException exception) when (
            exception.Status == 404 || exception.ErrorCode is "LeaseAlreadyPresent" or "LeaseIsBreakingAndCannotBeAcquired")
        {
            return null;
        }
        try
        {
            var run = await ReadAsync(blob, tenantId, runId, cancellationToken);
            if (run is not null) return new AzureRunLease(blob, lease, id, run, logger);
        }
        catch
        {
            await ReleaseAsync(lease, runId, logger);
            throw;
        }
        await ReleaseAsync(lease, runId, logger);
        return null;
    }

    public async Task<IReadOnlyList<AskAiQueueTicket>> ListUnfinishedRunsAsync(
        int maxCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxCount, 100);
        var tickets = new List<AskAiQueueTicket>();
        await foreach (var item in container.GetBlobsAsync(
            BlobTraits.Metadata, BlobStates.None, "v1/", cancellationToken))
        {
            if (item.Properties.LeaseState == LeaseState.Leased ||
                !item.Metadata.TryGetValue("status", out var status) ||
                status is not (nameof(JobStatus.Queued) or nameof(JobStatus.Running)))
            {
                continue;
            }
            if (!TryIdentifiers(item, out var tenantId, out var runId) ||
                !String.Equals(Name(tenantId, runId), item.Name, StringComparison.Ordinal))
            {
                logger.LogError("A stored Ask AI snapshot has invalid scheduling metadata.");
                continue;
            }
            tickets.Add(new AskAiQueueTicket(runId, tenantId));
            if (tickets.Count == maxCount) break;
        }
        return tickets;
    }

    private static string Hash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, 512);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string Name(string tenantId, string runId) => $"v1/{Hash(tenantId)}/{Hash(runId)}.json";

    private static bool TryIdentifiers(BlobItem item, out string tenantId, out string runId)
    {
        tenantId = runId = String.Empty;
        if (!item.Metadata.TryGetValue("tenant", out var tenant) || !item.Metadata.TryGetValue("run", out var run) ||
            tenant.Length > 3_000 || run.Length > 3_000) return false;
        try
        {
            tenantId = Encoding.UTF8.GetString(Convert.FromBase64String(tenant));
            runId = Encoding.UTF8.GetString(Convert.FromBase64String(run));
            return !String.IsNullOrWhiteSpace(tenantId) && !String.IsNullOrWhiteSpace(runId) &&
                tenantId.Length <= 512 && runId.Length <= 512;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task<AskAiRun?> ReadAsync(
        BlobClient blob, string tenantId, string runId, CancellationToken cancellationToken)
    {
        try
        {
            var download = await blob.DownloadStreamingAsync(
                new BlobDownloadOptions(), cancellationToken);
            await using var stream = download.Value.Content;
            if (download.Value.Details.ContentLength > MaximumSnapshotBytes)
            {
                throw new InvalidDataException("The stored Ask AI run exceeds the supported size.");
            }
            var run = await JsonSerializer.DeserializeAsync<AskAiRun>(stream, cancellationToken: cancellationToken);
            if (run is null || run.SchemaVersion != 1 || run.TenantId != tenantId || run.RunId != runId)
            {
                throw new InvalidDataException("Stored Ask AI run identity or schema is invalid.");
            }
            return run;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    private static async Task SaveAsync(
        BlobClient blob, AskAiRun run, string? leaseId, CancellationToken cancellationToken)
    {
        if (run.SchemaVersion != 1) throw new InvalidDataException("Unsupported Ask AI snapshot schema.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(run);
        if (bytes.Length > MaximumSnapshotBytes) throw new InvalidDataException("The Ask AI snapshot is too large.");
        await blob.UploadAsync(BinaryData.FromBytes(bytes), new BlobUploadOptions
        {
            Conditions = leaseId is null ? null : new BlobRequestConditions { LeaseId = leaseId },
            Metadata = new Dictionary<string, string>
            {
                ["status"] = run.Status.ToString(),
                ["tenant"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(run.TenantId)),
                ["run"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(run.RunId))
            }
        }, cancellationToken);
    }

    private static async Task ReleaseAsync(BlobLeaseClient lease, string runId, ILogger logger)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await lease.ReleaseAsync(cancellationToken: timeout.Token);
        }
        catch (Exception exception) when (exception is RequestFailedException or HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning("Could not release ownership for run {RunId}; the finite lease will expire. Error: {ErrorType}.",
                runId, exception.GetType().Name);
        }
    }

    private sealed class AzureRunLease : IAskAiRunLease
    {
        private readonly BlobClient _blob;
        private readonly BlobLeaseClient _lease;
        private readonly string _leaseId;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lost = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _operations = new(1, 1);
        private readonly Task _renewal;
        private bool _disposed;

        public AzureRunLease(BlobClient blob, BlobLeaseClient lease, string leaseId, AskAiRun run, ILogger logger)
        {
            _blob = blob;
            _lease = lease;
            _leaseId = leaseId;
            Run = run;
            _logger = logger;
            _renewal = RenewAsync();
        }

        public AskAiRun Run { get; }
        public CancellationToken OwnershipLostToken => _lost.Token;

        public async Task SaveAsync(CancellationToken cancellationToken = default)
        {
            await _operations.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _lost.Token.ThrowIfCancellationRequested();
                await AzureBlobAskAiRunStore.SaveAsync(_blob, Run, _leaseId, cancellationToken);
            }
            catch (RequestFailedException exception) when (exception.Status is 409 or 412)
            {
                _lost.Cancel();
                throw new OperationCanceledException("Ask AI ownership was lost.", exception, _lost.Token);
            }
            finally
            {
                _operations.Release();
            }
        }

        private async Task RenewAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), _stop.Token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await _lease.RenewAsync(cancellationToken: timeout.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning("Run {RunId} could not renew ownership: {ErrorType}.", Run.RunId, exception.GetType().Name);
                _lost.Cancel();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _renewal;
            await _operations.WaitAsync();
            try
            {
                if (!_disposed)
                {
                    _disposed = true;
                    if (!_lost.IsCancellationRequested)
                    {
                        await ReleaseAsync(_lease, Run.RunId, _logger);
                    }
                }
            }
            finally
            {
                _operations.Release();
            }
        }
    }
}
