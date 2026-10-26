using System.Text.Json;
using DocIntel.Api.Diagnostics;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class AskAiDiagnosticTests
{
    [Theory]
    [InlineData("""{"kind":"DocumentAnswer","answer":"Unsupported claim.","evidenceIds":[]}""", "MissingEvidence")]
    [InlineData("""{"kind":"NoEvidence","answer":"No matches [1].","evidenceIds":[]}""", "CitationMismatch")]
    [InlineData("""{"kind":"Conversation","answer":"","evidenceIds":[]}""", "InvalidModelAnswer")]
    [InlineData("""{"kind":"DocumentAnswer","answer":"A document [1].","evidenceIds":["invented-id"]}""", "UnknownCitation")]
    public async Task Replay_Classifies_Failures_Without_Printing_Content_Or_Modifying_The_Saved_Run(
        string response, string code)
    {
        // Arrange
        var source = Source();
        var original = JsonSerializer.Serialize(source);
        var store = Store(source);
        var client = Client(response);
        var access = Access(DocumentAccessScope.Unrestricted);
        using var output = new StringWriter();

        // Act
        var result = await AskAiRunDiagnostic.ExecuteAsync(source.TenantId, source.RunId, true,
            store.Object, Orchestrator(client, access), output);

        // Assert
        Assert.Equal(1, result);
        Assert.Contains(code, output.ToString());
        Assert.DoesNotContain(source.Question, output.ToString());
        Assert.DoesNotContain("Unsupported claim", output.ToString());
        Assert.DoesNotContain(source.Actor!.ObjectId.ToString(), output.ToString());
        Assert.Equal(original, JsonSerializer.Serialize(source));
        store.Verify(s => s.GetRunAsync(source.TenantId, source.RunId, It.IsAny<CancellationToken>()), Times.Once);
        store.VerifyNoOtherCalls();
        access.Verify(s => s.ResolveAsync(source.Actor, source.TenantId, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Inspection_Does_Not_Call_The_Model_And_Only_Prints_Bounded_Metadata()
    {
        // Arrange
        var source = Source();
        source.Steps.Add(new AskAiStepTrace("private-stage", "private-query", 0, "private-description"));
        var store = Store(source);
        var client = new Mock<IChatClient>(MockBehavior.Strict);
        using var output = new StringWriter();

        // Act
        var result = await AskAiRunDiagnostic.ExecuteAsync(source.TenantId, source.RunId, false,
            store.Object, Orchestrator(client, Access(DocumentAccessScope.Unrestricted)), output);

        // Assert
        Assert.Equal(0, result);
        Assert.DoesNotContain("private-", output.ToString());
        Assert.DoesNotContain(source.Question, output.ToString());
        client.VerifyNoOtherCalls();
        store.Verify(s => s.GetRunAsync(source.TenantId, source.RunId, It.IsAny<CancellationToken>()), Times.Once);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_Revoked_Actor_Cannot_Use_Replay_To_Bypass_Current_Tenant_Access()
    {
        // Arrange
        var source = Source();
        var client = new Mock<IChatClient>(MockBehavior.Strict);
        using var output = new StringWriter();

        // Act
        var result = await AskAiRunDiagnostic.ExecuteAsync(source.TenantId, source.RunId, true,
            Store(source).Object, Orchestrator(client, Access(DocumentAccessScope.Denied)), output);

        // Assert
        Assert.Equal(1, result);
        Assert.Contains("AccessRevoked", output.ToString());
        client.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("actor")]
    [InlineData("channel")]
    [InlineData("callback")]
    [InlineData("extension")]
    public async Task Replay_Refuses_Unsafe_Or_Nonfailed_Sources(string issue)
    {
        // Arrange
        var source = Source();
        if (issue == "completed") source.Status = JobStatus.Completed;
        if (issue == "actor") source.Actor = null;
        if (issue is "channel" or "callback")
            source = new AskAiRun
            {
                RunId = source.RunId,
                TenantId = source.TenantId,
                Actor = source.Actor,
                Question = source.Question,
                Status = JobStatus.Failed,
                ReplyChannel = new AskAiReplyChannel
                {
                    ChannelType = issue == "channel" ? AskAiChannelType.Email : AskAiChannelType.Web,
                    CallbackWebhookUrl = issue == "callback" ? "https://untrusted.example/" : null
                }
            };
        var client = new Mock<IChatClient>(MockBehavior.Strict);
        var provider = new Mock<IAskAiToolProvider>(MockBehavior.Strict);
        using var output = new StringWriter();
        var orchestrator = Orchestrator(client, Access(DocumentAccessScope.Unrestricted),
            issue == "extension" ? [provider.Object] : []);

        // Act
        var result = await AskAiRunDiagnostic.ExecuteAsync(source.TenantId, source.RunId, true,
            Store(source).Object, orchestrator, output);

        // Assert
        Assert.Equal(2, result);
        Assert.Contains("Refused", output.ToString());
        client.VerifyNoOtherCalls();
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Successful_In_Memory_Replay_Does_Not_Publish_The_Answer_Or_Store_A_New_Run()
    {
        // Arrange
        var source = Source();
        var store = Store(source);
        using var output = new StringWriter();
        var client = Client("""{"kind":"Conversation","answer":"Private model answer.","evidenceIds":[]}""");

        // Act
        var result = await AskAiRunDiagnostic.ExecuteAsync(source.TenantId, source.RunId, true,
            store.Object, Orchestrator(client, Access(DocumentAccessScope.Unrestricted)), output);

        // Assert
        Assert.Equal(0, result);
        Assert.Contains("Completed", output.ToString());
        Assert.DoesNotContain("Private model answer", output.ToString());
        Assert.Equal(JobStatus.Failed, source.Status);
        store.Verify(s => s.GetRunAsync(source.TenantId, source.RunId, It.IsAny<CancellationToken>()), Times.Once);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public void Unknown_Provider_Errors_Never_Expose_Their_Message()
    {
        // Arrange
        var failure = new HttpRequestException("private-provider-details");

        // Act
        var code = AskAiFailureDiagnostics.GetCode(failure);
        var message = AskAiFailureDiagnostics.GetMessage(code);

        // Assert
        Assert.Equal("ExecutionFailed", code);
        Assert.DoesNotContain("private-provider-details", message);
    }

    private static AskAiRun Source() => new()
    {
        RunId = Guid.NewGuid().ToString("N"),
        TenantId = "tenant",
        Actor = new ActorIdentity(Guid.NewGuid(), Guid.NewGuid()),
        Question = "private-question",
        Status = JobStatus.Failed,
        ErrorCode = "ExecutionFailed"
    };

    private static Mock<IAskAiRunStore> Store(AskAiRun source)
    {
        var store = new Mock<IAskAiRunStore>(MockBehavior.Strict);
        store.Setup(s => s.GetRunAsync(source.TenantId, source.RunId, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        return store;
    }

    private static Mock<IChatClient> Client(string response)
    {
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        return client;
    }

    private static Mock<ITenantAccessResolver> Access(DocumentAccessScope scope)
    {
        var access = new Mock<ITenantAccessResolver>();
        access.Setup(a => a.ResolveAsync(It.IsAny<ActorIdentity?>(), "tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(scope);
        return access;
    }

    private static AskAiOrchestrator Orchestrator(Mock<IChatClient> client, Mock<ITenantAccessResolver> access,
        IEnumerable<IAskAiToolProvider>? providers = null)
    {
        var tenant = new TenantHarness { TenantId = "tenant", DisplayName = "Tenant" };
        var harness = new Mock<IHarnessStore>();
        harness.Setup(s => s.GetTenantAsync("tenant", It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        var catalog = new ModelCatalog(customClients: new Dictionary<string, IChatClient>
        {
            [tenant.DefaultModelId] = client.Object
        });
        return new AskAiOrchestrator(harness.Object, new InMemoryJobStore(), catalog, providers,
            NullLogger<AskAiOrchestrator>.Instance, access.Object);
    }
}
