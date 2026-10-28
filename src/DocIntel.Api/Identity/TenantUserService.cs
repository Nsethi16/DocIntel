using System.Collections.Frozen;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Harness;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace DocIntel.Api.Identity;

public sealed record TenantUserEntry(
    Guid DirectoryTenantId, Guid ObjectId, TenantUserRole Role, TenantUserStatus Status,
    string Source, bool LegacyRestricted);

public sealed record TenantUserView(
    Guid ObjectId, TenantUserRole Role, TenantUserStatus Status, string Source, bool LegacyRestricted,
    DirectoryPerson? Profile, string DirectoryState);

public sealed record TenantUserList(
    long Version, bool DirectoryConfigured, Guid DirectoryTenantId,
    IReadOnlyList<TenantUserView> Users, IReadOnlyList<TenantAccessAudit> Audit, int Total, int? NextOffset);

/// <summary>Single membership authority for UI, APIs and channel workers. No role or address comes from a browser claim.</summary>
public sealed class TenantUserService(
    ITenantUserStore store, IOptionsMonitor<IdentityOptions> configuration, IEnterpriseDirectory directory,
    IHarnessStore harness, ILogger<TenantUserService> logger) : IAutomaticEmailAuthorization
{
    public async Task<bool> IsPlatformAdministratorAsync(ActorIdentity? actor, CancellationToken cancellationToken) =>
        actor is not null && configuration.CurrentValue.PlatformAdministrators.Contains(actor) &&
        (!directory.IsConfigured || actor.DirectoryTenantId == directory.DirectoryTenantId &&
            await directory.GetAsync(actor.ObjectId, cancellationToken) is { AccountEnabled: true, Member: true });

    public async Task<DocumentAccessScope> GetAccessAsync(
        ActorIdentity actor, string tenantId, CancellationToken cancellationToken)
    {
        var snapshot = await store.GetAsync(tenantId, cancellationToken);
        var baseline = Baseline(actor, tenantId);
        var assignment = snapshot.Users.SingleOrDefault(user =>
            user.DirectoryTenantId == actor.DirectoryTenantId && user.ObjectId == actor.ObjectId);
        if (assignment is null && baseline is null ||
            assignment is not null && assignment.Status != TenantUserStatus.Active)
            return DocumentAccessScope.Denied;
        if (directory.IsConfigured && baseline?.ServicePrincipal != true)
        {
            if (actor.DirectoryTenantId != directory.DirectoryTenantId ||
                await directory.GetAsync(actor.ObjectId, cancellationToken) is not { AccountEnabled: true, Member: true })
                return DocumentAccessScope.Denied;
        }
        else if (assignment is not null) return DocumentAccessScope.Denied;
        var role = assignment is null ? baseline!.Role :
            assignment.Role == TenantUserRole.TenantAdmin ? TenantRole.Admin : TenantRole.Reader;
        return new DocumentAccessScope(role, baseline?.AllDocuments == false
            ? baseline.DocumentIds.ToFrozenSet(StringComparer.Ordinal) : null);
    }

    public async Task<IReadOnlyList<string>> GetTenantIdsAsync(
        ActorIdentity actor, bool administratorsOnly, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        foreach (var tenant in await harness.ListTenantsAsync(cancellationToken))
        {
            var scope = await GetAccessAsync(actor, tenant.TenantId, cancellationToken);
            if (administratorsOnly ? scope.CanAdmin : scope.CanRead)
                result.Add(tenant.TenantId);
        }
        return result;
    }

    public async Task<TenantUserList> ListAsync(
        string tenantId, int offset, int limit, CancellationToken cancellationToken)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentException("Choose a supported users page.");
        var snapshot = await store.GetAsync(tenantId, cancellationToken);
        var result = new List<TenantUserView>();
        var entries = Effective(tenantId, snapshot).OrderBy(entry => entry.ObjectId).ToList();
        foreach (var entry in entries.Skip(offset).Take(limit))
        {
            DirectoryPerson? profile = null;
            var state = "NotConnected";
            if (directory.IsConfigured && entry.DirectoryTenantId == directory.DirectoryTenantId)
            {
                profile = await directory.GetAsync(entry.ObjectId, cancellationToken);
                state = profile is null ? "NotFound" : profile is { AccountEnabled: true, Member: true } ? "Active" : "Disabled";
            }
            result.Add(new TenantUserView(entry.ObjectId, entry.Role, entry.Status, entry.Source,
                entry.LegacyRestricted, profile, state));
        }
        return new TenantUserList(snapshot.Version, directory.IsConfigured, directory.DirectoryTenantId,
            result, snapshot.Audit.TakeLast(50).Reverse().ToList(), entries.Count,
            entries.Count > offset + result.Count ? offset + result.Count : null);
    }

    public async Task<IReadOnlyList<DirectoryPerson>> SearchAssignableAsync(
        string tenantId, string query, CancellationToken cancellationToken)
    {
        var snapshot = await store.GetAsync(tenantId, cancellationToken);
        var assigned = Effective(tenantId, snapshot).Where(entry => entry.Status != TenantUserStatus.Removed &&
            entry.DirectoryTenantId == directory.DirectoryTenantId).Select(entry => entry.ObjectId).ToHashSet();
        return (await directory.SearchAsync(query, cancellationToken))
            .Where(person => person is { AccountEnabled: true, Member: true } &&
                EmailChannelOptions.IsAddress(person.Email) && !assigned.Contains(person.ObjectId)).ToList();
    }

    public async Task<long> ChangeAsync(
        string tenantId, ActorIdentity? actor, bool localDevelopment, long version,
        IReadOnlyList<TenantUserChange>? changes, CancellationToken cancellationToken)
    {
        if (version < 0 || changes is null || changes.Count is < 1 or > 25 ||
            changes.Any(change => change is null || change.ObjectId == Guid.Empty ||
                !Enum.IsDefined(change.Role) || !Enum.IsDefined(change.Status)) ||
            changes.Select(change => change.ObjectId).Distinct().Count() != changes.Count)
            throw new ArgumentException("Select 1-25 distinct directory people with a supported role and access status.");
        await RequireAdministratorAsync(actor, localDevelopment, tenantId, cancellationToken);
        var snapshot = await store.GetAsync(tenantId, cancellationToken);
        if (snapshot.Version != version) throw new TenantUsersConflictException();
        if (snapshot.Audit.Count >= 5_000)
            throw new InvalidOperationException("The access audit is at capacity. Contact the operator; no history was removed.");
        var users = snapshot.Users.ToList();
        foreach (var change in changes)
        {
            var person = directory.IsConfigured
                ? await directory.GetAsync(change.ObjectId, cancellationToken) : null;
            var current = Effective(tenantId, snapshot).SingleOrDefault(entry => entry.ObjectId == change.ObjectId &&
                entry.DirectoryTenantId == directory.DirectoryTenantId);
            if (change.Status == TenantUserStatus.Active &&
                (person is not { AccountEnabled: true, Member: true } ||
                 !EmailChannelOptions.IsAddress(person.Email)))
                throw new ArgumentException("Only active corporate directory users with a work email can be assigned.");
            if (current is null && change.Status != TenantUserStatus.Active)
                throw new ArgumentException("This person has no workspace assignment to disable or remove.");
            if (current?.LegacyRestricted == true && change.Status == TenantUserStatus.Active)
                throw new ArgumentException("This legacy restricted assignment cannot be broadened in Admin Studio.");
            if (configuration.CurrentValue.Memberships.Any(m => m.ObjectId == change.ObjectId &&
                m.DirectoryTenantId == directory.DirectoryTenantId && m.ServicePrincipal))
                throw new ArgumentException("Application identities are managed by the operator, not the people picker.");
            users.RemoveAll(user => user.ObjectId == change.ObjectId &&
                user.DirectoryTenantId == directory.DirectoryTenantId);
            users.Add(new TenantUserAssignment(directory.DirectoryTenantId, change.ObjectId, change.Role, change.Status));
        }
        var nextVersion = checked(version + 1);
        var saved = snapshot with
        {
            Version = nextVersion,
            Users = users,
            Audit = snapshot.Audit.Append(new TenantAccessAudit(nextVersion, DateTimeOffset.UtcNow,
                actor, localDevelopment, changes.ToArray())).ToArray()
        };
        var administrators = Effective(tenantId, saved).Where(entry => entry.Status == TenantUserStatus.Active &&
            entry.Role == TenantUserRole.TenantAdmin && !entry.LegacyRestricted).ToList();
        var hasAdministrator = false;
        foreach (var administrator in administrators)
        {
            if (directory.IsConfigured)
            {
                if (await directory.GetAsync(administrator.ObjectId, cancellationToken) is
                    { AccountEnabled: true, Member: true }) { hasAdministrator = true; break; }
            }
            else if (administrator.Source == "Operator") { hasAdministrator = true; break; }
        }
        if (!hasAdministrator)
            throw new ArgumentException("Keep at least one active workspace admin. Assign another admin before removing or disabling the last one.");
        await RequireAdministratorAsync(actor, localDevelopment, tenantId, cancellationToken);
        await store.SaveAsync(saved, version, cancellationToken);
        logger.LogInformation("Tenant {TenantId} access version {Version} saved with {Count} assignment changes.",
            tenantId, nextVersion, changes.Count);
        return nextVersion;
    }

    public async Task<VerifiedEmailActor?> ResolveAsync(
        string tenantId, string address, CancellationToken cancellationToken)
    {
        if (!directory.IsConfigured) return null;
        var person = await directory.FindByEmailAsync(address, cancellationToken);
        if (person is not { AccountEnabled: true, Member: true } || !EmailChannelOptions.IsAddress(person.Email)) return null;
        var actor = new ActorIdentity(directory.DirectoryTenantId, person.ObjectId);
        if (!(await GetAccessAsync(actor, tenantId, cancellationToken)).CanRead ||
            (await harness.GetTenantAsync(tenantId, cancellationToken))?.IsActive != true) return null;
        return new VerifiedEmailActor(actor, person.Email);
    }

    private async Task RequireAdministratorAsync(
        ActorIdentity? actor, bool localDevelopment, string tenantId, CancellationToken cancellationToken)
    {
        if (localDevelopment) return;
        if (actor is null) throw new UnauthorizedAccessException("A verified workspace administrator is required.");
        if (await IsPlatformAdministratorAsync(actor, cancellationToken)) return;
        var scope = await GetAccessAsync(actor, tenantId, cancellationToken);
        if (!scope.CanAdmin || scope.AllowedJobIds is not null)
            throw new UnauthorizedAccessException("Workspace-wide administration is required.");
    }

    private TenantMembership? Baseline(ActorIdentity actor, string tenantId) =>
        configuration.CurrentValue.Memberships.SingleOrDefault(member =>
            member.DirectoryTenantId == actor.DirectoryTenantId && member.ObjectId == actor.ObjectId &&
            member.ApplicationTenantId == tenantId);

    private IReadOnlyList<TenantUserEntry> Effective(string tenantId, TenantUsersSnapshot snapshot)
    {
        var result = configuration.CurrentValue.Memberships.Where(member =>
            member.ApplicationTenantId == tenantId && !member.ServicePrincipal).Select(member =>
                new TenantUserEntry(member.DirectoryTenantId, member.ObjectId,
                    member.Role == TenantRole.Admin ? TenantUserRole.TenantAdmin : TenantUserRole.User,
                    TenantUserStatus.Active, "Operator", !member.AllDocuments)).ToList();
        foreach (var assignment in snapshot.Users)
        {
            var baseline = result.SingleOrDefault(entry => entry.ObjectId == assignment.ObjectId &&
                entry.DirectoryTenantId == assignment.DirectoryTenantId);
            result.RemoveAll(entry => entry.ObjectId == assignment.ObjectId &&
                entry.DirectoryTenantId == assignment.DirectoryTenantId);
            result.Add(new TenantUserEntry(assignment.DirectoryTenantId, assignment.ObjectId,
                assignment.Role, assignment.Status, "Managed", baseline?.LegacyRestricted == true));
        }
        return result;
    }
}
