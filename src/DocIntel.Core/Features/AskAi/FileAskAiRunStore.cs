using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocIntel.Core.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocIntel.Core.Features.AskAi;

internal sealed class OwnedAskAiRunLease(
    AskAiRun run,
    Func<CancellationToken, Task> save,
    Func<ValueTask> release) : IAskAiRunLease
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private bool _disposed;

    public AskAiRun Run => run;

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await save(cancellationToken);
        }
        finally
        {
            _operations.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                await release();
            }
        }
        finally
        {
            _operations.Release();
        }
    }
}

/// <summary>
/// Single-host run persistence using atomic snapshots and OS-owned process locks. The directory must
/// be service-owned, on a filesystem supporting exclusive locks and atomic same-directory replacement.
/// </summary>
public sealed class FileAskAiRunStore : IAskAiRunStore
{
    private const int MaximumSnapshotBytes = 2 * 1024 * 1024;
    private readonly string _directory;
    private readonly ILogger<FileAskAiRunStore> _logger;

    public FileAskAiRunStore(string directory, ILogger<FileAskAiRunStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _logger = logger ?? NullLogger<FileAskAiRunStore>.Instance;
        Directory.CreateDirectory(_directory);
    }

    public async Task UpsertRunAsync(AskAiRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var path = GetPath(run.TenantId, run.RunId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var gate = TryOpenLock(path)
            ?? throw new InvalidOperationException("This run is leased; save through the owning lease.");
        await WriteAsync(path, run, cancellationToken);
    }

    public async Task<AskAiRun?> GetRunAsync(
        string tenantId, string runId, CancellationToken cancellationToken = default)
    {
        var run = await ReadAsync(GetPath(tenantId, runId), cancellationToken);
        if (run is not null && (run.SchemaVersion != 1 ||
                               !String.Equals(run.TenantId, tenantId, StringComparison.Ordinal) ||
                               !String.Equals(run.RunId, runId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Stored Ask AI run identity does not match its storage key.");
        }
        return run;
    }

    public async Task<IReadOnlyList<AskAiRun>> ListRunsByTenantAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(_directory, HashId(tenantId));
        if (!Directory.Exists(directory))
        {
            return [];
        }
        var runs = new List<AskAiRun>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var run = await ReadAsync(path, cancellationToken);
            if (run is not null)
            {
                ValidateStorageKey(run, path);
                runs.Add(run);
            }
        }
        return runs.OrderByDescending(run => run.CreatedAtUtc).Take(50).ToList();
    }

    public async Task<IAskAiRunLease?> TryAcquireAsync(
        string tenantId, string runId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(tenantId, runId);
        if (!Directory.Exists(Path.GetDirectoryName(path)))
        {
            return null;
        }
        var gate = TryOpenLock(path);
        if (gate is null)
        {
            return null;
        }
        try
        {
            var run = await GetRunAsync(tenantId, runId, cancellationToken);
            if (run is not null)
            {
                return new OwnedAskAiRunLease(
                    run, ct => WriteAsync(path, run, ct), () => gate.DisposeAsync());
            }
        }
        catch
        {
            await gate.DisposeAsync();
            throw;
        }
        await gate.DisposeAsync();
        return null;
    }

    public async Task<IReadOnlyList<AskAiQueueTicket>> ListUnfinishedRunsAsync(
        int maxCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxCount, 100);
        var tickets = new List<AskAiQueueTicket>();
        var enumeration = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var tenantDirectory in Directory.EnumerateDirectories(_directory, "*", enumeration))
        {
            foreach (var path in Directory.EnumerateFiles(tenantDirectory, "*.json", enumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var gate = TryOpenLock(path);
                if (gate is null)
                {
                    continue;
                }
                AskAiRun? run;
                try
                {
                    run = await ReadAsync(path, cancellationToken);
                    if (run is not null)
                    {
                        ValidateStorageKey(run, path);
                    }
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException)
                {
                    _logger.LogError("A corrupt Ask AI snapshot could not be scheduled: {Snapshot}.",
                        Path.GetFileName(path));
                    continue;
                }
                if (run is not null)
                {
                    if (run.Status is JobStatus.Queued or JobStatus.Running)
                    {
                        tickets.Add(new AskAiQueueTicket(run.RunId, run.TenantId));
                        if (tickets.Count == maxCount)
                        {
                            return tickets;
                        }
                    }
                }
            }
        }
        return tickets;
    }

    private string GetPath(string tenantId, string runId)
    {
        var directory = Path.Combine(_directory, HashId(tenantId));
        return Path.Combine(directory, HashId(runId) + ".json");
    }

    private static string HashId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(id.Length, 512);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    }

    private void ValidateStorageKey(AskAiRun run, string path)
    {
        if (run.SchemaVersion != 1 || String.IsNullOrWhiteSpace(run.TenantId) || String.IsNullOrWhiteSpace(run.RunId) ||
            run.TenantId.Length > 512 || run.RunId.Length > 512 ||
            !String.Equals(GetPath(run.TenantId, run.RunId), path, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Stored Ask AI run identity does not match its storage key.");
        }
    }

    private static FileStream? TryOpenLock(string path)
    {
        try
        {
            return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
        {
            return null;
        }
    }

    private static async Task<AskAiRun?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.GetDirectoryName(path)) ||
            (!File.Exists(path) && !File.Exists(path + ".lock")))
        {
            return null;
        }
        // Windows replacement and polling need a short lock, separate from execution ownership.
        await using var snapshotLock = await AcquireSnapshotLockAsync(path, cancellationToken);
        return await ReadSnapshotAsync(path, cancellationToken);
    }

    private static async Task<FileStream> AcquireSnapshotLockAsync(string path, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gate = TryOpenLock(path + ".snapshot");
            if (gate is not null)
            {
                return gate;
            }
            if (timer.Elapsed >= TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException("Timed out waiting for an Ask AI snapshot operation.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    private static async Task<AskAiRun?> ReadSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (stream.Length > MaximumSnapshotBytes)
            {
                throw new InvalidDataException("The stored Ask AI run exceeds the supported size.");
            }
            return await JsonSerializer.DeserializeAsync<AskAiRun>(stream, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("The stored Ask AI run is empty.");
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static async Task WriteAsync(string path, AskAiRun run, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(run);
        if (bytes.Length > MaximumSnapshotBytes)
        {
            throw new InvalidDataException("The Ask AI run exceeds the supported snapshot size.");
        }
        await using var snapshotLock = await AcquireSnapshotLockAsync(path, cancellationToken);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Replace(temporaryPath, path, destinationBackupFileName: null);
                    }
                    else
                    {
                        File.Move(temporaryPath, path);
                    }
                    break;
                }
                catch (IOException exception) when (
                    (exception.HResult & 0xffff) is 11 or 32 or 33 && attempt < 6)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10 * (1 << attempt)), cancellationToken);
                }
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
