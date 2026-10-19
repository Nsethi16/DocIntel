using DocIntel.Core.Features.AskAi;
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
    public async Task Host_Shutdown_Persists_An_Interrupted_State_With_A_Fresh_Token()
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
        Assert.Equal(JobStatus.Failed, failed?.Status);
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

    private static AskAiRun CreateRun(string tenantId = "tenant-one") => new()
    {
        TenantId = tenantId,
        RunId = "same-id",
        Question = "Hi"
    };

    private static ChatResponse Greeting() => new(new ChatMessage(ChatRole.Assistant,
        "{\"kind\":\"Conversation\",\"answer\":\"Hello!\",\"evidenceIds\":[]}"));

    private static AskAiRunProcessor CreateProcessor(
        IAskAiRunStore store, IChatClient client, AskAiExecutionOptions? options = null)
    {
        var tenant = new TenantHarness { TenantId = "tenant-one", DisplayName = "Test" };
        var harness = new Mock<IHarnessStore>();
        harness.Setup(s => s.GetTenantAsync(tenant.TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        var catalog = new ModelCatalog(customClients: new Dictionary<string, IChatClient>
        {
            [tenant.DefaultModelId] = client
        });
        var orchestrator = new AskAiOrchestrator(
            harness.Object, new InMemoryJobStore(), catalog, [], NullLogger<AskAiOrchestrator>.Instance);
        return new AskAiRunProcessor(
            store, orchestrator, options ?? new AskAiExecutionOptions(), NullLogger<AskAiRunProcessor>.Instance);
    }
}
