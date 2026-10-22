using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace DocIntel.Core.Authorization;

/// <summary>Atomic, version-checked single-host membership storage with inter-process serialization of edits.</summary>
public sealed class FileTenantUserStore(string directory) : ITenantUserStore
{
    public async Task<TenantUsersSnapshot> GetAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(tenantId);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > TenantUserSerialization.MaximumBytes)
                throw new InvalidDataException("Tenant access state exceeded its supported size.");
            var snapshot = await JsonSerializer.DeserializeAsync<TenantUsersSnapshot>(
                stream, TenantUserSerialization.Options, cancellationToken)
                ?? throw new InvalidDataException("Tenant access state was empty.");
            snapshot.Validate(tenantId);
            return snapshot;
        }
        catch (FileNotFoundException) { return new TenantUsersSnapshot { TenantId = tenantId }; }
        catch (DirectoryNotFoundException) { return new TenantUsersSnapshot { TenantId = tenantId }; }
    }

    public async Task SaveAsync(
        TenantUsersSnapshot snapshot, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var bytes = TenantUserSerialization.Serialize(snapshot);
        var path = PathFor(snapshot.TenantId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(5));
        await using var gate = await LockAsync(path + ".lock", wait.Token);
        var current = await GetAsync(snapshot.TenantId, cancellationToken);
        if (expectedVersion != current.Version || snapshot.Version != checked(expectedVersion + 1))
            throw new TenantUsersConflictException();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private string PathFor(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tenantId.Length, 128);
        return Path.Combine(Path.GetFullPath(directory),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tenantId))) + ".json");
    }

    private static async Task<FileStream> LockAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
            { await Task.Delay(25, cancellationToken); }
        }
    }
}

/// <summary>Shared access state with ETag fencing, using a private prefix in the existing execution container.</summary>
public sealed class BlobTenantUserStore(BlobContainerClient container) : ITenantUserStore
{
    public async Task<TenantUsersSnapshot> GetAsync(
        string tenantId, CancellationToken cancellationToken = default) =>
        (await ReadAsync(tenantId, cancellationToken)).Snapshot;

    public async Task SaveAsync(
        TenantUsersSnapshot snapshot, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var bytes = TenantUserSerialization.Serialize(snapshot);
        var current = await ReadAsync(snapshot.TenantId, cancellationToken);
        if (current.Snapshot.Version != expectedVersion || snapshot.Version != checked(expectedVersion + 1))
            throw new TenantUsersConflictException();
        try
        {
            await container.GetBlobClient(Name(snapshot.TenantId)).UploadAsync(BinaryData.FromBytes(bytes),
                new BlobUploadOptions
                {
                    Conditions = current.ETag is { } tag
                        ? new BlobRequestConditions { IfMatch = tag }
                        : new BlobRequestConditions { IfNoneMatch = ETag.All }
                }, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        { throw new TenantUsersConflictException(); }
    }

    private async Task<(TenantUsersSnapshot Snapshot, ETag? ETag)> ReadAsync(
        string tenantId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.GetBlobClient(Name(tenantId)).DownloadStreamingAsync(
                new BlobDownloadOptions(), cancellationToken);
            await using var stream = response.Value.Content;
            if (response.Value.Details.ContentLength > TenantUserSerialization.MaximumBytes)
                throw new InvalidDataException("Tenant access state exceeded its supported size.");
            var snapshot = await JsonSerializer.DeserializeAsync<TenantUsersSnapshot>(
                stream, TenantUserSerialization.Options, cancellationToken)
                ?? throw new InvalidDataException("Tenant access state was empty.");
            snapshot.Validate(tenantId);
            return (snapshot, response.Value.Details.ETag);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        { return (new TenantUsersSnapshot { TenantId = tenantId }, null); }
    }

    private static string Name(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tenantId.Length, 128);
        return "tenant-access/v1/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tenantId))) + ".json";
    }
}
