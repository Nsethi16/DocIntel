using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocIntel.Core.Authorization;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TenantUserRole { User, TenantAdmin }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TenantUserStatus { Active, Disabled, Removed }

/// <summary>App-owned tenant membership; corporate profile information stays in the enterprise directory.</summary>
public sealed record TenantUserAssignment(
    Guid DirectoryTenantId, Guid ObjectId, TenantUserRole Role, TenantUserStatus Status);

public sealed record TenantUserChange(Guid ObjectId, TenantUserRole Role, TenantUserStatus Status);

public sealed record TenantAccessAudit(
    long Version, DateTimeOffset AtUtc, ActorIdentity? Actor, bool LocalDevelopment,
    IReadOnlyList<TenantUserChange> Changes);

/// <summary>Versioned access state, persisted separately from document/harness snapshots and public run artifacts.</summary>
public sealed record TenantUsersSnapshot
{
    public required string TenantId { get; init; }
    public long Version { get; init; }
    public IReadOnlyList<TenantUserAssignment> Users { get; init; } = [];
    public IReadOnlyList<TenantAccessAudit> Audit { get; init; } = [];

    public void Validate(string tenantId)
    {
        if (TenantId != tenantId || String.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 128 ||
            Version < 0 || Users is null || Audit is null || Users.Count > 5_000 || Audit.Count > 5_000 ||
            Users.Any(user => user is null || user.DirectoryTenantId == Guid.Empty ||
                user.ObjectId == Guid.Empty || !Enum.IsDefined(user.Role) || !Enum.IsDefined(user.Status)) ||
            Users.GroupBy(user => (user.DirectoryTenantId, user.ObjectId)).Any(group => group.Count() != 1) ||
            Audit.Any(entry => entry is null || entry.Version < 1 || entry.Version > Version ||
                entry.Changes is null || entry.Changes.Count is < 1 or > 25))
            throw new InvalidDataException("The stored tenant access state is invalid.");
    }
}

public sealed class TenantUsersConflictException() :
    InvalidOperationException("Tenant access changed. Reload the users before saving again.");

public interface ITenantUserStore
{
    Task<TenantUsersSnapshot> GetAsync(string tenantId, CancellationToken cancellationToken = default);
    Task SaveAsync(TenantUsersSnapshot snapshot, long expectedVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>Read-only enterprise profile; neither an email address nor a photo independently grants access.</summary>
public sealed record DirectoryPerson(
    Guid ObjectId, string DisplayName, string Email, string UserPrincipalName,
    string? JobTitle, string? Department, string? OfficeLocation, bool AccountEnabled, bool Member);

public sealed record DirectoryPhoto(byte[] Bytes, string ContentType);

public interface IEnterpriseDirectory
{
    bool IsConfigured { get; }
    Guid DirectoryTenantId { get; }
    Task<IReadOnlyList<DirectoryPerson>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<DirectoryPerson?> GetAsync(Guid objectId, CancellationToken cancellationToken);
    Task<DirectoryPerson?> FindByEmailAsync(string email, CancellationToken cancellationToken);
    Task<DirectoryPhoto?> GetPhotoAsync(Guid objectId, CancellationToken cancellationToken);
}

public sealed record VerifiedEmailActor(ActorIdentity Actor, string Address);

/// <summary>Server-side identity binding used only after corporate mail transport authentication succeeds.</summary>
public interface IAutomaticEmailAuthorization
{
    Task<VerifiedEmailActor?> ResolveAsync(string tenantId, string address, CancellationToken cancellationToken);
}

internal static class TenantUserSerialization
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static byte[] Serialize(TenantUsersSnapshot snapshot)
    {
        snapshot.Validate(snapshot.TenantId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Options);
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("Tenant access state exceeded its supported size; no audit entries were removed.");
        return bytes;
    }
}
