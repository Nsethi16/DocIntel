using System.Text.Json;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class AskAiAnalyticalAgentTests
{
    [Fact]
    public async Task A_Complex_Question_Produces_Server_Computed_Charts_And_Resolved_Row_Citations()
    {
        // Arrange
        var fixture = AskAiAnalysisTests.Invoices("sum", "bar", "supplier",
            ("Contoso", "100.25", "USD", "2026-01-01"),
            ("Contoso", "50.50", "USD", "2026-01-02"),
            ("Woodgrove", "300.00", "USD", "2026-01-03"));
        var jobs = new InMemoryJobStore();
        var index = 0;
        foreach (var source in fixture.Evidence.Values)
        {
            await jobs.UpsertJobAsync(new DocumentJob
            {
                JobId = $"source{++index}", TenantId = "workspace", TenantDisplayName = "Workspace",
                AppliedHarnessVersion = 1, FileName = $"invoice-{index}.pdf", Status = JobStatus.Completed,
                Segments = [new() { StartPage = 1, EndPage = 1, BodyText = source.SourceText }]
            });
        }
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<ChatMessage> _, ChatOptions options, CancellationToken token) =>
            {
                await InvokeAsync(options, "browse_documents", new()
                    { ["query"] = JsonSerializer.SerializeToElement(new DocumentQuery()) }, token);
                for (var number = 1; number <= 3; number++)
                    await InvokeAsync(options, "inspect_document_slice",
                        new() { ["jobId"] = $"source{number}", ["sliceIndex"] = 0, ["textOffset"] = 0 }, token);
                var result = await InvokeAsync(options, "analyze_evidence",
                    new() { ["request"] = JsonSerializer.SerializeToElement(fixture.Request) }, token);
                var computed = JsonSerializer.SerializeToDocument(result);
                Assert.Equal("a1", computed.RootElement.GetProperty("analysisId").GetString());
                return Response("""{"kind":"DocumentAnswer","answer":"Woodgrove has the higher inspected spend.",
                    "evidenceIds":["e1","e2","e3"],"analysisIds":["a1"]}""");
            });
        var run = new AskAiRun { RunId = "analysis", TenantId = "workspace", Question = "Compare supplier spend." };

        // Act
        await Orchestrator(jobs, client.Object).ExecuteRunAsync(run);

        // Assert
        var analysis = Assert.Single(run.Analyses);
        Assert.Equal("bar", analysis.Visualization);
        Assert.Equal(300m, analysis.Rows[0].Cells["value"]);
        Assert.Equal(150.75m, analysis.Rows[1].Cells["value"]);
        Assert.Equal(new[] { 1, 2 }, analysis.Rows[1].CitationNumbers);
        Assert.Equal(3, run.Citations.Count);
        Assert.Equal(3, run.SourceVersions.Count);
        Assert.Equal(3, analysis.Coverage.DocumentsInspected);
        Assert.Contains("150.75", run.EmailHtmlBody);
        Assert.Contains("Full tables", run.TeamsAdaptiveCardJson);
        Assert.Contains(run.Steps, step => step.StepName == "analyze_evidence");
        Assert.Equal(JobStatus.Completed, run.Status);
    }

    [Fact]
    public async Task Invented_Analytical_Results_Are_Rejected_Without_Publishing_Partial_Output()
    {
        // Arrange
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response("""{"kind":"DocumentAnswer","answer":"Fake chart.","evidenceIds":[],"analysisIds":["a99"]}"""));
        var run = new AskAiRun { RunId = "analysis", TenantId = "workspace", Question = "Invent a chart." };

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Orchestrator(new InMemoryJobStore(), client.Object).ExecuteRunAsync(run));
        Assert.Null(run.Answer);
        Assert.Empty(run.Analyses);
        Assert.Empty(run.Citations);
    }

    [Fact]
    public async Task Follow_Ups_Reinspect_Authorized_Sources_Rather_Than_Reusing_Old_Citation_Ids()
    {
        // Arrange
        var fixture = await AskAiConversationTests.FixtureAsync();
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<ChatMessage> messages, ChatOptions options, CancellationToken token) =>
            {
                Assert.Contains(messages, message => message.Text?.Contains("previousQuestion") == true);
                await InvokeAsync(options, "inspect_document_slice",
                    new() { ["jobId"] = "source", ["sliceIndex"] = 0, ["textOffset"] = 0 }, token);
                return Response("""{"kind":"DocumentAnswer","answer":"The source is available [1].",
                    "evidenceIds":["e1"],"analysisIds":[]}""");
            });

        // Act
        await Orchestrator(fixture.Jobs, client.Object, fixture.Runs).ExecuteRunAsync(fixture.FollowUp);

        // Assert
        Assert.Equal("source", Assert.Single(fixture.FollowUp.Citations).JobId);
        Assert.Single(fixture.FollowUp.SourceVersions);
    }

    private static async Task<object?> InvokeAsync(
        ChatOptions options, string name, AIFunctionArguments arguments, CancellationToken token)
    {
        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(options.Tools!, item => item.Name == name));
        return await tool.InvokeAsync(arguments, token);
    }

    private static ChatResponse Response(string json) => new(new ChatMessage(ChatRole.Assistant, json));

    private static AskAiOrchestrator Orchestrator(IJobStore jobs, IChatClient client, IAskAiRunStore? runs = null)
    {
        var tenant = new TenantHarness { TenantId = "workspace", DisplayName = "Workspace" };
        var harness = new Mock<IHarnessStore>();
        harness.Setup(store => store.GetTenantAsync("workspace", It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        var catalog = new ModelCatalog(customClients:
            new Dictionary<string, IChatClient> { [tenant.DefaultModelId] = client });
        return new(harness.Object, jobs, catalog, [], NullLogger<AskAiOrchestrator>.Instance, runStore: runs);
    }
}
