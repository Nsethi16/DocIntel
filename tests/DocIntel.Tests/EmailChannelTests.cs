using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class EmailChannelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trusted_Employee_Email_Executes_Ask_AI_Without_Confirmation_And_Rechecks_Access_Before_Sending(bool revoke)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Options.AuthorizationMode = EmailAuthorizationMode.TrustedInternal;
        fixture.Options.CorporateSenderProof = new String('a', 43);
        var authorization = new Mock<IAutomaticEmailAuthorization>();
        authorization.Setup(a => a.ResolveAsync("tenant", fixture.Message.Sender, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VerifiedEmailActor(fixture.Actor, "reader@example.com"));
        var receiver = new EmailQuestionReceiver(fixture.Options, fixture.Runs, fixture.Harness.Object,
            fixture.Mailbox.Object, NullLogger<EmailQuestionReceiver>.Instance, authorization.Object, fixture.Queue.Object);
        var model = new Mock<IChatClient>();
        model.Setup(m => m.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "{\"kind\":\"Conversation\",\"answer\":\"Hello from Meridian\",\"evidenceIds\":[]}")));
        var catalog = new ModelCatalog(customClients: new Dictionary<string, IChatClient>
        { [fixture.Tenant.DefaultModelId] = model.Object });
        var jobs = new InMemoryJobStore();
        var orchestrator = new AskAiOrchestrator(fixture.Harness.Object, jobs, catalog, null,
            NullLogger<AskAiOrchestrator>.Instance, fixture.Access.Object);
        var delivery = new ChannelDeliveryProcessor(
            [new EmailAnswerTransport(fixture.Options, fixture.Mailbox.Object)],
            NullLogger<ChannelDeliveryProcessor>.Instance, fixture.Access.Object, fixture.Harness.Object, jobs);
        var processor = new AskAiRunProcessor(fixture.Runs, orchestrator, new(),
            NullLogger<AskAiRunProcessor>.Instance, delivery);

        // Act
        await receiver.ReceiveAsync(fixture.Message with { Text = "Hello", TrustedInternalSender = true });
        var run = await fixture.RunAsync();
        Assert.Equal(JobStatus.Queued, run.Status);
        Assert.Equal(fixture.Actor, run.Actor);
        Assert.Null(run.PendingEmail!.ConfirmedAtUtc);
        Assert.NotNull(run.PendingEmail.AuthenticatedAtUtc);
        await processor.ProcessAsync(new(run.RunId, run.TenantId));
        if (revoke)
            fixture.Access.Setup(a => a.ResolveAsync(fixture.Actor, "tenant", It.IsAny<CancellationToken>()))
                .ReturnsAsync(DocumentAccessScope.Denied);
        await processor.ProcessAsync(new(run.RunId, run.TenantId));
        await receiver.ReceiveAsync(fixture.Message with { Text = "Hello", TrustedInternalSender = true });

        // Assert
        var completed = await fixture.RunAsync();
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal(revoke ? ChannelDeliveryStatus.Failed : ChannelDeliveryStatus.Sent, completed.ChannelDelivery!.Status);
        fixture.Mailbox.Verify(m => m.SendAsync("reader@example.com", It.IsAny<string>(),
            It.Is<string>(body => body.Contains("Hello from Meridian")), It.IsAny<CancellationToken>()),
            revoke ? Times.Never() : Times.Once());
        fixture.Mailbox.Verify(m => m.SendAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.Is<string>(body => body.Contains("/email-question.html")), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Queue.Verify(q => q.TryEnqueueAsync(It.IsAny<AskAiQueueTicket>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_Email_Rejects_Untrusted_Or_Unassigned_Senders_Before_Creating_Any_Run(bool trusted)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Options.AuthorizationMode = EmailAuthorizationMode.TrustedInternal;
        fixture.Options.CorporateSenderProof = new String('b', 43);
        var authorization = new Mock<IAutomaticEmailAuthorization>();
        var receiver = new EmailQuestionReceiver(fixture.Options, fixture.Runs, fixture.Harness.Object,
            fixture.Mailbox.Object, NullLogger<EmailQuestionReceiver>.Instance, authorization.Object, fixture.Queue.Object);

        // Act
        await receiver.ReceiveAsync(fixture.Message with { TrustedInternalSender = trusted });

        // Assert
        Assert.Empty(await fixture.Runs.ListRunsByTenantAsync("tenant"));
        fixture.Mailbox.Verify(m => m.SendAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Queue.Verify(q => q.TryEnqueueAsync(It.IsAny<AskAiQueueTicket>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Inbound_Email_Is_Deduplicated_And_Sends_Only_A_Source_Free_Confirmation_Not_An_Answer()
    {
        // Arrange
        var fixture = new Fixture();
        string? sent = null;
        fixture.Mailbox.Setup(m => m.SendAsync("reader@example.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, text, _) => sent = text).Returns(Task.CompletedTask);

        // Act
        await fixture.Receiver.ReceiveAsync(fixture.Message);
        await fixture.Receiver.ReceiveAsync(fixture.Message);
        var run = await fixture.RunAsync();

        // Assert
        Assert.Equal(JobStatus.AwaitingConfirmation, run.Status);
        Assert.Null(run.Actor);
        Assert.Null(run.Answer);
        Assert.Null(run.ChannelDelivery);
        Assert.Contains("/email-question.html?", sent);
        Assert.DoesNotContain(fixture.Message.Text, sent);
        Assert.DoesNotContain("PRIVATE_DOCUMENT_RESULT", sent);
        Assert.Empty(await fixture.Runs.ListUnfinishedRunsAsync(100));
        fixture.Mailbox.Verify(m => m.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_Forwarded_Confirmation_Link_Cannot_Use_Another_Users_Access_To_Send_Data_To_The_Original_Sender()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.Receiver.ReceiveAsync(fixture.Message);
        var run = await fixture.RunAsync();
        var other = new ActorIdentity(fixture.Actor.DirectoryTenantId, Guid.NewGuid());
        fixture.Mailbox.Setup(m => m.GetActorAddressesAsync(other, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["other@example.com"]);

        // Act / Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Confirmation.ReadAsync(other, "tenant", run.RunId, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Confirmation.ConfirmAsync(other, "tenant", run.RunId, CancellationToken.None));
        Assert.Null((await fixture.RunAsync()).Actor);
        fixture.Queue.Verify(q => q.TryEnqueueAsync(It.IsAny<AskAiQueueTicket>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reading_The_Question_Never_Executes_It_And_Explicit_Confirmation_Queues_Exactly_Once()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.Receiver.ReceiveAsync(fixture.Message);
        var pending = await fixture.RunAsync();

        // Act
        var shown = await fixture.Confirmation.ReadAsync(fixture.Actor, "tenant", pending.RunId, CancellationToken.None);
        Assert.Equal(JobStatus.AwaitingConfirmation, shown.Status);
        fixture.Queue.Verify(q => q.TryEnqueueAsync(It.IsAny<AskAiQueueTicket>(), It.IsAny<CancellationToken>()), Times.Never);
        await fixture.Confirmation.ConfirmAsync(fixture.Actor, "tenant", pending.RunId, CancellationToken.None);
        await fixture.Confirmation.ConfirmAsync(fixture.Actor, "tenant", pending.RunId, CancellationToken.None);
        var confirmed = await fixture.RunAsync();

        // Assert
        Assert.Equal(fixture.Actor, confirmed.Actor);
        Assert.Equal(JobStatus.Queued, confirmed.Status);
        Assert.Equal("reader@example.com", confirmed.ChannelDelivery!.DestinationId);
        Assert.Equal(fixture.Options.Profile, confirmed.ChannelDelivery.Profile);
        Assert.NotNull(confirmed.PendingEmail!.ConfirmedAtUtc);
        fixture.Queue.Verify(q => q.TryEnqueueAsync(It.IsAny<AskAiQueueTicket>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_Uncertain_Confirmation_Email_Is_Not_Automatically_Resent()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Mailbox.Setup(m => m.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Unknown result"));

        // Act
        await fixture.Receiver.ReceiveAsync(fixture.Message);
        await fixture.Receiver.ReceiveAsync(fixture.Message);

        // Assert
        Assert.Equal(ChannelDeliveryStatus.OutcomeUnknown, (await fixture.RunAsync()).PendingEmail!.ChallengeStatus);
        fixture.Mailbox.Verify(m => m.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Message_Lease_Contention_Is_Retryable_Instead_Of_Acknowledging_An_Unsent_Challenge()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.Receiver.ReceiveAsync(fixture.Message);
        var run = await fixture.RunAsync();
        await using var owner = await fixture.Runs.TryAcquireAsync("tenant", run.RunId);
        owner!.Run.PendingEmail!.ChallengeStatus = ChannelDeliveryStatus.Pending;
        await owner.SaveAsync();

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Receiver.ReceiveAsync(fixture.Message));
        Assert.Equal(ChannelDeliveryStatus.Pending, (await fixture.RunAsync()).PendingEmail!.ChallengeStatus);
        fixture.Mailbox.Verify(m => m.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Lost_Reply_Address_Ownership_Blocks_External_Delivery()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.Receiver.ReceiveAsync(fixture.Message);
        var pending = await fixture.RunAsync();
        var run = await fixture.Confirmation.ConfirmAsync(fixture.Actor, "tenant", pending.RunId, CancellationToken.None);
        run.Answer = "PRIVATE_DOCUMENT_RESULT";
        fixture.Mailbox.Setup(m => m.GetActorAddressesAsync(fixture.Actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["changed@example.com"]);

        // Act / Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new EmailAnswerTransport(fixture.Options, fixture.Mailbox.Object).SendAsync(run, run.ChannelDelivery!));
        fixture.Mailbox.Verify(m => m.SendAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.Is<string>(text => text.Contains("PRIVATE_DOCUMENT_RESULT")), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Disabled_Channels_Automated_Mail_And_Revoked_Access_Cannot_Trigger_Processing()
    {
        // Arrange
        var fixture = new Fixture();

        // Act / Assert
        await fixture.Receiver.ReceiveAsync(fixture.Message with { Automatic = true });
        Assert.Empty(await fixture.Runs.ListRunsByTenantAsync("tenant"));
        await fixture.Receiver.ReceiveAsync(fixture.Message);
        var run = await fixture.RunAsync();
        fixture.Tenant.Agent = fixture.Tenant.Agent with { EmailEnabled = false };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Confirmation.ConfirmAsync(fixture.Actor, "tenant", run.RunId, CancellationToken.None));
        fixture.Queue.Verify(q => q.TryEnqueueAsync(It.IsAny<AskAiQueueTicket>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class Fixture
    {
        public ActorIdentity Actor { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        public EmailChannelOptions Options { get; }
        public TenantHarness Tenant { get; } = new()
        {
            TenantId = "tenant",
            DisplayName = "Tenant",
            Agent = new() { DisplayName = "Meridian", EmailEnabled = true }
        };
        public InMemoryAskAiRunStore Runs { get; } = new();
        public Mock<IMailboxClient> Mailbox { get; } = new();
        public Mock<IAskAiQueue> Queue { get; } = new();
        public Mock<IHarnessStore> Harness { get; } = new();
        public Mock<ITenantAccessResolver> Access { get; } = new();
        public EmailQuestionReceiver Receiver { get; }
        public EmailQuestionConfirmation Confirmation { get; }
        public MailboxQuestion Message { get; } =
            new("graph-message-one", "reader@example.com", "Question", "What is the contract renewal date?", DateTimeOffset.UtcNow);
        public Fixture()
        {
            Options = new EmailChannelOptions
            {
                Enabled = true,
                TenantId = Actor.DirectoryTenantId,
                MailboxObjectId = Guid.NewGuid(),
                ApplicationTenantId = "tenant",
                MailboxAddress = "agent@example.com",
                PublicOrigin = "https://docintel.example"
            };
            Harness.Setup(h => h.GetTenantAsync("tenant", It.IsAny<CancellationToken>())).ReturnsAsync(Tenant);
            Access.Setup(a => a.ResolveAsync(It.IsAny<ActorIdentity>(), "tenant", It.IsAny<CancellationToken>()))
                .ReturnsAsync(DocumentAccessScope.Unrestricted);
            Mailbox.Setup(m => m.GetActorAddressesAsync(Actor, It.IsAny<CancellationToken>())).ReturnsAsync(["reader@example.com"]);
            Receiver = new EmailQuestionReceiver(Options, Runs, Harness.Object, Mailbox.Object, NullLogger<EmailQuestionReceiver>.Instance);
            Confirmation = new EmailQuestionConfirmation(Options, Mailbox.Object, Access.Object, Runs, Queue.Object, Harness.Object);
        }
        public async Task<AskAiRun> RunAsync() => Assert.Single(await Runs.ListRunsByTenantAsync("tenant"));
    }
}
