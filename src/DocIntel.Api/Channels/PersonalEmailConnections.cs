using System.Net;
using System.Security.Cryptography;
using DocIntel.Api.Identity;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Harness;
using Microsoft.Identity.Client;
using Microsoft.Extensions.Logging;

namespace DocIntel.Api.Channels;

public interface IPersonalEmailConnections
{
    Task<string> BeginAsync(
        ActorIdentity actor, string tenantId, PersonalEmailPurpose purpose, string? returnRunId,
        CancellationToken cancellationToken);
    Task<PendingPersonalEmail> GetPendingAsync(
        ActorIdentity actor, string requestId, CancellationToken cancellationToken);
    Task<PendingPersonalEmail> CompleteAsync(
        ActorIdentity actor, string requestId, string subject, PersonalEmailToken token,
        CancellationToken cancellationToken);
    Task<PersonalEmailStatus> GetStatusAsync(
        ActorIdentity actor, string tenantId, PersonalEmailPurpose purpose, CancellationToken cancellationToken);
    Task DisconnectAsync(
        ActorIdentity actor, string tenantId, PersonalEmailPurpose purpose, CancellationToken cancellationToken);
    Task<PersonalEmailCredential> GetMailboxAsync(CancellationToken cancellationToken);
    Task<PersonalEmailToken> AcquireAsync(
        PersonalEmailCredential credential, PersonalEmailPurpose purpose, CancellationToken cancellationToken);
    Task<bool?> OwnsSenderAsync(ActorIdentity actor, string address, CancellationToken cancellationToken);
}

/// <summary>Mailbox consent and sender ownership supplement, but never create, DocIntel workspace access.</summary>
public sealed class PersonalEmailConnections(
    EmailChannelOptions options, IdentityOptions identity, IPersonalEmailStateStore store,
    IPersonalEmailOAuthClient oauth, ITenantAccessResolver access, IHarnessStore harness,
    ILogger<PersonalEmailConnections> logger) : IPersonalEmailConnections
{
    public async Task<string> BeginAsync(
        ActorIdentity actor, string tenantId, PersonalEmailPurpose purpose, string? returnRunId,
        CancellationToken cancellationToken)
    {
        await RequireActorAsync(actor, tenantId, purpose, cancellationToken);
        if (returnRunId is not null && (purpose != PersonalEmailPurpose.Sender ||
            !PersonalEmailState.ValidRun(returnRunId)))
            throw new ArgumentException("A valid email question return link is required.");
        var request = new PendingPersonalEmail
        {
            RequestId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Actor = actor,
            Purpose = purpose,
            ReturnRunId = returnRunId,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        await UpdateAsync(state =>
        {
            var pending = state.Pending.Where(item => item.ExpiresAtUtc > DateTimeOffset.UtcNow &&
                !(item.Actor == actor && item.Purpose == purpose)).ToList();
            if (pending.Count >= 64) throw new InvalidOperationException("Too many email connections are pending.");
            pending.Add(request);
            return state with { Pending = pending };
        }, cancellationToken);
        return request.RequestId;
    }

    public async Task<PendingPersonalEmail> GetPendingAsync(
        ActorIdentity actor, string requestId, CancellationToken cancellationToken)
    {
        if (!PersonalEmailState.ValidRequest(requestId))
            throw new UnauthorizedAccessException("The email connection request is unavailable.");
        var pending = (await store.GetAsync(options.ApplicationTenantId, cancellationToken)).Pending
            .SingleOrDefault(item => item.RequestId == requestId);
        await RequirePendingAsync(pending, actor, cancellationToken);
        return pending!;
    }

    public async Task<PendingPersonalEmail> CompleteAsync(
        ActorIdentity actor, string requestId, string subject, PersonalEmailToken token,
        CancellationToken cancellationToken)
    {
        var pending = await GetPendingAsync(actor, requestId, cancellationToken);
        if (String.IsNullOrWhiteSpace(subject) || subject.Length > 512 || subject.Any(Char.IsControl) ||
            token.TokenCache is not { Length: > 0 and <= 524_288 } ||
            String.IsNullOrWhiteSpace(token.HomeAccountId) || token.HomeAccountId.Length > 512 ||
            token.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("Microsoft account verification was incomplete.");
        var profile = await oauth.GetProfileAsync(token.AccessToken, cancellationToken);
        if (pending.Purpose == PersonalEmailPurpose.Mailbox &&
            !profile.Addresses.Contains(options.MailboxAddress, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Connect the configured mailbox's primary Microsoft account.");
        var credential = new PersonalEmailCredential
        {
            Actor = actor,
            BindingId = Guid.NewGuid(),
            HomeAccountId = token.HomeAccountId,
            Subject = subject,
            GraphId = profile.GraphId,
            Addresses = profile.Addresses,
            TokenCache = token.TokenCache,
            VerifiedAtUtc = DateTimeOffset.UtcNow
        };
        await RequireActorAsync(actor, options.ApplicationTenantId, pending.Purpose, cancellationToken);
        await UpdateAsync(state =>
        {
            var current = state.Pending.SingleOrDefault(item => item.RequestId == requestId);
            if (current != pending || current.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                throw new UnauthorizedAccessException("The email connection request expired or was already used.");
            var remaining = state.Pending.Where(item => item.RequestId != requestId).ToList();
            if (pending.Purpose == PersonalEmailPurpose.Mailbox)
                return state with { Mailbox = credential, Pending = remaining };
            if (state.Senders.Any(item => item.Actor != actor && item.HomeAccountId == credential.HomeAccountId))
                throw new UnauthorizedAccessException("This personal account is already linked to another workspace user.");
            var senders = state.Senders.Where(item => item.Actor != actor).ToList();
            if (senders.Count >= 100) throw new InvalidOperationException("The personal sender limit was reached.");
            senders.Add(credential);
            return state with { Senders = senders, Pending = remaining };
        }, cancellationToken);
        logger.LogInformation("A personal email connection was verified for {Purpose}.", pending.Purpose);
        return pending;
    }

    public async Task<PersonalEmailStatus> GetStatusAsync(
        ActorIdentity actor, string tenantId, PersonalEmailPurpose purpose, CancellationToken cancellationToken)
    {
        await RequireActorAsync(actor, tenantId, purpose, cancellationToken);
        var state = await store.GetAsync(tenantId, cancellationToken);
        var credential = purpose == PersonalEmailPurpose.Mailbox ? state.Mailbox :
            state.Senders.SingleOrDefault(item => item.Actor == actor);
        if (purpose == PersonalEmailPurpose.Mailbox && credential is not null &&
            !(await access.ResolveAsync(credential.Actor, tenantId, cancellationToken)).CanAdmin)
            return new PersonalEmailStatus(true, false, "OwnerAccessUnavailable", options.MailboxAddress);
        return new PersonalEmailStatus(true, credential?.Status == PersonalEmailConnectionStatus.Connected,
            credential is null ? "NotConnected" : credential.Status.ToString(),
            credential is null ? null : purpose == PersonalEmailPurpose.Mailbox
                ? options.MailboxAddress : credential.Addresses[0]);
    }

    public async Task DisconnectAsync(
        ActorIdentity actor, string tenantId, PersonalEmailPurpose purpose, CancellationToken cancellationToken)
    {
        await RequireActorAsync(actor, tenantId, purpose, cancellationToken);
        await UpdateAsync(state =>
        {
            var pending = state.Pending.Where(item => item.Purpose != purpose ||
                purpose == PersonalEmailPurpose.Sender && item.Actor != actor).ToList();
            return purpose == PersonalEmailPurpose.Mailbox
                ? state with { Mailbox = null, Pending = pending }
                : state with { Senders = state.Senders.Where(item => item.Actor != actor).ToList(), Pending = pending };
        }, cancellationToken);
        logger.LogInformation("A personal email connection was disconnected for {Purpose}.", purpose);
    }

    public async Task<PersonalEmailCredential> GetMailboxAsync(CancellationToken cancellationToken)
    {
        var mailbox = (await store.GetAsync(options.ApplicationTenantId, cancellationToken)).Mailbox
            ?? throw new PersonalEmailConnectionRequiredException();
        await RequireActorAsync(mailbox.Actor, options.ApplicationTenantId, PersonalEmailPurpose.Mailbox,
            cancellationToken);
        if (mailbox.Status != PersonalEmailConnectionStatus.Connected ||
            !mailbox.Addresses.Contains(options.MailboxAddress, StringComparer.OrdinalIgnoreCase))
            throw new PersonalEmailConnectionRequiredException();
        return mailbox;
    }

    public async Task<PersonalEmailToken> AcquireAsync(
        PersonalEmailCredential credential, PersonalEmailPurpose purpose, CancellationToken cancellationToken)
    {
        await RequireActorAsync(credential.Actor, options.ApplicationTenantId, purpose, cancellationToken);
        await RequireCurrentAsync(credential, purpose, cancellationToken);
        PersonalEmailToken token;
        try
        {
            token = await oauth.AcquireAsync(credential, purpose, cancellationToken);
            var profile = await oauth.GetProfileAsync(token.AccessToken, cancellationToken);
            if (token.HomeAccountId != credential.HomeAccountId || profile.GraphId != credential.GraphId ||
                !profile.Addresses.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(credential.Addresses))
                throw new PersonalEmailConnectionRequiredException();
        }
        catch (Exception exception) when (exception is MsalUiRequiredException or
            PersonalEmailConnectionRequiredException ||
            exception is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
        {
            await SetReconnectionRequiredAsync(credential, purpose, cancellationToken);
            throw new PersonalEmailConnectionRequiredException();
        }
        if (!token.TokenCache.AsSpan().SequenceEqual(credential.TokenCache))
        {
            await UpdateAsync(state =>
            {
                var current = Find(state, credential, purpose);
                if (current is null || current.Status != PersonalEmailConnectionStatus.Connected ||
                    !current.TokenCache.AsSpan().SequenceEqual(credential.TokenCache))
                    throw new PersonalEmailStateConflictException();
                return Replace(state, current with { TokenCache = token.TokenCache }, purpose);
            }, cancellationToken);
        }
        await RequireCurrentAsync(credential, purpose, cancellationToken);
        await RequireActorAsync(credential.Actor, options.ApplicationTenantId, purpose, cancellationToken);
        return token;
    }

    public async Task<bool?> OwnsSenderAsync(
        ActorIdentity actor, string address, CancellationToken cancellationToken)
    {
        await RequireActorAsync(actor, options.ApplicationTenantId, PersonalEmailPurpose.Sender, cancellationToken);
        var credential = (await store.GetAsync(options.ApplicationTenantId, cancellationToken)).Senders
            .SingleOrDefault(item => item.Actor == actor &&
                item.Addresses.Contains(address, StringComparer.OrdinalIgnoreCase));
        if (credential is null) return null;
        await AcquireAsync(credential, PersonalEmailPurpose.Sender, cancellationToken);
        return true;
    }

    private async Task RequirePendingAsync(
        PendingPersonalEmail? pending, ActorIdentity actor, CancellationToken cancellationToken)
    {
        if (pending is null || pending.Actor != actor || pending.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new UnauthorizedAccessException("The email connection request is unavailable.");
        await RequireActorAsync(actor, options.ApplicationTenantId, pending.Purpose, cancellationToken);
    }

    private async Task RequireActorAsync(
        ActorIdentity actor, string tenantId, PersonalEmailPurpose purpose, CancellationToken cancellationToken)
    {
        options.Validate();
        if (!options.Enabled || !options.Personal.IsConfigured || !Enum.IsDefined(purpose) ||
            tenantId != options.ApplicationTenantId || actor.DirectoryTenantId != options.TenantId ||
            identity.Mode != IdentityMode.Entra ||
            options.AuthorizationMode is not (EmailAuthorizationMode.ConfirmSender or EmailAuthorizationMode.ApprovedUsers) ||
            purpose == PersonalEmailPurpose.Mailbox && options.Provider != EmailMailboxProvider.OutlookCom ||
            identity.Memberships.Any(item => item.ServicePrincipal && item.DirectoryTenantId == actor.DirectoryTenantId &&
                item.ObjectId == actor.ObjectId))
            throw new UnauthorizedAccessException("Personal email connection is unavailable for this account.");
        var scope = await access.ResolveAsync(actor, tenantId, cancellationToken);
        if (purpose == PersonalEmailPurpose.Mailbox ? !scope.CanAdmin : !scope.CanRead)
            throw new UnauthorizedAccessException("Current workspace access is required for this email connection.");
        if (await harness.GetTenantAsync(tenantId, cancellationToken) is not { IsActive: true })
            throw new UnauthorizedAccessException("The workspace is inactive.");
    }

    private async Task RequireCurrentAsync(
        PersonalEmailCredential credential, PersonalEmailPurpose purpose, CancellationToken cancellationToken)
    {
        var current = Find(await store.GetAsync(options.ApplicationTenantId, cancellationToken), credential, purpose);
        if (current is null || current.Status != PersonalEmailConnectionStatus.Connected)
            throw new PersonalEmailConnectionRequiredException();
    }

    private async Task SetReconnectionRequiredAsync(
        PersonalEmailCredential credential, PersonalEmailPurpose purpose, CancellationToken cancellationToken)
    {
        await UpdateAsync(state =>
        {
            var current = Find(state, credential, purpose);
            return current is null ? state :
                Replace(state, current with { Status = PersonalEmailConnectionStatus.ReconnectionRequired }, purpose);
        }, cancellationToken);
        logger.LogWarning("A personal email connection requires Microsoft account consent again for {Purpose}.", purpose);
    }

    private static PersonalEmailCredential? Find(
        PersonalEmailState state, PersonalEmailCredential credential, PersonalEmailPurpose purpose) =>
        purpose == PersonalEmailPurpose.Mailbox
            ? state.Mailbox?.BindingId == credential.BindingId ? state.Mailbox : null
            : state.Senders.SingleOrDefault(item => item.BindingId == credential.BindingId && item.Actor == credential.Actor);

    private static PersonalEmailState Replace(
        PersonalEmailState state, PersonalEmailCredential credential, PersonalEmailPurpose purpose) =>
        purpose == PersonalEmailPurpose.Mailbox ? state with { Mailbox = credential } :
            state with { Senders = state.Senders.Select(item =>
                item.Actor == credential.Actor ? credential : item).ToList() };

    private async Task UpdateAsync(
        Func<PersonalEmailState, PersonalEmailState> update, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var state = await store.GetAsync(options.ApplicationTenantId, cancellationToken);
            var next = update(state) with { Version = checked(state.Version + 1) };
            try
            {
                await store.SaveAsync(next, state.Version, cancellationToken);
                return;
            }
            catch (PersonalEmailStateConflictException) when (attempt < 2)
            { await Task.Delay(25, cancellationToken); }
        }
    }
}
