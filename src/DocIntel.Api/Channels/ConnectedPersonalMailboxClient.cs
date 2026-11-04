using Azure.Core;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using Microsoft.Extensions.Logging;

namespace DocIntel.Api.Channels;

/// <summary>Delegated personal mail and verified sender links, with the original Exchange transport preserved.</summary>
public sealed class ConnectedPersonalMailboxClient(
    EmailChannelOptions options, IPersonalEmailConnections connections, GraphMailboxClient corporate,
    IHttpClientFactory clients, ILogger<GraphMailboxClient> logger) : IConnectedMailboxClient
{
    public async Task<string> GetConnectionProfileAsync(
        string configuredProfile, CancellationToken cancellationToken) =>
        options.Provider == EmailMailboxProvider.OutlookCom
            ? (await connections.GetMailboxAsync(cancellationToken)).Profile(configuredProfile)
            : configuredProfile;

    public async Task<MailboxChanges> ReadChangesAsync(
        string? continuation, CancellationToken cancellationToken, DateTimeOffset? receivedAfterUtc = null)
    {
        var profile = await GetConnectionProfileAsync(options.ConfiguredProfile, cancellationToken);
        return await ReadForProfileAsync(profile, continuation, cancellationToken, receivedAfterUtc);
    }

    public async Task<DateTimeOffset?> GetAdmissionStartAsync(string profile, CancellationToken cancellationToken)
    {
        if (options.Provider != EmailMailboxProvider.OutlookCom) return null;
        var mailbox = await connections.GetMailboxAsync(cancellationToken);
        if (mailbox.Profile(options.ConfiguredProfile) != profile)
            throw new PersonalEmailConnectionRequiredException();
        return mailbox.VerifiedAtUtc;
    }

    public async Task<MailboxChanges> ReadForProfileAsync(
        string profile, string? continuation, CancellationToken cancellationToken, DateTimeOffset? receivedAfterUtc = null)
    {
        if (options.Provider != EmailMailboxProvider.OutlookCom)
        {
            if (profile != options.ConfiguredProfile)
                throw new InvalidOperationException("The Exchange mailbox configuration changed.");
            return await corporate.ReadChangesAsync(continuation, cancellationToken, receivedAfterUtc);
        }
        var mailbox = await connections.GetMailboxAsync(cancellationToken);
        if (mailbox.Profile(options.ConfiguredProfile) != profile)
            throw new PersonalEmailConnectionRequiredException();
        var token = await connections.AcquireAsync(mailbox, PersonalEmailPurpose.Mailbox, cancellationToken);
        var changes = await PersonalClient(token, mailbox).ReadChangesAsync(
            continuation, cancellationToken, receivedAfterUtc);
        if (await GetConnectionProfileAsync(options.ConfiguredProfile, cancellationToken) != profile)
            throw new PersonalEmailConnectionRequiredException();
        return changes with
        {
            Messages = changes.Messages.Select(message =>
                message with { ConnectionProfile = profile }).ToArray()
        };
    }

    public Task<IReadOnlyList<string>> GetActorAddressesAsync(
        ActorIdentity actor, CancellationToken cancellationToken) =>
        corporate.GetActorAddressesAsync(actor, cancellationToken);

    public async Task<bool> OwnsAddressAsync(
        ActorIdentity actor, string address, CancellationToken cancellationToken)
    {
        if (options.AuthorizationMode == EmailAuthorizationMode.ConfirmSender &&
            await connections.OwnsSenderAsync(actor, address, cancellationToken) is { } owned)
            return owned;
        return (await corporate.GetActorAddressesAsync(actor, cancellationToken))
            .Contains(address, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SendAsync(string recipient, string subject, string text, CancellationToken cancellationToken)
    {
        var profile = await GetConnectionProfileAsync(options.ConfiguredProfile, cancellationToken);
        await SendForProfileAsync(profile, recipient, subject, text, cancellationToken);
    }

    public async Task SendForProfileAsync(
        string profile, string recipient, string subject, string text, CancellationToken cancellationToken)
    {
        if (options.Provider != EmailMailboxProvider.OutlookCom)
        {
            if (profile != options.ConfiguredProfile)
                throw new InvalidOperationException("The Exchange mailbox configuration changed.");
            await corporate.SendAsync(recipient, subject, text, cancellationToken);
            return;
        }
        var mailbox = await connections.GetMailboxAsync(cancellationToken);
        if (mailbox.Profile(options.ConfiguredProfile) != profile)
            throw new PersonalEmailConnectionRequiredException();
        var token = await connections.AcquireAsync(mailbox, PersonalEmailPurpose.Mailbox, cancellationToken);
        await PersonalClient(token, mailbox).SendAsync(recipient, subject, text, cancellationToken);
    }

    public async Task ReplyAsync(
        string messageId, string recipient, string text, CancellationToken cancellationToken)
    {
        var profile = await GetConnectionProfileAsync(options.ConfiguredProfile, cancellationToken);
        await ReplyForProfileAsync(profile, messageId, recipient, text, cancellationToken);
    }

    public async Task ReplyForProfileAsync(
        string profile, string messageId, string recipient, string text, CancellationToken cancellationToken)
    {
        if (options.Provider != EmailMailboxProvider.OutlookCom)
        {
            if (profile != options.ConfiguredProfile)
                throw new InvalidOperationException("The Exchange mailbox configuration changed.");
            await corporate.ReplyAsync(messageId, recipient, text, cancellationToken);
            return;
        }
        var mailbox = await connections.GetMailboxAsync(cancellationToken);
        if (mailbox.Profile(options.ConfiguredProfile) != profile)
            throw new PersonalEmailConnectionRequiredException();
        var token = await connections.AcquireAsync(mailbox, PersonalEmailPurpose.Mailbox, cancellationToken);
        await PersonalClient(token, mailbox).ReplyAsync(messageId, recipient, text, async ct =>
        {
            if (await GetConnectionProfileAsync(options.ConfiguredProfile, ct) != profile)
                throw new PersonalEmailConnectionRequiredException();
        }, cancellationToken);
    }

    private GraphMailboxClient PersonalClient(PersonalEmailToken token, PersonalEmailCredential mailbox) =>
        new(clients, new AccessToken(token.AccessToken, token.ExpiresAtUtc), options, mailbox.GraphId, logger);
}
