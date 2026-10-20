using System.Collections.Frozen;
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
    IHarnessStore harnessStore) : IAskAiAccessResolver
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

    public bool IsPlatformAdministrator(ClaimsPrincipal principal) =>
        IsLocalDevelopment || IsPlatformAdministrator(GetActor(principal));

    public bool IsServicePrincipal(ActorIdentity actor) =>
        configuration.CurrentValue.Memberships.Any(m => m.ServicePrincipal &&
            m.DirectoryTenantId == actor.DirectoryTenantId && m.ObjectId == actor.ObjectId);

    public DocumentAccessScope GetAccess(ClaimsPrincipal principal, string tenantId) =>
        GetAccess(GetActor(principal), tenantId);

    public bool HasAnyMembership(ClaimsPrincipal principal) =>
        IsPlatformAdministrator(principal) || configuration.CurrentValue.Memberships.Any(m =>
            GetActor(principal) == new ActorIdentity(m.DirectoryTenantId, m.ObjectId));

    public bool CanAdministerAnyTenant(ClaimsPrincipal principal) =>
        IsPlatformAdministrator(principal) || configuration.CurrentValue.Memberships.Any(m =>
            m.Role == TenantRole.Admin && GetActor(principal) == new ActorIdentity(m.DirectoryTenantId, m.ObjectId));

    public IReadOnlySet<string>? GetTenantIds(ClaimsPrincipal principal) =>
        IsPlatformAdministrator(principal) ? null : configuration.CurrentValue.Memberships
            .Where(m => GetActor(principal) == new ActorIdentity(m.DirectoryTenantId, m.ObjectId))
            .Select(m => m.ApplicationTenantId).ToFrozenSet(StringComparer.Ordinal);

    public async Task<DocumentAccessScope> ResolveAsync(
        ActorIdentity? actor, string tenantId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = GetAccess(actor, tenantId);
        if (!scope.CanRead) return scope;
        var tenant = await harnessStore.GetTenantAsync(tenantId, cancellationToken);
        return tenant?.IsActive == true ? scope : DocumentAccessScope.Denied;
    }

    private bool IsPlatformAdministrator(ActorIdentity? actor) =>
        actor is not null && configuration.CurrentValue.PlatformAdministrators.Contains(actor);

    private DocumentAccessScope GetAccess(ActorIdentity? actor, string tenantId)
    {
        if (IsLocalDevelopment || IsPlatformAdministrator(actor))
        {
            return DocumentAccessScope.Unrestricted;
        }
        if (actor is null)
        {
            return DocumentAccessScope.Denied;
        }
        var membership = configuration.CurrentValue.Memberships.SingleOrDefault(m =>
            m.DirectoryTenantId == actor.DirectoryTenantId && m.ObjectId == actor.ObjectId &&
            String.Equals(m.ApplicationTenantId, tenantId, StringComparison.Ordinal));
        return membership is null ? DocumentAccessScope.Denied : new DocumentAccessScope(
            membership.Role,
            membership.AllDocuments ? null : membership.DocumentIds.ToFrozenSet(StringComparer.Ordinal));
    }
}
