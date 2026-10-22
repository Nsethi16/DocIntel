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

/// <summary>Operator-owned mailbox binding. No sender address or URL grants document permissions.</summary>
public sealed class EmailChannelOptions
{
    public const string SectionName = "Channels:Email";
    public bool Enabled { get; set; }
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
        if (TenantId == Guid.Empty || MailboxObjectId == Guid.Empty || !IsAddress(MailboxAddress) ||
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
    public string ConfirmationUrl(string runId) => PublicOrigin.TrimEnd('/') + "/email-question.html?tenant=" +
        Uri.EscapeDataString(ApplicationTenantId) + "&run=" + Uri.EscapeDataString(runId);
    public string RunUrl(string runId) => PublicOrigin.TrimEnd('/') + "/ask-run.html?tenant=" +
        Uri.EscapeDataString(ApplicationTenantId) + "&run=" + Uri.EscapeDataString(runId);
    public static bool IsAddress(string? value) => value is { Length: <= 254 } &&
        MailAddress.TryCreate(value, out var address) && address.Address == value && !value.Any(Char.IsControl);
}

public sealed record MailboxQuestion(string MessageId, string Sender, string Subject, string Text,
    DateTimeOffset ReceivedAtUtc, bool Automatic = false, bool TrustedInternalSender = false);
public sealed record MailboxChanges(IReadOnlyList<MailboxQuestion> Messages, string? NextLink, string? DeltaLink);

public interface IMailboxClient
{
    Task<MailboxChanges> ReadChangesAsync(string? continuation, CancellationToken cancellationToken,
        DateTimeOffset? receivedAfterUtc = null);
    Task<IReadOnlyList<string>> GetActorAddressesAsync(ActorIdentity actor, CancellationToken cancellationToken);
    Task SendAsync(string recipient, string subject, string text, CancellationToken cancellationToken);
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
        var runId = "mail-" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            options.MailboxObjectId,
            options.ApplicationTenantId,
            message.MessageId
        })));
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
                Profile = options.Profile,
                Sender = verified?.Address ?? message.Sender,
                MessageId = message.MessageId,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24),
                AuthorizationMode = options.AuthorizationMode,
                AuthenticatedAtUtc = verified is null ? null : DateTimeOffset.UtcNow
            },
            ChannelDelivery = verified is null ? null : new ChannelDelivery
            {
                Transport = "Email",
                Profile = options.Profile,
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
            lease.Run.PendingEmail is not { } pending || pending.Profile != options.Profile) return;
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
            await mailbox.SendAsync(pending.Sender, $"{tenant.Agent.DisplayName}: confirm your document question",
                $"You received this because a question was emailed to {options.MailboxAddress}.\n\n" +
                "Sign in with your work account, review the question and explicitly confirm it before processing:\n" +
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
                Profile = options.Profile,
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
        if (!options.Enabled || actor.DirectoryTenantId != options.TenantId ||
            run.TenantId != options.ApplicationTenantId || run.PendingEmail is not { } pending ||
            pending.Profile != options.Profile ||
            pending.AuthorizationMode != EmailAuthorizationMode.ConfirmSender ||
            (run.Status == JobStatus.AwaitingConfirmation && pending.ExpiresAtUtc <= DateTimeOffset.UtcNow) ||
            (run.Actor is not null && run.Actor != actor) ||
            !(await access.ResolveAsync(actor, run.TenantId, cancellationToken)).CanRead)
            throw new UnauthorizedAccessException("The email question is unavailable for this account.");
        var tenant = await harness.GetTenantAsync(run.TenantId, cancellationToken);
        if (tenant is not { IsActive: true } || !tenant.Agent.EmailEnabled ||
            !tenant.Capabilities.TryGetValue(FeatureHarness.AskAiKey, out var feature) || !feature.Enabled)
            throw new UnauthorizedAccessException("Email questions are disabled.");
        var addresses = await mailbox.GetActorAddressesAsync(actor, cancellationToken);
        if (!addresses.Contains(pending.Sender, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Sign in using the account that owns the original email address.");
    }
}

/// <summary>One acknowledged Graph send, with current directory-address verification and no arbitrary Reply-To override.</summary>
public sealed class EmailAnswerTransport(
    EmailChannelOptions options, IMailboxClient mailbox) : IChannelTransport
{
    public string Name => "Email";
    public async Task SendAsync(AskAiRun run, ChannelDelivery target, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || target.Profile != options.Profile || run.TenantId != options.ApplicationTenantId ||
            run.Actor is not { } actor || actor.DirectoryTenantId != options.TenantId ||
            run.PendingEmail is not { } pending || pending.Profile != options.Profile ||
            !(pending.AuthorizationMode == EmailAuthorizationMode.ConfirmSender && pending.ConfirmedAtUtc is not null ||
              pending.AuthorizationMode == EmailAuthorizationMode.TrustedInternal &&
              options.AuthorizationMode == EmailAuthorizationMode.TrustedInternal && pending.AuthenticatedAtUtc is not null) ||
            !String.Equals(target.DestinationId, run.PendingEmail.Sender, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The verified mailbox destination is unavailable.");
        var current = await mailbox.GetActorAddressesAsync(actor, cancellationToken);
        if (!current.Contains(target.DestinationId, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The sender no longer owns the reply address.");
        var name = run.AgentProfile?.DisplayName ?? "DocIntel";
        var sources = String.Join("\n", run.Citations.Select(citation =>
            $"[{citation.CitationNumber}] {citation.FileName} | {citation.DocumentName} | {citation.PageRange}"));
        await mailbox.SendAsync(target.DestinationId, name + ": answer to your document question",
            (run.Answer ?? throw new InvalidOperationException("A completed answer is required.")) +
            "\n\nSources:\n" + sources + "\n\nView the answer and original sources (sign-in required): " +
            options.RunUrl(run.RunId), cancellationToken);
    }
}
