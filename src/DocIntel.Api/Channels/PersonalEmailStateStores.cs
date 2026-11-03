using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.DataProtection;

namespace DocIntel.Api.Channels;

internal static class PersonalEmailSerialization
{
    internal const int MaximumBytes = 2 * 1024 * 1024;
    internal static byte[] Protect(PersonalEmailState state, IDataProtectionProvider protection)
    {
        state.Validate(state.TenantId);
        var plain = JsonSerializer.SerializeToUtf8Bytes(state);
        if (plain.Length > MaximumBytes)
            throw new InvalidDataException("Personal email storage exceeded its supported size.");
        try { return Protector(protection, state.TenantId).Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal static PersonalEmailState Unprotect(
        byte[] protectedBytes, string tenantId, IDataProtectionProvider protection)
    {
        if (protectedBytes.Length > MaximumBytes * 2)
            throw new InvalidDataException("Personal email storage exceeded its supported size.");
        var plain = Protector(protection, tenantId).Unprotect(protectedBytes);
        try
        {
            if (plain.Length > MaximumBytes)
                throw new InvalidDataException("Personal email storage exceeded its supported size.");
            var state = JsonSerializer.Deserialize<PersonalEmailState>(plain)
                ?? throw new InvalidDataException("Personal email storage was empty.");
            state.Validate(tenantId);
            return state;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal static string Key(string tenantId)
    {
        if (!PersonalEmailState.ValidTenant(tenantId))
            throw new ArgumentException("A valid workspace is required.", nameof(tenantId));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tenantId)));
    }

    private static IDataProtector Protector(IDataProtectionProvider protection, string tenantId) =>
        protection.CreateProtector("DocIntel.PersonalEmail.v1", Key(tenantId));
}

/// <summary>Private, protected, atomic local state. Production replicas use the shared Blob implementation.</summary>
public sealed class FilePersonalEmailStateStore(string directory, IDataProtectionProvider protection) :
    IPersonalEmailStateStore
{
    public async Task<PersonalEmailState> GetAsync(string tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(PathFor(tenantId), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > PersonalEmailSerialization.MaximumBytes * 2)
                throw new InvalidDataException("Personal email storage exceeded its supported size.");
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            return PersonalEmailSerialization.Unprotect(buffer.ToArray(), tenantId, protection);
        }
        catch (FileNotFoundException) { return new PersonalEmailState { TenantId = tenantId }; }
        catch (DirectoryNotFoundException) { return new PersonalEmailState { TenantId = tenantId }; }
    }

    public async Task SaveAsync(
        PersonalEmailState state, long expectedVersion, CancellationToken cancellationToken)
    {
        var bytes = PersonalEmailSerialization.Protect(state, protection);
        var path = PathFor(state.TenantId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(5));
        await using var gate = await LockAsync(path + ".lock", wait.Token);
        var current = await GetAsync(state.TenantId, cancellationToken);
        if (current.Version != expectedVersion || state.Version != checked(expectedVersion + 1))
            throw new PersonalEmailStateConflictException();
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

    private string PathFor(string tenantId) =>
        Path.Combine(Path.GetFullPath(directory), PersonalEmailSerialization.Key(tenantId) + ".protected");

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

/// <summary>Encrypted OAuth state in a private execution-container prefix, fenced with both version and ETag.</summary>
public sealed class BlobPersonalEmailStateStore(BlobContainerClient container, IDataProtectionProvider protection) :
    IPersonalEmailStateStore
{
    public async Task<PersonalEmailState> GetAsync(string tenantId, CancellationToken cancellationToken) =>
        (await ReadAsync(tenantId, cancellationToken)).State;

    public async Task SaveAsync(
        PersonalEmailState state, long expectedVersion, CancellationToken cancellationToken)
    {
        var bytes = PersonalEmailSerialization.Protect(state, protection);
        var current = await ReadAsync(state.TenantId, cancellationToken);
        if (current.State.Version != expectedVersion || state.Version != checked(expectedVersion + 1))
            throw new PersonalEmailStateConflictException();
        try
        {
            await Blob(state.TenantId).UploadAsync(BinaryData.FromBytes(bytes), new BlobUploadOptions
            {
                Conditions = current.ETag is { } tag
                    ? new BlobRequestConditions { IfMatch = tag }
                    : new BlobRequestConditions { IfNoneMatch = ETag.All }
            }, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        { throw new PersonalEmailStateConflictException(); }
    }

    private async Task<(PersonalEmailState State, ETag? ETag)> ReadAsync(
        string tenantId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await Blob(tenantId).DownloadStreamingAsync(cancellationToken: cancellationToken);
            await using var stream = response.Value.Content;
            if (response.Value.Details.ContentLength > PersonalEmailSerialization.MaximumBytes * 2)
                throw new InvalidDataException("Personal email storage exceeded its supported size.");
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            return (PersonalEmailSerialization.Unprotect(buffer.ToArray(), tenantId, protection),
                response.Value.Details.ETag);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        { return (new PersonalEmailState { TenantId = tenantId }, null); }
    }

    private BlobClient Blob(string tenantId) =>
        container.GetBlobClient("personal-email/v1/" + PersonalEmailSerialization.Key(tenantId) + ".protected");
}
