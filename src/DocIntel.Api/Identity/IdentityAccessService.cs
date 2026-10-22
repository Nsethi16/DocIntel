using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DocIntel.Core.Authorization;
using DocIntel.Core.Harness;
using Microsoft.Extensions.Options;

namespace DocIntel.Api.Identity;

/// <summary>Maps verified directory object IDs to application tenant membership and document permissions.</summary>
public sealed class IdentityAccessService(
    IdentityOptions startup,
    IOptionsMonitor<IdentityOptions> configuration,
    IHarnessStore harnessStore,
    TenantUserService users) : ITenantAccessResolver
{
    public bool IsLocalDevelopment => startup.Mode == IdentityMode.LocalDevelopment;

    public string GetChatPartition(ClaimsPrincipal principal)
    {
        if (IsLocalDevelopment) return "local-development";
        var actor = GetActor(principal) ?? throw new UnauthorizedAccessException("A verified identity is required.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{startup.ClientId:D}:{actor.DirectoryTenantId:D}:{actor.ObjectId:D}")));
    }

    public static ActorIdentity? GetActor(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true &&
        Guid.TryParse(principal.FindFirst("tid")?.Value, out var directoryId) && directoryId != Guid.Empty &&
        Guid.TryParse(principal.FindFirst("oid")?.Value, out var objectId) && objectId != Guid.Empty
            ? new ActorIdentity(directoryId, objectId) : null;

    public async Task<bool> IsPlatformAdministratorAsync(
        ClaimsPrincipal principal, CancellationToken cancellationToken = default) =>
        IsLocalDevelopment || await users.IsPlatformAdministratorAsync(GetActor(principal), cancellationToken);

    public bool IsServicePrincipal(ActorIdentity actor) =>
        configuration.CurrentValue.Memberships.Any(m => m.ServicePrincipal &&
            m.DirectoryTenantId == actor.DirectoryTenantId && m.ObjectId == actor.ObjectId);

    public Task<DocumentAccessScope> GetAccessAsync(
        ClaimsPrincipal principal, string tenantId, CancellationToken cancellationToken = default) =>
        GetAccessAsync(GetActor(principal), tenantId, cancellationToken);

    public async Task<bool> HasAnyMembershipAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) =>
        await IsPlatformAdministratorAsync(principal, cancellationToken) ||
        GetActor(principal) is { } actor &&
        (await users.GetTenantIdsAsync(actor, false, cancellationToken)).Count > 0;

    public async Task<bool> CanAdministerAnyTenantAsync(
        ClaimsPrincipal principal, CancellationToken cancellationToken = default) =>
        await IsPlatformAdministratorAsync(principal, cancellationToken) ||
        GetActor(principal) is { } actor &&
        (await users.GetTenantIdsAsync(actor, true, cancellationToken)).Count > 0;

    public async Task<IReadOnlyList<string>?> GetTenantIdsAsync(
        ClaimsPrincipal principal, bool administratorsOnly = false, CancellationToken cancellationToken = default) =>
        await IsPlatformAdministratorAsync(principal, cancellationToken) ? null : GetActor(principal) is { } actor
            ? await users.GetTenantIdsAsync(actor, administratorsOnly, cancellationToken) : [];

    public async Task<DocumentAccessScope> ResolveAsync(
        ActorIdentity? actor, string tenantId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = await GetAccessAsync(actor, tenantId, cancellationToken);
        if (!scope.CanRead) return scope;
        var tenant = await harnessStore.GetTenantAsync(tenantId, cancellationToken);
        return tenant?.IsActive == true ? scope : DocumentAccessScope.Denied;
    }

    private async Task<DocumentAccessScope> GetAccessAsync(
        ActorIdentity? actor, string tenantId, CancellationToken cancellationToken)
    {
        if (IsLocalDevelopment || await users.IsPlatformAdministratorAsync(actor, cancellationToken))
        {
            return DocumentAccessScope.Unrestricted;
        }
        if (actor is null)
        {
            return DocumentAccessScope.Denied;
        }
        return await users.GetAccessAsync(actor, tenantId, cancellationToken);
    }
}
