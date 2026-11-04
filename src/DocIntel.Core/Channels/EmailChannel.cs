using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Channels;

public enum EmailAuthorizationMode { ConfirmSender, TrustedInternal }
public enum EmailMailboxProvider { ExchangeOnline, OutlookCom }

/// <summary>Separate consumer OAuth registration; credentials belong in a managed secret provider.</summary>
public sealed class PersonalEmailOAuthOptions
{
    public Guid ClientId { get; set; }
    public string ClientSecret { get; set; } = String.Empty;
    public bool IsConfigured => ClientId != Guid.Empty && !String.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>Operator-owned mailbox binding. No sender address or URL grants document permissions.</summary>
public sealed class EmailChannelOptions
{
    public const string SectionName = "Channels:Email";
    public bool Enabled { get; set; }
    public EmailMailboxProvider Provider { get; set; }
    public PersonalEmailOAuthOptions Personal { get; set; } = new();
    public Guid TenantId { get; set; }
    public Guid MailboxObjectId { get; set; }
    public string MailboxAddress { get; set; } = String.Empty;
    public string ApplicationTenantId { get; set; } = String.Empty;
    public string PublicOrigin { get; set; } = String.Empty;
    public int PollSeconds { get; set; } = 30;
    public EmailAuthorizationMode AuthorizationMode { get; set; }
    /// <summary>Secret transport attestation; Exchange must strip incoming copies and stamp only authenticated internal mail.</summary>
    public string CorporateSenderProof { get; set; } = String.Empty;
    public const string CorporateSenderProofHeader = "X-DocIntel-Internal-Sender-Proof";
    public void Validate()
    {
        if (!Enabled) return;
        if (!Enum.IsDefined(AuthorizationMode) || AuthorizationMode == EmailAuthorizationMode.TrustedInternal &&
            (CorporateSenderProof.Length is < 43 or > 256 ||
             CorporateSenderProof.Any(character => !Char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
            throw new InvalidOperationException("Automatic email requires a securely configured corporate sender attestation.");
        if (!Enum.IsDefined(Provider) || Personal is null || Personal.ClientSecret is null ||
            Provider == EmailMailboxProvider.OutlookCom &&
                (!Personal.IsConfigured || AuthorizationMode != EmailAuthorizationMode.ConfirmSender) ||
            (Personal.ClientId != Guid.Empty || Personal.ClientSecret.Length > 0) &&
                (!Personal.IsConfigured || Personal.ClientSecret.Length > 4_096 ||
                 Personal.ClientSecret.Any(Char.IsControl)) ||
            TenantId == Guid.Empty || Provider == EmailMailboxProvider.ExchangeOnline && MailboxObjectId == Guid.Empty ||
            !IsAddress(MailboxAddress) ||
            String.IsNullOrWhiteSpace(ApplicationTenantId) || ApplicationTenantId.Length > 128 ||
            ApplicationTenantId.Any(c => !Char.IsAsciiLetterOrDigit(c) && c != '-') ||
            PollSeconds is < 10 or > 300 || !Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme != "https" || !origin.IsDefaultPort || origin.AbsolutePath != "/" ||
            origin.UserInfo.Length > 0 || origin.Query.Length > 0 || origin.Fragment.Length > 0 || PublicOrigin.Length > 512)
            throw new InvalidOperationException("Email requires a verified directory/mailbox ID, mailbox address, tenant and HTTPS app origin.");
    }
    public string Profile => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        TenantId,
        MailboxObjectId,
        MailboxAddress,
        ApplicationTenantId,
        PublicOrigin,
        AuthorizationMode,
        ProofHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(CorporateSenderProof)))
    })));
    /// <summary>Consumer connection changes invalidate pending confirmations without coupling them to token refresh.</summary>
    public string ConfiguredProfile => Provider == EmailMailboxProvider.ExchangeOnline ? Profile :
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Profile,
            Provider,
            Personal.ClientId
        })));
    public string ConfirmationUrl(string runId) => PublicOrigin.TrimEnd('/') + "/email-question.html?tenant=" +
        Uri.EscapeDataString(ApplicationTenantId) + "&run=" + Uri.EscapeDataString(runId);
    public string RunUrl(string runId) => PublicOrigin.TrimEnd('/') + "/ask-run.html?tenant=" +
        Uri.EscapeDataString(ApplicationTenantId) + "&run=" + Uri.EscapeDataString(runId);
    public static bool IsAddress(string? value) => value is { Length: <= 254 } &&
        MailAddress.TryCreate(value, out var address) && address.Address == value && !value.Any(Char.IsControl);
}

public sealed record MailboxQuestion(string MessageId, string Sender, string Subject, string Text,
    DateTimeOffset ReceivedAtUtc, bool Automatic = false, bool TrustedInternalSender = false,
    string? ConnectionProfile = null);
public sealed record MailboxChanges(IReadOnlyList<MailboxQuestion> Messages, string? NextLink, string? DeltaLink);

public interface IMailboxClient
{
    Task<MailboxChanges> ReadChangesAsync(string? continuation, CancellationToken cancellationToken,
        DateTimeOffset? receivedAfterUtc = null);
    Task<IReadOnlyList<string>> GetActorAddressesAsync(ActorIdentity actor, CancellationToken cancellationToken);
    Task SendAsync(string recipient, string subject, string text, CancellationToken cancellationToken);
    Task ReplyAsync(string messageId, string recipient, string text, CancellationToken cancellationToken);
}

/// <summary>Personal mailbox operations are fenced to the persisted OAuth connection that admitted the message.</summary>
public interface IConnectedMailboxClient : IMailboxClient
{
    Task<string> GetConnectionProfileAsync(string configuredProfile, CancellationToken cancellationToken);
    Task<DateTimeOffset?> GetAdmissionStartAsync(string profile, CancellationToken cancellationToken);
    Task<MailboxChanges> ReadForProfileAsync(
        string profile, string? continuation, CancellationToken cancellationToken, DateTimeOffset? receivedAfterUtc = null);
    Task<bool> OwnsAddressAsync(ActorIdentity actor, string address, CancellationToken cancellationToken);
    Task SendForProfileAsync(
        string profile, string recipient, string subject, string text, CancellationToken cancellationToken);
    Task ReplyForProfileAsync(
        string profile, string messageId, string recipient, string text, CancellationToken cancellationToken);
}

/// <summary>Preserves the existing Exchange contracts while binding delegated mailbox operations to their connection.</summary>
public static class EmailMailboxBinding
{
    public static Task<string> GetProfileAsync(
        EmailChannelOptions options, IMailboxClient mailbox, CancellationToken cancellationToken) =>
        mailbox is IConnectedMailboxClient connected
            ? connected.GetConnectionProfileAsync(options.ConfiguredProfile, cancellationToken)
            : Task.FromResult(options.ConfiguredProfile);

    public static Task<DateTimeOffset?> GetAdmissionStartAsync(
        IMailboxClient mailbox, string profile, CancellationToken cancellationToken) =>
        mailbox is IConnectedMailboxClient connected
            ? connected.GetAdmissionStartAsync(profile, cancellationToken)
            : Task.FromResult<DateTimeOffset?>(null);

    public static Task<MailboxChanges> ReadAsync(IMailboxClient mailbox, string profile, string? continuation,
        CancellationToken cancellationToken, DateTimeOffset? receivedAfterUtc) =>
        mailbox is IConnectedMailboxClient connected
            ? connected.ReadForProfileAsync(profile, continuation, cancellationToken, receivedAfterUtc)
            : mailbox.ReadChangesAsync(continuation, cancellationToken, receivedAfterUtc);

    public static async Task<bool> OwnsAddressAsync(
        IMailboxClient mailbox, ActorIdentity actor, string address, CancellationToken cancellationToken) =>
        mailbox is IConnectedMailboxClient connected
            ? await connected.OwnsAddressAsync(actor, address, cancellationToken)
            : (await mailbox.GetActorAddressesAsync(actor, cancellationToken))
                .Contains(address, StringComparer.OrdinalIgnoreCase);

    public static Task SendAsync(IMailboxClient mailbox, string profile, string recipient,
        string subject, string text, CancellationToken cancellationToken) =>
        mailbox is IConnectedMailboxClient connected
            ? connected.SendForProfileAsync(profile, recipient, subject, text, cancellationToken)
            : mailbox.SendAsync(recipient, subject, text, cancellationToken);

    public static Task ReplyAsync(IMailboxClient mailbox, string profile, string messageId,
        string recipient, string text, CancellationToken cancellationToken) =>
        mailbox is IConnectedMailboxClient connected
            ? connected.ReplyForProfileAsync(profile, messageId, recipient, text, cancellationToken)
            : mailbox.ReplyAsync(messageId, recipient, text, cancellationToken);
}

/// <summary>Separate inbound-confirmation state; it never makes a question executable on a GET request.</summary>
public sealed class PendingEmailQuestion
{
    public required string Profile { get; init; }
    public required string Sender { get; init; }
    public required string MessageId { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public ChannelDeliveryStatus ChallengeStatus { get; set; } = ChannelDeliveryStatus.Pending;
    public DateTimeOffset? ConfirmedAtUtc { get; set; }
    public EmailAuthorizationMode AuthorizationMode { get; init; }
    public DateTimeOffset? AuthenticatedAtUtc { get; init; }
}

/// <summary>Deduplicates mailbox messages and sends only a source-free sign-in challenge before user authorization.</summary>
public sealed class EmailQuestionReceiver(
    EmailChannelOptions options, IAskAiRunStore runs, IHarnessStore harness, IMailboxClient mailbox,
    ILogger<EmailQuestionReceiver> logger, IAutomaticEmailAuthorization? automaticAuthorization = null,
    IAskAiQueue? queue = null)
{
    public async Task ReceiveAsync(MailboxQuestion message, CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (!options.Enabled) throw new InvalidOperationException("Mailbox ingestion is disabled.");
        if (message.Automatic || String.Equals(message.Sender, options.MailboxAddress, StringComparison.OrdinalIgnoreCase)) return;
        if (!EmailChannelOptions.IsAddress(message.Sender) ||
            String.IsNullOrWhiteSpace(message.MessageId) || message.MessageId.Length > 1_024 ||
            message.MessageId.Any(Char.IsControl) || AskAiRequestValidator.GetError(message.Text) is not null ||
            message.Subject.Length > 512)
        {
            logger.LogWarning("An inbound email could not be accepted because its envelope or question exceeds supported limits.");
            return;
        }
        var tenant = await harness.GetTenantAsync(options.ApplicationTenantId, cancellationToken);
        if (tenant is not { IsActive: true } || !tenant.Agent.EmailEnabled ||
            !tenant.Capabilities.TryGetValue(FeatureHarness.AskAiKey, out var feature) || !feature.Enabled) return;
        var profile = await EmailMailboxBinding.GetProfileAsync(options, mailbox, cancellationToken);
        if (options.Provider == EmailMailboxProvider.OutlookCom && message.ConnectionProfile != profile)
            throw new InvalidOperationException("The mailbox connection changed before the message was admitted.");
        VerifiedEmailActor? verified = null;
        if (options.AuthorizationMode == EmailAuthorizationMode.TrustedInternal)
        {
            if (!message.TrustedInternalSender)
            {
                logger.LogWarning("An email lacked trusted corporate sender authentication; no question or answer was created.");
                return;
            }
            if (automaticAuthorization is null || queue is null)
                throw new InvalidOperationException("Automatic email requires directory-backed tenant authorization and dispatch.");
            verified = await automaticAuthorization.ResolveAsync(options.ApplicationTenantId, message.Sender, cancellationToken);
            if (verified is null)
            {
                logger.LogWarning("An email sender has no active verified tenant assignment; no question or answer was created.");
                return;
            }
        }
        var messageIdentity = options.Provider == EmailMailboxProvider.OutlookCom
            ? JsonSerializer.SerializeToUtf8Bytes(new { profile, options.ApplicationTenantId, message.MessageId })
            : JsonSerializer.SerializeToUtf8Bytes(new
                { options.MailboxObjectId, options.ApplicationTenantId, message.MessageId });
        var runId = "mail-" + Convert.ToHexString(SHA256.HashData(messageIdentity));
        var run = new AskAiRun
        {
            RunId = runId,
            TenantId = options.ApplicationTenantId,
            Question = message.Text.Trim(),
            Actor = verified?.Actor,
            Status = verified is null ? JobStatus.AwaitingConfirmation : JobStatus.Queued,
            ReplyChannel = new AskAiReplyChannel { ChannelType = AskAiChannelType.Email },
            PendingEmail = new PendingEmailQuestion
            {
                Profile = profile,
                Sender = verified?.Address ?? message.Sender,
                MessageId = message.MessageId,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24),
                AuthorizationMode = options.AuthorizationMode,
                AuthenticatedAtUtc = verified is null ? null : DateTimeOffset.UtcNow
            },
            ChannelDelivery = verified is null ? null : new ChannelDelivery
            {
                Transport = "Email",
                Profile = profile,
                DestinationId = verified.Address,
                MessageId = message.MessageId
            }
        };
        var created = await runs.TryCreateRunAsync(run, cancellationToken);
        if (verified is not null)
        {
            if (created) await queue!.TryEnqueueAsync(new AskAiQueueTicket(runId, run.TenantId), cancellationToken);
            return;
        }
        await using var lease = await runs.TryAcquireAsync(run.TenantId, run.RunId, cancellationToken)
            ?? throw new InvalidOperationException("The message is still owned by another worker; retain the mailbox checkpoint.");
        if (lease.Run.Status != JobStatus.AwaitingConfirmation ||
            lease.Run.PendingEmail is not { } pending || pending.Profile != profile) return;
        if (pending.ChallengeStatus == ChannelDeliveryStatus.Sending)
        {
            pending.ChallengeStatus = ChannelDeliveryStatus.OutcomeUnknown;
            await lease.SaveAsync(cancellationToken);
            return;
        }
        if (pending.ChallengeStatus != ChannelDeliveryStatus.Pending || pending.ExpiresAtUtc <= DateTimeOffset.UtcNow) return;
        using var challenge = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.OwnershipLostToken);
        challenge.CancelAfter(TimeSpan.FromSeconds(60));
        pending.ChallengeStatus = ChannelDeliveryStatus.Sending;
        await lease.SaveAsync(challenge.Token);
        try
        {
            await EmailMailboxBinding.SendAsync(mailbox, profile, pending.Sender,
                $"{tenant.Agent.DisplayName}: confirm your document question",
                $"You received this because a question was emailed to {options.MailboxAddress}.\n\n" +
                "Sign in to DocIntel, verify your sender email if needed, and explicitly confirm the question:\n" +
                options.ConfirmationUrl(runId) +
                "\n\nThis link expires in 24 hours. No document information is included in this email. " +
                "If you did not send the question, do not confirm it.", challenge.Token);
            pending.ChallengeStatus = ChannelDeliveryStatus.Sent;
        }
        catch (Exception exception)
        {
            pending.ChallengeStatus = ChannelDeliveryStatus.OutcomeUnknown;
            logger.LogWarning("Email confirmation has an uncertain delivery outcome: {ErrorType}; no automatic resend.",
                exception.GetType().Name);
        }
        lease.OwnershipLostToken.ThrowIfCancellationRequested();
        using var persist = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await lease.SaveAsync(persist.Token);
    }
}

/// <summary>Binds the actual signed-in user to the original sender/destination before exposing or executing the question.</summary>
public sealed class EmailQuestionConfirmation(
    EmailChannelOptions options, IMailboxClient mailbox, ITenantAccessResolver access,
    IAskAiRunStore runs, IAskAiQueue queue, IHarnessStore harness)
{
    public async Task<AskAiRun> ReadAsync(ActorIdentity actor, string tenantId, string runId, CancellationToken cancellationToken)
    {
        var run = await runs.GetRunAsync(tenantId, runId, cancellationToken)
            ?? throw new UnauthorizedAccessException("The email question is unavailable.");
        await AuthorizeAsync(actor, run, cancellationToken);
        return run;
    }

    public async Task<AskAiRun> ConfirmAsync(
        ActorIdentity actor, string tenantId, string runId, CancellationToken cancellationToken)
    {
        await using var lease = await runs.TryAcquireAsync(tenantId, runId, cancellationToken)
            ?? throw new InvalidOperationException("The question is busy or unavailable. Refresh before retrying.");
        var run = lease.Run;
        await AuthorizeAsync(actor, run, cancellationToken);
        if (run.Status == JobStatus.AwaitingConfirmation)
        {
            run.Actor = actor;
            run.PendingEmail!.ConfirmedAtUtc = DateTimeOffset.UtcNow;
            run.ChannelDelivery = new ChannelDelivery
            {
                Transport = "Email",
                Profile = run.PendingEmail!.Profile,
                DestinationId = run.PendingEmail.Sender,
                MessageId = run.PendingEmail.MessageId
            };
            run.Status = JobStatus.Queued;
            await lease.SaveAsync(cancellationToken);
            await queue.TryEnqueueAsync(new AskAiQueueTicket(runId, tenantId), cancellationToken);
        }
        return run;
    }

    private async Task AuthorizeAsync(ActorIdentity actor, AskAiRun run, CancellationToken cancellationToken)
    {
        options.Validate();
        var profile = await EmailMailboxBinding.GetProfileAsync(options, mailbox, cancellationToken);
        if (!options.Enabled || actor.DirectoryTenantId != options.TenantId ||
            run.TenantId != options.ApplicationTenantId || run.PendingEmail is not { } pending ||
            pending.Profile != profile ||
            pending.AuthorizationMode != EmailAuthorizationMode.ConfirmSender ||
            (run.Status == JobStatus.AwaitingConfirmation && pending.ExpiresAtUtc <= DateTimeOffset.UtcNow) ||
            (run.Actor is not null && run.Actor != actor) ||
            !(await access.ResolveAsync(actor, run.TenantId, cancellationToken)).CanRead)
            throw new UnauthorizedAccessException("The email question is unavailable for this account.");
        var tenant = await harness.GetTenantAsync(run.TenantId, cancellationToken);
        if (tenant is not { IsActive: true } || !tenant.Agent.EmailEnabled ||
            !tenant.Capabilities.TryGetValue(FeatureHarness.AskAiKey, out var feature) || !feature.Enabled)
            throw new UnauthorizedAccessException("Email questions are disabled.");
        if (!await EmailMailboxBinding.OwnsAddressAsync(mailbox, actor, pending.Sender, cancellationToken))
            throw new UnauthorizedAccessException("Sign in using the account that owns the original email address.");
    }
}

/// <summary>One native reply to the original question, with current access and verified-recipient checks.</summary>
public sealed class EmailAnswerTransport(
    EmailChannelOptions options, IMailboxClient mailbox) : IChannelTransport
{
    public string Name => "Email";
    public async Task SendAsync(AskAiRun run, ChannelDelivery target, CancellationToken cancellationToken = default)
    {
        var profile = await EmailMailboxBinding.GetProfileAsync(options, mailbox, cancellationToken);
        if (!options.Enabled || target.Profile != profile || run.TenantId != options.ApplicationTenantId ||
            run.Actor is not { } actor || actor.DirectoryTenantId != options.TenantId ||
            run.PendingEmail is not { } pending || pending.Profile != profile ||
            String.IsNullOrWhiteSpace(pending.MessageId) ||
            !String.Equals(target.MessageId, pending.MessageId, StringComparison.Ordinal) ||
            !(pending.AuthorizationMode == EmailAuthorizationMode.ConfirmSender && pending.ConfirmedAtUtc is not null ||
              pending.AuthorizationMode == EmailAuthorizationMode.TrustedInternal &&
              options.AuthorizationMode == EmailAuthorizationMode.TrustedInternal && pending.AuthenticatedAtUtc is not null) ||
            !String.Equals(target.DestinationId, run.PendingEmail.Sender, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The verified mailbox destination is unavailable.");
        if (!await EmailMailboxBinding.OwnsAddressAsync(mailbox, actor, target.DestinationId, cancellationToken))
            throw new UnauthorizedAccessException("The sender no longer owns the reply address.");
        var sources = String.Join("\n", run.Citations.Select(citation =>
            $"[{citation.CitationNumber}] {citation.FileName} | {citation.DocumentName} | {citation.PageRange}"));
        await EmailMailboxBinding.ReplyAsync(mailbox, profile, pending.MessageId, target.DestinationId,
            (run.Answer ?? throw new InvalidOperationException("A completed answer is required.")) +
            "\n\nSources:\n" + sources + "\n\nView the answer and original sources (sign-in required): " +
            options.RunUrl(run.RunId), cancellationToken);
    }
}
