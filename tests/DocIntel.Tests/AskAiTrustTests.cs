using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class AskAiTrustTests
{
    [Fact]
    public void Missing_Model_Endpoint_Is_Not_Treated_As_A_Successful_Simulation()
    {
        // Arrange
        var catalog = new ModelCatalog();
        var tenant = CreateTenant();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => catalog.ResolveRequiredChatClient(tenant));

        // Assert
        Assert.Contains("configured", exception.Message);
    }

    [Fact]
    public void Explicitly_Simulated_Client_Is_Not_Used_By_Ask_AI()
    {
        // Arrange
        var tenant = CreateTenant();
        var catalog = new ModelCatalog(customClients: new Dictionary<string, IChatClient>
        {
            [tenant.DefaultModelId] = new SimulatedSliceChatClient(tenant.DefaultModelId)
        });

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => catalog.ResolveRequiredChatClient(tenant));

        // Assert
        Assert.Contains("non-simulated", exception.Message);
    }

    [Fact]
    public void Text_Only_Inference_Adapter_Is_Rejected_For_Tool_Calling()
    {
        // Arrange
        var catalog = new ModelCatalog(
        [
            new(CreateTenant().DefaultModelId, "Test", "Test", ModelProvider.FoundryInference,
                "test", "https://example.invalid")
        ]);

        // Act
        var exception = Assert.Throws<NotSupportedException>(() =>
            catalog.ResolveRequiredChatClient(CreateTenant()));

        // Assert
        Assert.Contains("tool calling", exception.Message);
    }

    [Fact]
    public async Task Reasoning_Cleanup_Preserves_Function_Calls_And_Response_Metadata()
    {
        // Arrange
        var call = new FunctionCallContent("call-one", "search_documents", new Dictionary<string, object?>());
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new TextContent("<think>Private reasoning</think>Searching."), call]))
        {
            ResponseId = "response-one",
            Usage = new UsageDetails { TotalTokenCount = 42 }
        };
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        using var cleanup = new ReasoningCleanupChatClient(client.Object, ownsInnerClient: false);

        // Act
        var result = await cleanup.GetResponseAsync([new ChatMessage(ChatRole.User, "Search")]);

        // Assert
        Assert.Equal("Searching.", result.Text);
        Assert.Same(call, result.Messages[0].Contents[1]);
        Assert.Equal("response-one", result.ResponseId);
        Assert.Equal(42, result.Usage?.TotalTokenCount);
    }

    [Fact]
    public void Disposing_A_Run_Client_Does_Not_Dispose_The_Injected_Shared_Client()
    {
        // Arrange
        var tenant = CreateTenant();
        var client = new Mock<IChatClient>();
        var catalog = new ModelCatalog(customClients: new Dictionary<string, IChatClient>
        {
            [tenant.DefaultModelId] = client.Object
        });

        // Act
        catalog.ResolveRequiredChatClient(tenant).Dispose();

        // Assert
        client.Verify(c => c.Dispose(), Times.Never);
    }

    [Fact]
    public async Task Provider_Failure_Is_Not_Replaced_With_Document_Lists()
    {
        // Arrange
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Model unavailable."));
        var orchestrator = CreateOrchestrator(client.Object);
        var run = CreateRun();

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => orchestrator.ExecuteRunAsync(run));

        // Assert
        Assert.Null(run.Answer);
        Assert.NotEqual(JobStatus.Completed, run.Status);
    }

    [Fact]
    public async Task Provider_Cancellation_Is_Propagated()
    {
        // Arrange
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var orchestrator = CreateOrchestrator(client.Object);
        var run = CreateRun();

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.ExecuteRunAsync(run));

        // Assert
        Assert.Null(run.Answer);
        Assert.Empty(run.Citations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"cosmosSql\":\"DELETE FROM c\"}")]
    public async Task Invalid_Model_Query_Does_Not_Fall_Back_To_All_Documents(string responseText)
    {
        // Arrange
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
        var orchestrator = CreateOrchestrator(client.Object);
        var run = CreateRun();

        // Act
        var exception = await Record.ExceptionAsync(() => orchestrator.ExecuteRunAsync(run));

        // Assert
        Assert.NotNull(exception);
        Assert.Null(run.Answer);
        Assert.Empty(run.ExecutedCosmosQueries);
    }

    [Fact]
    public async Task Disabled_Ask_AI_Does_Not_Call_The_Model()
    {
        // Arrange
        var tenant = CreateTenant();
        tenant.Capabilities[FeatureHarness.AskAiKey].Enabled = false;
        var client = new Mock<IChatClient>(MockBehavior.Strict);
        var orchestrator = CreateOrchestrator(client.Object, tenant);

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ExecuteRunAsync(CreateRun()));

        // Assert
        client.VerifyNoOtherCalls();
    }

    private static TenantHarness CreateTenant() => new()
    {
        TenantId = "tenant-test",
        DisplayName = "Test"
    };

    private static AskAiRun CreateRun() => new()
    {
        RunId = "test-run",
        TenantId = "tenant-test",
        Question = "What agreements are available?"
    };

    private static AskAiOrchestrator CreateOrchestrator(IChatClient client, TenantHarness? tenant = null)
    {
        tenant ??= CreateTenant();
        var store = new Mock<IHarnessStore>();
        store.Setup(s => s.GetTenantAsync(tenant.TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        var catalog = new ModelCatalog(customClients: new Dictionary<string, IChatClient>
        {
            [tenant.DefaultModelId] = client
        });

        return new AskAiOrchestrator(
            store.Object, new InMemoryJobStore(), catalog, [], NullLogger<AskAiOrchestrator>.Instance);
    }
}
