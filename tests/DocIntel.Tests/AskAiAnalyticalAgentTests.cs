using System.Text.Json;
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

public sealed class AskAiAnalyticalAgentTests
{
    [Fact]
    public async Task A_Currency_Before_A_Repeated_Record_Header_Can_Be_Repaired_With_The_Exact_Source_Quote()
    {
        // Arrange
        const string text = "Billing currency: USD.\nInvoice INV-001; Supplier Contoso; Amount 100.00;\n" +
            "Repeated page header:\nInvoice INV-001; Supplier Contoso; Amount 100.00;";
        var jobs = new InMemoryJobStore();
        await jobs.UpsertJobAsync(new DocumentJob
        {
            JobId = "source", TenantId = "workspace", TenantDisplayName = "Workspace",
            FileName = "quote.pdf", Status = JobStatus.Completed,
            Segments = [new() { StartPage = 1, EndPage = 1, BodyText = text }]
        });
        var columns = new List<AskAiAnalysisInputColumn>
        {
            new() { Key = "supplier", Label = "Supplier", Kind = "text" },
            new() { Key = "amount", Label = "Amount", Kind = "number" },
            new() { Key = "invoice", Label = "Invoice ID", Kind = "text" }
        };
        AskAiAnalysisRequest Request(string quote) => new()
        {
            Title = "Supplier totals", Operation = "sum", Visualization = "bar",
            GroupBy = "supplier", ValueColumn = "amount", RecordIdColumn = "invoice", Columns = columns,
            Rows = [new() { EvidenceId = "e1", SourceQuote = quote, Unit = "USD",
                Cells = new() { ["supplier"] = "Contoso", ["amount"] = "100.00", ["invoice"] = "INV-001" } }]
        };
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<ChatMessage> _, ChatOptions options, CancellationToken token) =>
            {
                await InvokeAsync(options, "browse_documents", new()
                    { ["query"] = JsonSerializer.SerializeToElement(new DocumentQuery()) }, token);
                await InvokeAsync(options, "inspect_document_slice",
                    new() { ["jobId"] = "source", ["sliceIndex"] = 0, ["textOffset"] = 0 }, token);
                var failure = await InvokeAsync(options, "analyze_evidence",
                    new() { ["request"] = JsonSerializer.SerializeToElement(Request(String.Empty)) }, token);
                using var rejected = JsonSerializer.SerializeToDocument(failure);
                Assert.Equal("AnalysisUnit", rejected.RootElement.GetProperty("failureCode").GetString());
                var repair = rejected.RootElement.GetProperty("repair");
                Assert.Equal("unit", repair.GetProperty("columnKey").GetString());
                Assert.Equal(text, repair.GetProperty("sourceQuote").GetString());
                var calculated = await InvokeAsync(options, "analyze_evidence",
                    new() { ["request"] = JsonSerializer.SerializeToElement(Request(
                        repair.GetProperty("sourceQuote").GetString()!)) }, token);
                using var result = JsonSerializer.SerializeToDocument(calculated);
                Assert.Equal("a1", result.RootElement.GetProperty("analysisId").GetString());
                return Response("""
                    {"kind":"DocumentAnswer","answer":"The cited supplier total is shown below.",
                    "evidenceIds":["e1"],"analysisIds":["a1"]}
                    """);
            });
        var run = new AskAiRun { RunId = "one", TenantId = "workspace", Question = "Show supplier spend as a bar chart.",
            ReplyChannel = new AskAiReplyChannel { ChannelType = AskAiChannelType.Email } };

        // Act
        await Orchestrator(jobs, client.Object).ExecuteRunAsync(run);

        // Assert
        Assert.Equal(100m, Assert.Single(Assert.Single(run.Analyses).Rows).Cells["value"]);
        Assert.Contains("aria-label=\"Bar chart\"", run.EmailHtmlBody);
        Assert.Contains("100.00%", run.EmailHtmlBody);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Complex_Question_Produces_Server_Computed_Charts_And_Resolved_Row_Citations(bool singleCustomer)
    {
        // Arrange
        var fixture = AskAiAnalysisTests.Invoices("sum", "bar", "supplier",
            ("Contoso", "100.25", "USD", "2026-01-01"),
            ("Contoso", "50.50", "USD", "2026-01-02"),
            (singleCustomer ? "Contoso" : "Woodgrove", "300.00", "USD", "2026-01-03"));
        fixture.Request = new AskAiAnalysisRequest
        {
            Title = fixture.Request.Title, Operation = "sum", Visualization = "bar",
            GroupBy = "supplier", ValueColumn = "amount", RecordIdColumn = "invoice",
            Rows = fixture.Request.Rows,
            Columns = fixture.Request.Columns.Append(new AskAiAnalysisInputColumn
                { Key = "invoice", Label = "Invoice ID", Kind = "text" }).ToList()
        };
        for (var number = 0; number < fixture.Request.Rows.Count; number++)
        {
            var row = fixture.Request.Rows[number];
            row.Cells["invoice"] = "Invoice " + (number + 1);
            fixture.Request.Rows[number] = new()
            {
                EvidenceId = row.EvidenceId, Unit = row.Unit, Cells = row.Cells,
                SourceQuote = "The model rephrased this record instead of copying a paragraph."
            };
            fixture.Evidence[row.EvidenceId] = fixture.Evidence[row.EvidenceId] with
            {
                SourceText = fixture.Evidence[row.EvidenceId].SourceText + "\nRepeated page header:\n" +
                    fixture.Evidence[row.EvidenceId].SourceText
            };
        }
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
                Assert.Contains("Do not draw ASCII/block-character charts", options.Instructions);
                Assert.Contains("\"by customer\" means groupBy the customer column", options.Instructions);
                Assert.Contains("recordIdColumn=\"quote_id\"", options.Instructions);
                await InvokeAsync(options, "browse_documents", new()
                    { ["query"] = JsonSerializer.SerializeToElement(new DocumentQuery()) }, token);
                for (var number = 1; number <= 3; number++)
                    await InvokeAsync(options, "inspect_document_slice",
                        new() { ["jobId"] = $"source{number}", ["sliceIndex"] = 0, ["textOffset"] = 0 }, token);
                var exactAmount = fixture.Request.Rows[0].Cells["amount"];
                fixture.Request.Rows[0].Cells["amount"] = "999999.00";
                var rejected = await InvokeAsync(options, "analyze_evidence",
                    new() { ["request"] = JsonSerializer.SerializeToElement(fixture.Request) }, token);
                using (var failure = JsonSerializer.SerializeToDocument(rejected))
                {
                    Assert.Equal("AnalysisCell", failure.RootElement.GetProperty("failureCode").GetString());
                    var repair = failure.RootElement.GetProperty("repair");
                    Assert.Equal("e1", repair.GetProperty("evidenceId").GetString());
                    Assert.Equal("amount", repair.GetProperty("columnKey").GetString());
                    Assert.Contains("100.25", repair.GetProperty("inspectedSourceText").GetString());
                    Assert.DoesNotContain("999999.00", repair.GetProperty("sourceQuote").GetString());
                }
                fixture.Request.Rows[0].Cells["amount"] = exactAmount;
                var result = await InvokeAsync(options, "analyze_evidence",
                    new() { ["request"] = JsonSerializer.SerializeToElement(fixture.Request) }, token);
                using var computed = JsonSerializer.SerializeToDocument(result);
                Assert.Equal("a1", computed.RootElement.GetProperty("analysisId").GetString());
                return Response("""
                    {"kind":"DocumentAnswer","answer":"The inspected supplier totals are shown below.",
                    "evidenceIds":["e1","e2","e3"],"analysisIds":["a1"]}
                    """);
            });
        var run = new AskAiRun
        {
            RunId = "analysis", TenantId = "workspace", Question = "Compare supplier spend.",
            ReplyChannel = new AskAiReplyChannel { ChannelType = AskAiChannelType.Email }
        };

        // Act
        await Orchestrator(jobs, client.Object).ExecuteRunAsync(run);

        // Assert
        var analysis = Assert.Single(run.Analyses);
        Assert.Equal("bar", analysis.Visualization);
        Assert.Equal(singleCustomer ? 1 : 2, analysis.Rows.Count);
        Assert.Equal(singleCustomer ? 450.75m : 300m, analysis.Rows[0].Cells["value"]);
        if (!singleCustomer)
        {
            Assert.Equal(150.75m, analysis.Rows[1].Cells["value"]);
            Assert.Equal(new[] { 1, 2 }, analysis.Rows[1].CitationNumbers);
        }
        Assert.Equal(3, run.Citations.Count);
        Assert.Equal(3, run.SourceVersions.Count);
        Assert.Equal(3, analysis.Coverage.DocumentsInspected);
        Assert.Contains(singleCustomer ? "450.75" : "150.75", run.EmailHtmlBody);
        Assert.Contains("aria-label=\"Bar chart\"", run.EmailHtmlBody);
        Assert.Contains("<th scope=\"col\"", run.EmailHtmlBody);
        Assert.Contains(singleCustomer ? "100.00%" : "33.44%", run.EmailHtmlBody);
        Assert.DoesNotContain("Compare supplier spend.", run.EmailHtmlBody);
        Assert.DoesNotContain("Full tables and visualizations", run.EmailHtmlBody);
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
                return Response("""
                    {"kind":"DocumentAnswer","answer":"The source is available [1].",
                    "evidenceIds":["e1"],"analysisIds":[]}
                    """);
            });

        // Act
        await Orchestrator(fixture.Jobs, client.Object, fixture.Runs).ExecuteRunAsync(fixture.FollowUp);

        // Assert
        Assert.Equal("source", Assert.Single(fixture.FollowUp.Citations).JobId);
        Assert.Single(fixture.FollowUp.SourceVersions);
    }

    [Fact]
    public async Task Source_Changes_During_Reasoning_Discard_The_Answer_And_Analyses()
    {
        // Arrange
        var fixture = await AskAiConversationTests.FixtureAsync();
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<ChatMessage> _, ChatOptions options, CancellationToken token) =>
            {
                await InvokeAsync(options, "inspect_document_slice",
                    new() { ["jobId"] = "source", ["sliceIndex"] = 0, ["textOffset"] = 0 }, token);
                fixture.Source.Segments[0].BodyText = "Replaced source facts.";
                await fixture.Jobs.UpsertJobAsync(fixture.Source, token);
                return Response("""{"kind":"DocumentAnswer","answer":"Old source [1].","evidenceIds":["e1"],"analysisIds":[]}""");
            });

        // Act / Assert
        await Assert.ThrowsAsync<AskAiSourceChangedException>(() =>
            Orchestrator(fixture.Jobs, client.Object, fixture.Runs).ExecuteRunAsync(fixture.FollowUp));
        Assert.Null(fixture.FollowUp.Answer);
        Assert.Empty(fixture.FollowUp.Analyses);
        Assert.Empty(fixture.FollowUp.Citations);
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
