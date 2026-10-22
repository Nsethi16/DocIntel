using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class TeamsChannelTests
{
    [Fact]
    public async Task A_Verified_Personal_Question_Is_Persisted_Before_Dispatch_And_Deduplicated()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Queue.Setup(q => q.TryEnqueueAsync(It.IsAny<AskAiQueueTicket>(), It.IsAny<CancellationToken>()))
            .Returns<AskAiQueueTicket, CancellationToken>(async (ticket, ct) =>
            {
                Assert.NotNull(await fixture.Runs.GetRunAsync(ticket.TenantId, ticket.RunId, ct));
                return false;
            });

        // Act
        var first = await fixture.Receiver.ReceiveAsync(fixture.Question);
        var duplicate = await fixture.Receiver.ReceiveAsync(fixture.Question);
        var persisted = await fixture.Runs.GetRunAsync("tenant", first.RunId);

        // Assert
        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(first.RunId, duplicate.RunId);
        Assert.Equal(fixture.Actor, persisted!.Actor);
        Assert.Equal("private-conversation", persisted.ChannelDelivery!.DestinationId);
        Assert.Equal(fixture.Options.Profile, persisted.ChannelDelivery.Profile);
        Assert.Null(persisted.ReplyChannel.ServiceUrl);
        Assert.Null(persisted.ReplyChannel.CallbackWebhookUrl);
        Assert.Equal(JobStatus.Queued, persisted.Status);
        Assert.Single(await fixture.Runs.ListUnfinishedRunsAsync(100));
        Assert.StartsWith("https://docintel.example/ask-run.html?tenant=tenant&run=", first.StatusUrl);
        fixture.Queue.Verify(q => q.TryEnqueueAsync(It.IsAny<AskAiQueueTicket>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("channel")]
    [InlineData("group")]
    [InlineData("directory")]
    [InlineData("conversation-directory")]
    [InlineData("missing-actor")]
    [InlineData("recipient")]
    [InlineData("service-url")]
    [InlineData("service-fragment")]
    [InlineData("oversized-id")]
    [InlineData("control-id")]
    public async Task Untrusted_Or_Shared_Conversation_Fields_Cannot_Create_Work(string invalid)
    {
        // Arrange
        var fixture = new Fixture();
        var question = invalid switch
        {
            "channel" => fixture.Question with { ChannelId = "other" },
            "group" => fixture.Question with { IsGroup = true },
            "directory" => fixture.Question with { DirectoryTenantId = Guid.NewGuid().ToString() },
            "conversation-directory" => fixture.Question with { ConversationTenantId = Guid.NewGuid().ToString() },
            "missing-actor" => fixture.Question with { ObjectId = null },
            "recipient" => fixture.Question with { RecipientId = "28:" + Guid.NewGuid() },
            "service-url" => fixture.Question with { ServiceUrl = new Uri("https://attacker.example/") },
            "service-fragment" => fixture.Question with { ServiceUrl = new Uri(fixture.Options.ServiceUrl + "#fragment") },
            "oversized-id" => fixture.Question with { ConversationId = new string('a', 513) },
            _ => fixture.Question with { MessageId = "line\nbreak" }
        };

        // Act / Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Receiver.ReceiveAsync(question));
        Assert.Empty(await fixture.Runs.ListRunsByTenantAsync("tenant"));
        fixture.Queue.Verify(q => q.TryEnqueueAsync(It.IsAny<AskAiQueueTicket>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Missing_Current_Membership_And_Disabled_Tenants_Block_Question_Submission()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Access.Setup(a => a.ResolveAsync(fixture.Actor, "tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentAccessScope.Denied);

        // Act / Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Receiver.ReceiveAsync(fixture.Question));
        fixture.Access.Setup(a => a.ResolveAsync(fixture.Actor, "tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentAccessScope.Unrestricted);
        fixture.Tenant.IsActive = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Receiver.ReceiveAsync(fixture.Question));
        Assert.Empty(await fixture.Runs.ListRunsByTenantAsync("tenant"));
    }

    [Fact]
    public async Task Changed_Replay_Content_Cannot_Replace_The_Original_Question()
    {
        // Arrange
        var fixture = new Fixture();
        var accepted = await fixture.Receiver.ReceiveAsync(fixture.Question);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Receiver.ReceiveAsync(fixture.Question with { Text = "Different question" }));
        Assert.Equal(fixture.Question.Text, (await fixture.Runs.GetRunAsync("tenant", accepted.RunId))!.Question);
    }

    [Fact]
    public async Task Blank_And_Oversized_Questions_Are_Rejected_Before_Creating_Work()
    {
        // Arrange
        var fixture = new Fixture();

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Receiver.ReceiveAsync(fixture.Question with { Text = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Receiver.ReceiveAsync(fixture.Question with { Text = new string('x', 8_001) }));
        Assert.Empty(await fixture.Runs.ListRunsByTenantAsync("tenant"));
    }

    [Fact]
    public void Routing_Changes_Invalidate_The_Persisted_Delivery_Profile()
    {
        // Arrange
        var fixture = new Fixture();
        var original = fixture.Options.Profile;

        // Act
        fixture.Options.ServiceUrl = "https://smba.trafficmanager.net/emea/";

        // Assert
        Assert.NotEqual(original, fixture.Options.Profile);
        fixture.Options.ServiceUrl = "http://127.0.0.1/";
        Assert.Throws<InvalidOperationException>(fixture.Options.Validate);
    }

    [Fact]
    public async Task Long_Unicode_Answers_Have_One_Bounded_Preview_And_An_Authenticated_Full_Answer_Link()
    {
        // Arrange
        var fixture = new Fixture();
        var accepted = await fixture.Receiver.ReceiveAsync(fixture.Question);
        var run = (await fixture.Runs.GetRunAsync("tenant", accepted.RunId))!;
        run.Answer = String.Concat(Enumerable.Repeat("\U0001f600\u6f22", 4_000));
        for (var i = 1; i <= 48; i++)
            run.Citations.Add(new AskAiCitation(i, "doc", new string('n', 120), "doc", 0, "Invoice",
                "1", "", "", "", "", "", "", "/review", "/pdf", "HumanReviewedField", 3));

        // Act
        var preview = TeamsAnswerFormatter.Format(run, fixture.Options);

        // Assert
        Assert.True(preview.Length <= 3_000);
        Assert.DoesNotContain('\uFFFD', preview);
        Assert.Contains("Preview shortened", preview);
        Assert.Contains(accepted.StatusUrl, preview);
        Assert.Contains("all cited sources", preview);
        Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { text = preview }).Length < 20_000);
    }

    private sealed class Fixture
    {
        public ActorIdentity Actor { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        public TeamsChannelOptions Options { get; }
        public TenantHarness Tenant { get; } = new()
        {
            TenantId = "tenant",
            DisplayName = "Tenant",
            Agent = new() { TeamsEnabled = true }
        };
        public InMemoryAskAiRunStore Runs { get; } = new();
        public Mock<ITenantAccessResolver> Access { get; } = new();
        public Mock<IAskAiQueue> Queue { get; } = new();
        public TeamsQuestionReceiver Receiver { get; }
        public VerifiedTeamsQuestion Question { get; }

        public Fixture()
        {
            Options = new TeamsChannelOptions
            {
                Enabled = true,
                TenantId = Actor.DirectoryTenantId,
                ClientId = Guid.NewGuid(),
                ApplicationTenantId = "tenant",
                PublicOrigin = "https://docintel.example",
                ServiceUrl = "https://smba.trafficmanager.net/teams/"
            };
            Access.Setup(a => a.ResolveAsync(Actor, "tenant", It.IsAny<CancellationToken>()))
                .ReturnsAsync(DocumentAccessScope.Unrestricted);
            var harness = new Mock<IHarnessStore>();
            harness.Setup(h => h.GetTenantAsync("tenant", It.IsAny<CancellationToken>())).ReturnsAsync(Tenant);
            Receiver = new TeamsQuestionReceiver(Options, Access.Object, harness.Object, Runs, Queue.Object);
            Question = new VerifiedTeamsQuestion("msteams", Actor.DirectoryTenantId.ToString("D"),
                Actor.DirectoryTenantId.ToString("D"), Actor.ObjectId.ToString("D"), $"28:{Options.ClientId:D}",
                "personal", false, "private-conversation", "message", new Uri(Options.ServiceUrl), "Question");
        }
    }
}
