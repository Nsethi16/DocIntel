using DocIntel.Api.Identity;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Harness;
using Microsoft.Extensions.Logging;

namespace DocIntel.Api.Channels;

/// <summary>Matches email routing to a current assigned recipient and a server-verified delivery address.</summary>
public sealed class AssignedEmailAuthorization(
    EmailChannelOptions options, TenantUserService users, IHarnessStore harness,
    ILogger<AssignedEmailAuthorization> logger, IPersonalEmailStateStore? personalStore = null,
    IPersonalEmailConnections? personalConnections = null) : IAssignedEmailAuthorization
{
    public async Task<VerifiedEmailActor?> ResolveAsync(
        string tenantId, string address, CancellationToken cancellationToken)
    {
        if (!options.Enabled || options.AuthorizationMode != EmailAuthorizationMode.ApprovedUsers ||
            tenantId != options.ApplicationTenantId || !EmailChannelOptions.IsAddress(address))
            return null;
        var tenant = await harness.GetTenantAsync(tenantId, cancellationToken);
        if (tenant is not { IsActive: true } || !tenant.Agent.EmailEnabled ||
            tenant.Agent.EmailConfirmationRequired ||
            !tenant.Capabilities.TryGetValue(FeatureHarness.AskAiKey, out var feature) || !feature.Enabled)
            return null;
        VerifiedEmailActor? primary;
        try
        {
            primary = await users.ResolveAsync(tenantId, address, cancellationToken);
        }
        catch (AmbiguousEmailAddressException)
        {
            logger.LogWarning("A directory email address has ambiguous ownership; no automatic question was authorized.");
            return null;
        }
        if (primary is not null &&
            !String.Equals(primary.Address, address, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("A directory sign-in alias is not the verified primary reply address; no question was authorized.");
            return null;
        }
        var candidates = new HashSet<ActorIdentity>();
        if (primary is not null) candidates.Add(primary.Actor);
        if (personalStore is not null && personalConnections is not null && options.Personal.IsConfigured)
        {
            var state = await personalStore.GetAsync(tenantId, cancellationToken);
            foreach (var credential in state.Senders.Where(item =>
                item.Status == PersonalEmailConnectionStatus.Connected &&
                item.Addresses.Contains(address, StringComparer.OrdinalIgnoreCase)))
            {
                if (credential.Actor.DirectoryTenantId == options.TenantId &&
                    await users.IsAssignedUserAsync(credential.Actor, tenantId, cancellationToken))
                    candidates.Add(credential.Actor);
            }
        }
        if (candidates.Count != 1) return null;
        var actor = candidates.Single();
        if (primary?.Actor == actor) return primary;
        try
        {
            if (await personalConnections!.OwnsSenderAsync(actor, address, cancellationToken) != true) return null;
        }
        catch (Exception exception) when (exception is PersonalEmailConnectionRequiredException or UnauthorizedAccessException)
        {
            logger.LogWarning("A linked approved email address is no longer available: {ErrorType}.",
                exception.GetType().Name);
            return null;
        }
        if (!await users.IsAssignedUserAsync(actor, tenantId, cancellationToken)) return null;
        return new VerifiedEmailActor(actor, address);
    }
}
