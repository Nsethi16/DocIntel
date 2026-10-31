using System.Text.Json;
using System.Xml.Linq;
using DocIntel.Core.Features.DocumentInsight;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class DocumentInsightTests
{
    [Theory]
    [InlineData(true, false, 3)]
    [InlineData(false, true, 3)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1)]
    public async Task Workflow_Generates_One_Complete_File_Insight_When_Identification_Is_Unavailable_Or_Incomplete(
        bool splitEnabled, bool identifyEnabled, int identifiedCount)
    {
        // Arrange
        var tenant = Tenant(splitEnabled, identifyEnabled);
        var slices = Enumerable.Range(1, 3).Select(page => new DocumentSlice
        {
            SegmentIndex = page - 1, StartPage = page, EndPage = page,
            IdentifiedDocumentName = page <= identifiedCount ? "Invoice" : "Undefined",
            BodyText = $"PAGE_{page}_CONTENT", InsightSummary = $"Old page {page} summary"
        }).ToList();
        var state = State(tenant, slices, 3);
        var fixture = Model();

        // Act
        await new GenerateDocumentInsightsExecutor(fixture.Catalog)
            .HandleAsync(state, Mock.Of<IWorkflowContext>());

        // Assert
        Assert.Single(fixture.Inputs);
        Assert.Equal(String.Join("\n\n",
            slices.Select(slice => slice.BodyText)), Unwrap(fixture.Inputs[0]));
        Assert.Equal("Summary 1", state.PacketSummary);
        Assert.All(state.Slices, slice => Assert.Null(slice.InsightSummary));
        Assert.Contains("complete uploaded file", Assert.Single(fixture.Instructions));
        Assert.Contains("Do not generate page-by-page summaries", fixture.Instructions[0]);
        Assert.Contains("one complete-file insight", state.ExecutionTrace.Single().Notes);
    }

    [Fact]
    public async Task Workflow_Summarizes_All_Pages_Of_Each_Identified_Document_Without_Merging_The_Same_Type()
    {
        // Arrange
        var slices = Documents();
        var state = State(Tenant(), slices, 4);
        var fixture = Model();

        // Act
        await new GenerateDocumentInsightsExecutor(fixture.Catalog)
            .HandleAsync(state, Mock.Of<IWorkflowContext>());

        // Assert
        Assert.Equal(2, fixture.Inputs.Count);
        Assert.Equal("FIRST_PAGE_1\nFIRST_PAGE_2", Unwrap(fixture.Inputs[0]));
        Assert.Equal("SECOND_PAGE_3\nSECOND_PAGE_4", Unwrap(fixture.Inputs[1]));
        Assert.Equal("Summary 1", slices[0].InsightSummary);
        Assert.Equal("Summary 2", slices[1].InsightSummary);
        Assert.Contains("Document 1: Invoice (Pages 1-2)", state.PacketSummary);
        Assert.Contains("Document 2: Invoice (Pages 3-4)", state.PacketSummary);
        Assert.All(fixture.Instructions, instructions => Assert.Contains("one complete identified document", instructions));
    }

    [Fact]
    public async Task Reevaluation_Uses_One_File_Summary_And_Removes_Obsolete_Page_Summaries()
    {
        // Arrange
        var slices = Documents();
        var job = Job(slices);
        var fixture = Model();
        slices.Reverse();

        // Act
        await GenerateDocumentInsightsExecutor.EvaluateJobInsightsAsync(job, Tenant(identify: false), fixture.Catalog);

        // Assert
        Assert.Single(fixture.Inputs);
        Assert.Equal("FIRST_PAGE_1\nFIRST_PAGE_2\n\n" +
            "SECOND_PAGE_3\nSECOND_PAGE_4", Unwrap(fixture.Inputs[0]));
        Assert.Equal("Summary 1", job.PacketSummary);
        Assert.All(job.Segments, slice => Assert.Null(slice.InsightSummary));
    }

    [Fact]
    public async Task Reevaluation_Uses_The_Complete_Translated_Document_Content_When_Available()
    {
        // Arrange
        var slices = Documents();
        slices[0].TranslatedText = "TRANSLATED_FIRST_PAGE_1\nTRANSLATED_FIRST_PAGE_2";
        slices[0].WasTranslated = true;
        var job = Job(slices);
        var fixture = Model();

        // Act
        await GenerateDocumentInsightsExecutor.EvaluateJobInsightsAsync(job, Tenant(), fixture.Catalog);

        // Assert
        Assert.Equal(2, fixture.Inputs.Count);
        Assert.Equal(slices[0].TranslatedText, Unwrap(fixture.Inputs[0]));
        Assert.Equal(slices[1].BodyText, Unwrap(fixture.Inputs[1]));
        Assert.Equal("Summary 1", slices[0].InsightSummary);
        Assert.Equal("Summary 2", slices[1].InsightSummary);
    }

    [Fact]
    public async Task Reevaluation_Ignores_A_Legacy_Same_Language_Placeholder_And_Keeps_Complete_Markdown()
    {
        // Arrange
        var slices = Documents();
        slices[0].Markdown = "# Invoice\n| Item | Amount |\n| Final obligation | USD 1234 |";
        slices[0].TranslatedText = slices[0].BodyText;
        slices[0].WasTranslated = false;
        var job = Job(slices);
        var fixture = Model();

        // Act
        await GenerateDocumentInsightsExecutor.EvaluateJobInsightsAsync(job, Tenant(), fixture.Catalog);

        // Assert
        Assert.Equal(2, fixture.Inputs.Count);
        Assert.Equal(slices[0].Markdown, Unwrap(fixture.Inputs[0]));
        Assert.Contains("USD 1234", Unwrap(fixture.Inputs[0]));
        Assert.Equal(slices[1].BodyText, Unwrap(fixture.Inputs[1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_Generation_Preserves_All_Previous_Summaries(bool reevaluation)
    {
        // Arrange
        var slices = Documents();
        var tenant = Tenant();
        var fixture = Model(number => number == 1 ? """{"summary":"New first summary"}""" : "{}");
        var state = State(tenant, slices, 4);
        state.PacketSummary = "Original file summary";
        var job = Job(slices);

        // Act
        var action = reevaluation
            ? GenerateDocumentInsightsExecutor.EvaluateJobInsightsAsync(job, tenant, fixture.Catalog)
            : new GenerateDocumentInsightsExecutor(fixture.Catalog)
                .HandleAsync(state, Mock.Of<IWorkflowContext>()).AsTask();

        // Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => action);
        Assert.Equal("Original file summary", state.PacketSummary);
        Assert.Equal("Original file summary", job.PacketSummary);
        Assert.Equal("Old first summary", slices[0].InsightSummary);
        Assert.Equal("Old second summary", slices[1].InsightSummary);
    }

    [Fact]
    public async Task Cancellation_Does_Not_Publish_A_Partial_Document_Batch()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var slices = Documents();
        var job = Job(slices);
        var fixture = Model(afterRequest: _ => cancellation.Cancel());

        // Act
        var action = GenerateDocumentInsightsExecutor.EvaluateJobInsightsAsync(
            job, Tenant(), fixture.Catalog, cancellation.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        Assert.Single(fixture.Inputs);
        Assert.Equal("Original file summary", job.PacketSummary);
        Assert.Equal("Old first summary", slices[0].InsightSummary);
        Assert.Equal("Old second summary", slices[1].InsightSummary);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("overlap")]
    [InlineData("missing")]
    public async Task Incomplete_Or_Overlapping_File_Coverage_Is_Rejected_Before_Using_The_Model(string problem)
    {
        // Arrange
        var slices = Documents();
        if (problem == "gap") slices[1].StartPage = 4;
        if (problem == "overlap") slices[1].StartPage = 2;
        var job = Job(slices);
        if (problem == "missing") job.TotalPages = 5;
        var fixture = Model();

        // Act
        var action = GenerateDocumentInsightsExecutor.EvaluateJobInsightsAsync(
            job, Tenant(identify: false), fixture.Catalog);

        // Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => action);
        Assert.Empty(fixture.Inputs);
        Assert.Equal("Original file summary", job.PacketSummary);
    }

    [Fact]
    public async Task Disabling_Insights_Clears_Both_File_And_Document_Summaries_Without_A_Model_Request()
    {
        // Arrange
        var tenant = Tenant();
        tenant.Capabilities[FeatureHarness.DocumentInsightKey].Enabled = false;
        var job = Job(Documents());
        var fixture = Model();

        // Act
        await GenerateDocumentInsightsExecutor.EvaluateJobInsightsAsync(job, tenant, fixture.Catalog);

        // Assert
        Assert.Null(job.PacketSummary);
        Assert.All(job.Segments, slice => Assert.Null(slice.InsightSummary));
        Assert.Empty(fixture.Inputs);
    }

    private static TenantHarness Tenant(bool split = true, bool identify = true) => new()
    {
        TenantId = "tenant", DisplayName = "Workspace", DefaultModelId = "phi-4",
        AllowedModels = ["phi-4"],
        Capabilities = new Dictionary<string, FeatureHarness>
        {
            [FeatureHarness.SplitAndGroupKey] = new() { Enabled = split },
            [FeatureHarness.IdentifyDocumentsKey] = new() { Enabled = identify },
            [FeatureHarness.DocumentInsightKey] = new()
            {
                Enabled = true, ModelIdOverride = "phi-4",
                DocumentInsight = new() { SummaryInstructions = "Focus on renewal and obligations." }
            }
        }
    };

    private static List<DocumentSlice> Documents() =>
    [
        new()
        {
            SegmentIndex = 0, StartPage = 1, EndPage = 2, IdentifiedDocumentName = "Invoice",
            BodyText = "FIRST_PAGE_1\nFIRST_PAGE_2", InsightSummary = "Old first summary"
        },
        new()
        {
            SegmentIndex = 1, StartPage = 3, EndPage = 4, IdentifiedDocumentName = "Invoice",
            BodyText = "SECOND_PAGE_3\nSECOND_PAGE_4", InsightSummary = "Old second summary"
        }
    ];

    private static SplittingState State(TenantHarness tenant, List<DocumentSlice> slices, int pages) => new()
    {
        Tenant = tenant, FileName = "bundle.pdf", Slices = slices,
        Pages = Enumerable.Range(1, pages).Select(page => new PageLayout { PageNumber = page }).ToList()
    };

    private static DocumentJob Job(List<DocumentSlice> slices) => new()
    {
        JobId = "one", TenantId = "tenant", TenantDisplayName = "Workspace", AppliedHarnessVersion = 1,
        FileName = "bundle.pdf", Status = JobStatus.Completed, TotalPages = 4,
        Segments = slices, PacketSummary = "Original file summary"
    };

    private static string Unwrap(string text)
    {
        const string closing = "</untrusted_document_content>";
        return XElement.Parse(text[..(text.IndexOf(closing, StringComparison.Ordinal) + closing.Length)])
            .Value.Trim('\r', '\n');
    }

    private static (ModelCatalog Catalog, List<string> Inputs, List<string> Instructions) Model(
        Func<int, string>? response = null, Action<int>? afterRequest = null)
    {
        var inputs = new List<string>();
        var instructions = new List<string>();
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> messages, ChatOptions options, CancellationToken _) =>
            {
                inputs.Add(messages.Last(message => message.Role == ChatRole.User).Text!);
                instructions.Add(options.Instructions ?? String.Empty);
                afterRequest?.Invoke(inputs.Count);
                var json = response?.Invoke(inputs.Count) ??
                    JsonSerializer.Serialize(new { summary = $"Summary {inputs.Count}" });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
            });
        return (new ModelCatalog(customClients:
            new Dictionary<string, IChatClient> { ["phi-4"] = client.Object }), inputs, instructions);
    }
}
