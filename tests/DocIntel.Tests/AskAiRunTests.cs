using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class AskAiRunTests
{
    [Fact]
    public async Task Run_Ids_Are_Scoped_To_Their_Tenant()
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        await store.UpsertRunAsync(CreateRun("first"));
        await store.UpsertRunAsync(CreateRun("second"));

        // Act
        var first = await store.GetRunAsync("first", "same-id");
        var second = await store.GetRunAsync("second", "same-id");
        var missing = await store.GetRunAsync("third", "same-id");

        // Assert
        Assert.Equal("first", first?.TenantId);
        Assert.Equal("second", second?.TenantId);
        Assert.Null(missing);
        Assert.Single(await store.ListRunsByTenantAsync("first"));
    }

    [Fact]
    public async Task Polling_Reads_Isolated_Snapshots_Not_A_Live_Mutable_Run()
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var run = CreateRun();
        await store.UpsertRunAsync(run);
        run.Status = JobStatus.Running;

        // Act
        var beforeSave = await store.GetRunAsync(run.TenantId, run.RunId);
        await store.UpsertRunAsync(run);
        var afterSave = await store.GetRunAsync(run.TenantId, run.RunId);
        afterSave!.Steps.Add(new AskAiStepTrace("changed", null, 0, "Not saved."));
        var otherReader = await store.GetRunAsync(run.TenantId, run.RunId);

        // Assert
        Assert.Equal(JobStatus.Queued, beforeSave?.Status);
        Assert.Equal(JobStatus.Running, afterSave.Status);
        Assert.Empty(otherReader!.Steps);
    }

    [Fact]
    public async Task A_Run_Is_Persisted_As_Running_Before_The_First_Model_Call()
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var run = CreateRun();
        await store.UpsertRunAsync(run);
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var snapshot = await store.GetRunAsync(run.TenantId, run.RunId);
                Assert.Equal(JobStatus.Running, snapshot?.Status);
                return Greeting();
            });
        var processor = CreateProcessor(store, client.Object);

        // Act
        await processor.ProcessAsync(new AskAiQueueTicket(run.RunId, run.TenantId));
        var completed = await store.GetRunAsync(run.TenantId, run.RunId);

        // Assert
        Assert.Equal(JobStatus.Completed, completed?.Status);
        Assert.Equal("Hello!", completed?.Answer);
        Assert.NotNull(completed?.StartedAtUtc);
        Assert.NotNull(completed?.CompletedAtUtc);
    }

    [Fact]
    public async Task Provider_Failures_Produce_Safe_Persisted_Error_States()
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var run = CreateRun();
        await store.UpsertRunAsync(run);
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Sensitive provider response."));
        var processor = CreateProcessor(store, client.Object);

        // Act
        await processor.ProcessAsync(new AskAiQueueTicket(run.RunId, run.TenantId));
        var failed = await store.GetRunAsync(run.TenantId, run.RunId);

        // Assert
        Assert.Equal(JobStatus.Failed, failed?.Status);
        Assert.Equal("ExecutionFailed", failed?.ErrorCode);
        Assert.DoesNotContain("Sensitive", failed!.ErrorMessage);
        Assert.Null(failed.Answer);
    }

    [Fact]
    public async Task Failed_Runs_Clear_All_Analytical_Output_And_Source_Context()
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var run = CreateRun();
        run.Analyses = [new() { AnalysisId = "stale", Title = "Stale result" }];
        run.SourceVersions["source"] = new string('A', 64);
        await store.UpsertRunAsync(run);
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Private provider failure."));

        // Act
        await CreateProcessor(store, client.Object).ProcessAsync(new(run.RunId, run.TenantId));
        var failed = await store.GetRunAsync(run.TenantId, run.RunId);

        // Assert
        Assert.Equal(JobStatus.Failed, failed!.Status);
        Assert.Null(failed.Answer);
        Assert.Empty(failed.Analyses);
        Assert.Empty(failed.SourceVersions);
        Assert.Empty(failed.Citations);
    }

    [Theory]
    [InlineData("""{"kind":"DocumentAnswer","answer":"A claim without evidence.","evidenceIds":[]}""", "MissingEvidence")]
    [InlineData("""{"kind":"NoEvidence","answer":"No matches [1].","evidenceIds":[]}""", "CitationMismatch")]
    [InlineData("""{"kind":"Conversation","answer":"","evidenceIds":[]}""", "InvalidModelAnswer")]
    [InlineData("""{"kind":"DocumentAnswer","answer":"A document [1].","evidenceIds":["invented-id"]}""", "UnknownCitation")]
    public async Task Known_Answer_Validation_Failures_Persist_Specific_Safe_Codes(string response, string code)
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var run = CreateRun();
        await store.UpsertRunAsync(run);
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));

        // Act
        await CreateProcessor(store, client.Object).ProcessAsync(new AskAiQueueTicket(run.RunId, run.TenantId));
        var failed = await store.GetRunAsync(run.TenantId, run.RunId);

        // Assert
        Assert.Equal(JobStatus.Failed, failed?.Status);
        Assert.Equal(code, failed?.ErrorCode);
        Assert.NotEqual("ExecutionFailed", failed?.ErrorCode);
        Assert.Null(failed?.Answer);
        Assert.Empty(failed!.Citations);
        Assert.DoesNotContain(response, failed.ErrorMessage);
    }

    [Fact]
    public async Task Execution_Timeout_Does_Not_Leave_A_Run_Running_Forever()
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var run = CreateRun();
        await store.UpsertRunAsync(run);
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<ChatMessage> _, ChatOptions? _, CancellationToken ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Greeting();
            });
        var processor = CreateProcessor(store, client.Object, new AskAiExecutionOptions { MaxRunSeconds = 1 });

        // Act
        await processor.ProcessAsync(new AskAiQueueTicket(run.RunId, run.TenantId));
        var failed = await store.GetRunAsync(run.TenantId, run.RunId);

        // Assert
        Assert.Equal(JobStatus.Failed, failed?.Status);
        Assert.Equal("TimedOut", failed?.ErrorCode);
    }

    [Fact]
    public async Task Inline_Requests_Use_The_Same_Failure_Transitions_As_Queued_Runs()
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Provider unavailable."));

        // Act
        var failed = await CreateProcessor(store, client.Object).ExecuteInlineAsync(CreateRun());

        // Assert
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal("ExecutionFailed", failed.ErrorCode);
        Assert.Equal(JobStatus.Failed,
            (await store.GetRunAsync(failed.TenantId, failed.RunId))?.Status);
    }

    [Fact]
    public async Task Host_Shutdown_Requeues_The_Same_Run_With_A_Fresh_Persistence_Token()
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var run = CreateRun();
        await store.UpsertRunAsync(run);
        using var stopping = new CancellationTokenSource();
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions? _, CancellationToken ct) =>
            {
                stopping.Cancel();
                return Task.FromCanceled<ChatResponse>(ct);
            });

        // Act
        await CreateProcessor(store, client.Object).ProcessAsync(
            new AskAiQueueTicket(run.RunId, run.TenantId), stopping.Token);
        var failed = await store.GetRunAsync(run.TenantId, run.RunId);

        // Assert
        Assert.Equal(JobStatus.Queued, failed?.Status);
        Assert.Equal("Interrupted", failed?.ErrorCode);
    }

    [Fact]
    public async Task A_Completed_Run_Is_Not_Executed_Again_On_Duplicate_Delivery()
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var run = CreateRun();
        run.Status = JobStatus.Completed;
        await store.UpsertRunAsync(run);
        var client = new Mock<IChatClient>(MockBehavior.Strict);

        // Act
        await CreateProcessor(store, client.Object).ProcessAsync(new AskAiQueueTicket(run.RunId, run.TenantId));

        // Assert
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_Worker_That_Loses_Ownership_Does_Not_Overwrite_Its_Replacement()
    {
        // Arrange
        using var lost = new CancellationTokenSource();
        var run = CreateRun();
        var lease = new Mock<IAskAiRunLease>();
        lease.SetupGet(l => l.Run).Returns(run);
        lease.SetupGet(l => l.OwnershipLostToken).Returns(lost.Token);
        lease.Setup(l => l.SaveAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var store = new Mock<IAskAiRunStore>();
        store.Setup(s => s.TryAcquireAsync(run.TenantId, run.RunId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lease.Object);
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions? _, CancellationToken ct) =>
            {
                lost.Cancel();
                return Task.FromCanceled<ChatResponse>(ct);
            });

        // Act
        await CreateProcessor(store.Object, client.Object).ProcessAsync(new AskAiQueueTicket(run.RunId, run.TenantId));

        // Assert
        lease.Verify(l => l.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(run.Answer);
        Assert.Equal(JobStatus.Running, run.Status);
    }

    [Theory]
    [InlineData(false, AskAiChannelType.Email)]
    [InlineData(false, AskAiChannelType.Teams)]
    [InlineData(true, AskAiChannelType.Email)]
    [InlineData(true, AskAiChannelType.Teams)]
    public async Task Inline_Requests_Wait_When_A_Recovery_Worker_Already_Owns_The_Run(
        bool file, AskAiChannelType channel)
    {
        // Arrange
        var directory = Path.Combine("TestOutput", "DocIntel.InlineOwnership.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            IAskAiRunStore store = file ? new FileAskAiRunStore(directory) : new InMemoryAskAiRunStore();
            var run = CreateRun(channel: channel);
            Assert.True(await store.TryCreateRunAsync(run));
            await using var owner = await store.TryAcquireAsync(run.TenantId, run.RunId);
            owner!.Run.Status = JobStatus.Running;
            owner.Run.ExecutionAttempts = 1;
            await owner.SaveAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertRunAsync(run));
            var client = new Mock<IChatClient>(MockBehavior.Strict);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // Act
            var inline = CreateProcessor(store, client.Object).ExecuteInlineAsync(run, deadline.Token);
            Assert.False(inline.IsCompleted);
            owner.Run.Status = JobStatus.Completed;
            owner.Run.Answer = "Finished by the owner.";
            owner.Run.EmailHtmlBody = "<p>Finished by the owner.</p>";
            owner.Run.TeamsAdaptiveCardJson = """{"text":"Finished by the owner."}""";
            await owner.SaveAsync();
            var result = await inline;

            // Assert
            Assert.Equal(JobStatus.Completed, result.Status);
            Assert.Equal("Finished by the owner.", result.Answer);
            Assert.Equal(1, result.ExecutionAttempts);
            Assert.Equal(owner.Run.EmailHtmlBody, result.EmailHtmlBody);
            Assert.Equal(owner.Run.TeamsAdaptiveCardJson, result.TeamsAdaptiveCardJson);
            Assert.Equal(JobStatus.Queued, run.Status);
            Assert.Equal(channel, result.ReplyChannel.ChannelType);
            client.VerifyNoOtherCalls();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inline_Requests_Cannot_Reset_An_Already_Completed_Stored_Answer(bool file)
    {
        // Arrange
        var directory = Path.Combine("TestOutput", "DocIntel.InlineCompleted.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            IAskAiRunStore store = file ? new FileAskAiRunStore(directory) : new InMemoryAskAiRunStore();
            var request = CreateRun();
            Assert.True(await store.TryCreateRunAsync(request));
            await using (var owner = await store.TryAcquireAsync(request.TenantId, request.RunId))
            {
                owner!.Run.Status = JobStatus.Completed;
                owner.Run.Answer = "Already completed by recovery.";
                owner.Run.ExecutionAttempts = 1;
                await owner.SaveAsync();
            }
            var client = new Mock<IChatClient>(MockBehavior.Strict);

            // Act
            var result = await CreateProcessor(store, client.Object).ExecuteInlineAsync(request);

            // Assert
            Assert.Equal(JobStatus.Completed, result.Status);
            Assert.Equal("Already completed by recovery.", result.Answer);
            Assert.Equal(1, result.ExecutionAttempts);
            Assert.Equal(JobStatus.Queued, request.Status);
            client.VerifyNoOtherCalls();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task An_Inline_Replay_Cannot_Use_Another_Actors_Stored_Run()
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var request = CreateRun();
        request.Actor = new ActorIdentity(Guid.NewGuid(), Guid.NewGuid());
        await store.TryCreateRunAsync(request);
        var differentActor = CreateRun();
        differentActor.Actor = new ActorIdentity(request.Actor.DirectoryTenantId, Guid.NewGuid());
        var client = new Mock<IChatClient>(MockBehavior.Strict);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateProcessor(store, client.Object).ExecuteInlineAsync(differentActor));
        Assert.Equal(request.Actor, (await store.GetRunAsync(request.TenantId, request.RunId))!.Actor);
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Recovery_Completed_Delivery_Is_Not_Reset_Or_Resent_By_Duplicate_Inline_Requests()
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var request = CreateRun();
        request.Actor = new ActorIdentity(Guid.NewGuid(), Guid.NewGuid());
        request.ChannelDelivery = new()
        {
            Transport = "Teams", Profile = "configured", DestinationId = "private-chat",
            AccessFingerprint = ChannelDelivery.GetAccessFingerprint(DocumentAccessScope.Unrestricted)
        };
        await store.TryCreateRunAsync(request);
        await using (var owner = await store.TryAcquireAsync(request.TenantId, request.RunId))
        {
            owner!.Run.Status = JobStatus.Completed;
            owner.Run.Answer = "Hello from the recovery owner.";
            owner.Run.ExecutionAttempts = 1;
            await owner.SaveAsync();
        }
        var transport = new Mock<IChannelTransport>();
        transport.SetupGet(channel => channel.Name).Returns("Teams");
        transport.Setup(channel => channel.SendAsync(
            It.IsAny<AskAiRun>(), It.IsAny<ChannelDelivery>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var harness = new Mock<IHarnessStore>();
        harness.Setup(config => config.GetTenantAsync(request.TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantHarness
            {
                TenantId = request.TenantId, DisplayName = "Tenant", Agent = new() { TeamsEnabled = true }
            });
        var access = new Mock<ITenantAccessResolver>();
        access.Setup(resolver => resolver.ResolveAsync(request.Actor, request.TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentAccessScope.Unrestricted);
        var delivery = new ChannelDeliveryProcessor([transport.Object],
            NullLogger<ChannelDeliveryProcessor>.Instance, access.Object, harness.Object, new InMemoryJobStore());
        var client = new Mock<IChatClient>(MockBehavior.Strict);
        var processor = CreateProcessor(store, client.Object, delivery: delivery);

        // Act
        await processor.ExecuteInlineAsync(request);
        var result = await processor.ExecuteInlineAsync(request);

        // Assert
        Assert.Equal(JobStatus.Completed, result.Status);
        Assert.Equal(ChannelDeliveryStatus.Sent, result.ChannelDelivery!.Status);
        Assert.Equal(1, result.ExecutionAttempts);
        Assert.Equal(ChannelDeliveryStatus.Pending, request.ChannelDelivery.Status);
        transport.Verify(channel => channel.SendAsync(
            It.IsAny<AskAiRun>(), It.IsAny<ChannelDelivery>(), It.IsAny<CancellationToken>()), Times.Once);
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Cancelling_An_Inline_Wait_Does_Not_Erase_The_Recovery_Owners_Run()
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var run = CreateRun();
        await store.TryCreateRunAsync(run);
        var owner = (await store.TryAcquireAsync(run.TenantId, run.RunId))!;
        await using (owner)
        {
            owner.Run.Status = JobStatus.Running;
            owner.Run.ExecutionAttempts = 1;
            await owner.SaveAsync();
            using var cancelled = new CancellationTokenSource();
            var client = new Mock<IChatClient>(MockBehavior.Strict);

            // Act
            var inline = CreateProcessor(store, client.Object).ExecuteInlineAsync(run, cancelled.Token);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inline);

            // Assert
            var persisted = (await store.GetRunAsync(run.TenantId, run.RunId))!;
            Assert.Equal(JobStatus.Running, persisted.Status);
            Assert.Equal(1, persisted.ExecutionAttempts);
            client.VerifyNoOtherCalls();
        }
        Assert.Equal(new AskAiQueueTicket(run.RunId, run.TenantId),
            Assert.Single(await store.ListUnfinishedRunsAsync(100)));
    }

    [Fact]
    public async Task Interrupted_Read_Only_Runs_Are_Replayed_From_Their_Persisted_Question()
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var run = CreateRun();
        run.Status = JobStatus.Running;
        run.ExecutionAttempts = 1;
        run.Steps.Add(new AskAiStepTrace("prior attempt", null, 1, "Interrupted."));
        await store.UpsertRunAsync(run);
        var client = GreetingClient();

        // Act
        await CreateProcessor(store, client.Object).ProcessAsync(new AskAiQueueTicket(run.RunId, run.TenantId));
        var recovered = await store.GetRunAsync(run.TenantId, run.RunId);

        // Assert
        Assert.Equal(JobStatus.Completed, recovered?.Status);
        Assert.Equal(2, recovered?.ExecutionAttempts);
        Assert.Empty(recovered!.Steps);
    }

    [Fact]
    public async Task Repeated_Crash_Recovery_Has_A_Bounded_Attempt_Count()
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var run = CreateRun();
        run.Status = JobStatus.Running;
        run.ExecutionAttempts = 3;
        await store.UpsertRunAsync(run);
        var client = new Mock<IChatClient>(MockBehavior.Strict);

        // Act
        await CreateProcessor(store, client.Object).ProcessAsync(new AskAiQueueTicket(run.RunId, run.TenantId));
        var failed = await store.GetRunAsync(run.TenantId, run.RunId);

        // Assert
        Assert.Equal("RecoveryLimitExceeded", failed?.ErrorCode);
        Assert.Equal(JobStatus.Failed, failed?.Status);
        client.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Queued)]
    public async Task Extension_Tools_Are_Not_Automatically_Replayed_After_Interruption(JobStatus status)
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var run = CreateRun();
        run.Status = status;
        run.ExecutionAttempts = 1;
        await store.UpsertRunAsync(run);
        var client = new Mock<IChatClient>(MockBehavior.Strict);
        var provider = new Mock<IAskAiToolProvider>();

        // Act
        await CreateProcessor(store, client.Object, providers: [provider.Object])
            .ProcessAsync(new AskAiQueueTicket(run.RunId, run.TenantId));
        var failed = await store.GetRunAsync(run.TenantId, run.RunId);

        // Assert
        Assert.Equal("RecoveryRequiresReview", failed?.ErrorCode);
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Concurrent_Duplicate_Tickets_Execute_Only_Once()
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var run = CreateRun();
        await store.UpsertRunAsync(run);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() => { entered.SetResult(); return result.Task; });
        var processor = CreateProcessor(store, client.Object);
        var ticket = new AskAiQueueTicket(run.RunId, run.TenantId);

        // Act
        var first = processor.ProcessAsync(ticket);
        await entered.Task;
        await processor.ProcessAsync(ticket);
        result.SetResult(Greeting());
        await first;

        // Assert
        client.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_Ticket_For_Another_Tenant_Cannot_Execute_The_Run()
    {
        // Arrange
        var store = new InMemoryAskAiRunStore();
        var run = CreateRun();
        await store.UpsertRunAsync(run);
        var client = new Mock<IChatClient>(MockBehavior.Strict);

        // Act
        await CreateProcessor(store, client.Object).ProcessAsync(new AskAiQueueTicket(run.RunId, "other"));

        // Assert
        client.VerifyNoOtherCalls();
        Assert.Equal(JobStatus.Queued, (await store.GetRunAsync(run.TenantId, run.RunId))?.Status);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Hi", "http://localhost/callback")]
    public void Invalid_Requests_Are_Rejected_At_The_Api_Boundary(string? question, string? callback)
    {
        // Arrange / Act
        var error = AskAiRequestValidator.GetError(question, callback);

        // Assert
        Assert.NotNull(error);
    }

    [Fact]
    public void Question_Length_Is_Bounded_Before_Enqueueing()
    {
        // Arrange / Act
        var error = AskAiRequestValidator.GetError(new String('x', 8_001));

        // Assert
        Assert.Contains("8,000", error);
    }

    private static AskAiRun CreateRun(
        string tenantId = "tenant-one", AskAiChannelType channel = AskAiChannelType.Web) => new()
    {
        TenantId = tenantId,
        RunId = "same-id",
        Question = "Hi",
        ReplyChannel = new() { ChannelType = channel }
    };

    private static ChatResponse Greeting() => new(new ChatMessage(ChatRole.Assistant,
        "{\"kind\":\"Conversation\",\"answer\":\"Hello!\",\"evidenceIds\":[]}"));

    private static Mock<IChatClient> GreetingClient()
    {
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Greeting());
        return client;
    }

    private static AskAiRunProcessor CreateProcessor(
        IAskAiRunStore store, IChatClient client, AskAiExecutionOptions? options = null,
        IEnumerable<IAskAiToolProvider>? providers = null, ChannelDeliveryProcessor? delivery = null)
    {
        var tenant = new TenantHarness { TenantId = "tenant-one", DisplayName = "Test" };
        var harness = new Mock<IHarnessStore>();
        harness.Setup(s => s.GetTenantAsync(tenant.TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        var catalog = new ModelCatalog(customClients: new Dictionary<string, IChatClient>
        {
            [tenant.DefaultModelId] = client
        });
        var orchestrator = new AskAiOrchestrator(
            harness.Object, new InMemoryJobStore(), catalog, providers, NullLogger<AskAiOrchestrator>.Instance);
        return new AskAiRunProcessor(
            store, orchestrator, options ?? new AskAiExecutionOptions(),
            NullLogger<AskAiRunProcessor>.Instance, delivery);
    }
}
