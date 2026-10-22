using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;

namespace DocIntel.Core.Channels;

/// <summary>One operator-configured personal-chat bot, bound to one application tenant.</summary>
public sealed class TeamsChannelOptions
{
    public const string SectionName = "Channels:Teams";
    public bool Enabled { get; set; }
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public string ApplicationTenantId { get; set; } = String.Empty;
    public string ServiceUrl { get; set; } = String.Empty;
    public string PublicOrigin { get; set; } = String.Empty;

    public void Validate()
    {
        if (!Enabled) return;
        if (TenantId == Guid.Empty || ClientId == Guid.Empty ||
            PublicOrigin.Length > 512 || ServiceUrl.Length > 512 ||
            String.IsNullOrWhiteSpace(ApplicationTenantId) || ApplicationTenantId.Length > 128 ||
            ApplicationTenantId.Any(c => !Char.IsAsciiLetterOrDigit(c) && c != '-') ||
            !Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var service) ||
            service.Scheme != Uri.UriSchemeHttps || service.Host != "smba.trafficmanager.net" ||
            !service.IsDefaultPort || service.UserInfo.Length != 0 || service.Query.Length != 0 ||
            service.Fragment.Length != 0 ||
            !Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || !origin.IsDefaultPort || origin.UserInfo.Length != 0 ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new InvalidOperationException("Enabled Teams delivery requires valid bot/directory IDs, an application tenant, " +
                "an HTTPS smba.trafficmanager.net service URL and an HTTPS application origin.");
    }

    public string Profile => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        TenantId,
        ClientId,
        ApplicationTenantId,
        ServiceUrl = new Uri(ServiceUrl).AbsoluteUri,
        PublicOrigin = new Uri(PublicOrigin).GetLeftPart(UriPartial.Authority)
    })));

    public string RunUrl(string runId) => PublicOrigin.TrimEnd('/') + "/ask-run.html?tenant=" +
        Uri.EscapeDataString(ApplicationTenantId) + "&run=" + Uri.EscapeDataString(runId);
}

/// <summary>Activity fields supplied only by the authenticated native Teams adapter.</summary>
public sealed record VerifiedTeamsQuestion(
    string ChannelId, string? DirectoryTenantId, string? ConversationTenantId, string? ObjectId,
    string? RecipientId, string? ConversationType, bool? IsGroup, string? ConversationId,
    string? MessageId, Uri? ServiceUrl, string? Text);

public sealed record TeamsSubmission(bool Created, string RunId, string StatusUrl);

/// <summary>Produces one small plaintext preview; the complete answer remains behind application sign-in.</summary>
public static class TeamsAnswerFormatter
{
    public static string Format(AskAiRun run, TeamsChannelOptions options)
    {
        var text = run.Answer ?? throw new InvalidOperationException("No completed answer is available.");
        var citations = String.Join("\n", run.Citations.Select(citation =>
            $"[{citation.CitationNumber}] {citation.FileName} | {citation.DocumentName} | {citation.PageRange}" +
            (citation.ReviewVersion is null ? "" : $" | human review v{citation.ReviewVersion}")));
        var suffix = "\n\n" + citations + "\n\nFull answer and source links (sign-in required): " + options.RunUrl(run.RunId);
        const int maximumTextCharacters = 3_000;
        if (suffix.Length >= maximumTextCharacters)
            suffix = "\n\nFull answer and all cited sources (sign-in required): " + options.RunUrl(run.RunId);
        var available = maximumTextCharacters - suffix.Length;
        if (text.Length <= available) return text + suffix;
        const string shortened = "\n[Preview shortened; open full answer.]";
        var length = Math.Max(0, available - shortened.Length);
        if (length > 0 && Char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + shortened + suffix;
    }
}

/// <summary>Maps verified personal-chat activities to the shared durable Ask AI execution path.</summary>
public sealed class TeamsQuestionReceiver(
    TeamsChannelOptions options, ITenantAccessResolver access, IHarnessStore harness,
    IAskAiRunStore runs, IAskAiQueue queue)
{
    public async Task<TeamsSubmission> ReceiveAsync(
        VerifiedTeamsQuestion question, CancellationToken cancellationToken = default)
    {
        var actor = ValidateRouting(question);
        var validation = AskAiRequestValidator.GetError(question.Text);
        if (validation is not null) throw new ArgumentException(validation);
        var scope = await access.ResolveAsync(actor, options.ApplicationTenantId, cancellationToken);
        if (!scope.CanRead) throw new UnauthorizedAccessException("This user has no document tenant membership.");
        var tenant = await harness.GetTenantAsync(options.ApplicationTenantId, cancellationToken);
        if (tenant is not { IsActive: true } ||
            !tenant.Capabilities.TryGetValue(FeatureHarness.AskAiKey, out var feature) || !feature.Enabled)
            throw new UnauthorizedAccessException("Ask AI is not enabled for this tenant.");
        if (tenant.Agent.TeamsEnabled != true)
            throw new UnauthorizedAccessException("Teams questions are not enabled for this tenant.");

        var runId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            options.TenantId,
            options.ClientId,
            options.ApplicationTenantId,
            actor.ObjectId,
            question.ConversationId,
            question.MessageId
        }))));
        var run = new AskAiRun
        {
            RunId = runId,
            TenantId = options.ApplicationTenantId,
            Actor = actor,
            Question = question.Text!.Trim(),
            ReplyChannel = new AskAiReplyChannel { ChannelType = AskAiChannelType.Teams },
            ChannelDelivery = new ChannelDelivery
            {
                Transport = "Teams",
                Profile = options.Profile,
                DestinationId = question.ConversationId!,
                MessageId = question.MessageId
            }
        };
        var created = await runs.TryCreateRunAsync(run, cancellationToken);
        if (!created)
        {
            var existing = await runs.GetRunAsync(run.TenantId, runId, cancellationToken)
                ?? throw new InvalidDataException("A duplicate Teams activity has no stored run.");
            if (existing.Actor != actor || existing.Question != run.Question ||
                existing.ChannelDelivery?.Profile != options.Profile ||
                existing.ChannelDelivery.DestinationId != question.ConversationId ||
                existing.ChannelDelivery.MessageId != question.MessageId)
                throw new InvalidDataException("Duplicate activity contents differ from the original verified question.");
        }
        else
        {
            await queue.TryEnqueueAsync(new AskAiQueueTicket(runId, run.TenantId), cancellationToken);
        }
        return new TeamsSubmission(created, runId, options.RunUrl(runId));
    }

    /// <summary>Checks the private routing envelope before even a generic acceptance/rejection reply is allowed.</summary>
    public ActorIdentity ValidateRouting(VerifiedTeamsQuestion question)
    {
        options.Validate();
        if (!options.Enabled) throw new UnauthorizedAccessException("Teams is not enabled.");
        if (question.ChannelId != "msteams" || question.ConversationType != "personal" || question.IsGroup == true ||
            !Guid.TryParse(question.DirectoryTenantId, out var directory) || directory != options.TenantId ||
            !Guid.TryParse(question.ConversationTenantId, out var conversationDirectory) || conversationDirectory != directory ||
            !Guid.TryParse(question.ObjectId, out var objectId) || objectId == Guid.Empty ||
            question.RecipientId != $"28:{options.ClientId:D}" ||
            question.ServiceUrl?.AbsoluteUri != new Uri(options.ServiceUrl).AbsoluteUri ||
            !ValidId(question.ConversationId) || !ValidId(question.MessageId))
            throw new UnauthorizedAccessException("Only verified personal Teams conversations for the configured bot are accepted.");
        return new ActorIdentity(directory, objectId);
    }

    private static bool ValidId(string? value) => !String.IsNullOrWhiteSpace(value) && value.Length <= 512 &&
        !value.Any(Char.IsControl);
}
