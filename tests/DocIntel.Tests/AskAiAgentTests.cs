using System.Text.Json;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

using DocIntel.Core.Evidence;
using DocIntel.Core.Review;

namespace DocIntel.Tests;

public sealed class AskAiAgentTests
{
    [Fact]
    public async Task Greetings_Are_Answered_By_The_Model_Without_Reading_Documents()
    {
        // Arrange
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        var orchestrator = CreateOrchestrator(store.Object,
            Final("Conversation", "Hello! What would you like to know about your documents?"));

        // Act
        var run = await orchestrator.ExecuteRunAsync(CreateRun("Hi"));

        // Assert
        Assert.StartsWith("Hello!", run.Answer);
        Assert.Empty(run.Citations);
        Assert.Empty(run.ExecutedCosmosQueries);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Empty_Search_Does_Not_Fall_Back_To_Unrelated_Documents()
    {
        // Arrange
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        store.Setup(s => s.SearchDocumentsAsync("tenant-one", It.IsAny<DocumentQuery>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentQueryPage([], null));
        var orchestrator = CreateOrchestrator(store.Object,
            SearchCall(), Final("NoEvidence", "I couldn't find supporting documents for that question."));

        // Act
        var run = await orchestrator.ExecuteRunAsync(CreateRun());

        // Assert
        Assert.Contains("couldn't find", run.Answer);
        Assert.Empty(run.Citations);
        store.Verify(s => s.SearchDocumentsAsync("tenant-one",
            It.IsAny<DocumentQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_Document_Removed_After_Search_Cannot_Contribute_To_An_Uncited_Final_Answer()
    {
        // Arrange
        var store = new Mock<IJobStore>();
        store.Setup(s => s.SearchDocumentsAsync("tenant-one", It.IsAny<DocumentQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentQueryPage([new DocumentReference("deleted", "file.pdf", 1, 1)], null));
        var orchestrator = CreateOrchestrator(store.Object,
            SearchCall(), Final("NoEvidence", "An uncited conclusion based on the search."));

        // Act / Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => orchestrator.ExecuteRunAsync(CreateRun()));
    }

    [Theory]
    [InlineData("Missing [1].", "invented-source")]
    [InlineData("Wrong marker [2].", "e1")]
    [InlineData("Missing marker.", "e1")]
    public async Task Fabricated_Or_Mismatched_Citations_Fail_Explicitly(string answer, string evidenceId)
    {
        // Arrange
        var store = await CreateDocumentStoreAsync();
        var orchestrator = CreateOrchestrator(store,
            SearchCall(), InspectCall(), Final("DocumentAnswer", answer, evidenceId));

        // Act
        var exception = await Record.ExceptionAsync(() => orchestrator.ExecuteRunAsync(CreateRun()));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task Document_Answers_Require_At_Least_One_Resolved_Citation()
    {
        // Arrange
        var orchestrator = CreateOrchestrator(new InMemoryJobStore(),
            Final("DocumentAnswer", "A contract exists."));

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.ExecuteRunAsync(CreateRun()));

        // Assert
        Assert.Contains("require evidence", exception.Message);
    }

    [Fact]
    public async Task Citations_Use_Actual_Source_Text_Not_A_Derived_Summary()
    {
        // Arrange
        var store = await CreateDocumentStoreAsync();
        var orchestrator = CreateOrchestrator(store,
            SearchCall(), InspectCall(), Final("DocumentAnswer", "The source names Woodgrove [1].", "e1"));

        // Act
        var run = await orchestrator.ExecuteRunAsync(CreateRun());

        // Assert
        var citation = Assert.Single(run.Citations);
        Assert.Equal("Original Woodgrove agreement text.", citation.Excerpt);
        Assert.Equal("p.2-3", citation.PageRange);
        Assert.Equal("source.pdf", citation.FileName);
        Assert.Contains("tenant-one", citation.PdfUrl);
    }

    [Fact]
    public async Task Heavy_Field_Geometry_Does_Not_Exhaust_The_Ask_Ai_Tool_Payload()
    {
        // Arrange
        var store = await CreateDocumentStoreAsync();
        var job = (await store.GetJobAsync("source", tenantId: "tenant-one"))!;
        var boxes = Enumerable.Range(0, 64)
            .Select(_ => new BoundingBox(2, [0.1, 0.1, 0.2, 0.1, 0.2, 0.2, 0.1, 0.2])).ToArray();
        var provenance = new ValueProvenance(GroundingStatus.ExactTextMatch,
            Enumerable.Range(0, 64).Select(index => $"w_2_{index}").ToArray(), boxes, "Source");
        job.Segments[0].ExtractedEntities = Enumerable.Range(0, 15).Select(index =>
            new ExtractedEntity("Field" + index, "Value" + index, "Party", 0.9, 0, provenance,
                new ConfidenceSignals(0.9, 1))).ToList();
        Assert.True(JsonSerializer.Serialize(job.Segments[0].ExtractedEntities).Length > 32_000);
        var orchestrator = CreateOrchestrator(store,
            SearchCall(), InspectCall(), Final("DocumentAnswer", "The source is available [1].", "e1"));

        // Act
        var run = await orchestrator.ExecuteRunAsync(CreateRun());

        // Assert
        Assert.Equal(JobStatus.Completed, run.Status);
        Assert.Single(run.Citations);
    }

    [Fact]
    public async Task Documents_Not_Found_By_Search_Cannot_Be_Inspected()
    {
        // Arrange
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        var orchestrator = CreateOrchestrator(store.Object,
            InspectCall(), Final("Conversation", "Hello."));

        // Act
        await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.ExecuteRunAsync(CreateRun()));

        // Assert
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Retrieval_Failure_Cannot_Be_Turned_Into_A_Completed_Answer()
    {
        // Arrange
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        store.Setup(s => s.SearchDocumentsAsync("tenant-one", It.IsAny<DocumentQuery>(),
            It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("Storage unavailable."));
        var orchestrator = CreateOrchestrator(store.Object,
            SearchCall(), Final("NoEvidence", "No documents."));
        var run = CreateRun();

        // Act
        await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.ExecuteRunAsync(run));

        // Assert
        Assert.Null(run.Answer);
        Assert.NotEqual(JobStatus.Completed, run.Status);
    }

    [Fact]
    public async Task Cancellation_During_Retrieval_Does_Not_Produce_A_Completed_Answer()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        store.Setup(s => s.SearchDocumentsAsync("tenant-one", It.IsAny<DocumentQuery>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, DocumentQuery _, CancellationToken ct) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<DocumentQueryPage>(ct);
            });
        var orchestrator = CreateOrchestrator(store.Object,
            SearchCall(), Final("Conversation", "Finished."));
        var run = CreateRun();

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.ExecuteRunAsync(run, cancellationToken: cancellation.Token));

        // Assert
        Assert.Null(run.Answer);
        Assert.NotEqual(JobStatus.Completed, run.Status);
    }

    [Fact]
    public async Task A_Mismatched_Tenant_Returned_By_Storage_Is_Not_Exposed()
    {
        // Arrange
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        store.Setup(s => s.SearchDocumentsAsync("tenant-one", It.IsAny<DocumentQuery>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(
                new DocumentQueryPage([new DocumentReference("source", "private.pdf", 1, 1)], null));
        store.Setup(s => s.GetJobAsync("source", It.IsAny<CancellationToken>(), "tenant-one"))
            .ReturnsAsync(new DocumentJob
            {
                JobId = "source",
                TenantId = "other-tenant",
                FileName = "private.pdf",
                TenantDisplayName = "Other",
                AppliedHarnessVersion = 1,
                Status = JobStatus.Completed
            });
        var orchestrator = CreateOrchestrator(store.Object,
            SearchCall(), InspectCall(), Final("DocumentAnswer", "Private [1].", "e1"));
        var run = CreateRun();

        // Act
        await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.ExecuteRunAsync(run));

        // Assert
        Assert.Null(run.Answer);
        Assert.Empty(run.Citations);
    }

    [Fact]
    public async Task Permission_Revocation_Is_Checked_Again_Before_The_Final_Answer_Is_Returned()
    {
        // Arrange
        var store = await CreateDocumentStoreAsync();
        var resolver = new Mock<ITenantAccessResolver>();
        var grant = new DocumentAccessScope(TenantRole.Reader, new HashSet<string> { "source" });
        resolver.SetupSequence(r => r.ResolveAsync(
                It.IsAny<ActorIdentity?>(), "tenant-one", It.IsAny<CancellationToken>()))
            .ReturnsAsync(grant).ReturnsAsync(grant).ReturnsAsync(grant)
            .ReturnsAsync(DocumentAccessScope.Denied);
        var orchestrator = CreateOrchestrator(store, [],
            [SearchCall(), InspectCall(), Final("DocumentAnswer", "A source answer [1].", "e1")], resolver.Object);
        var run = CreateRun();

        // Act
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => orchestrator.ExecuteRunAsync(run));

        // Assert
        Assert.Null(run.Answer);
        Assert.Empty(run.Citations);
    }

    [Fact]
    public async Task Extension_Tools_Share_The_Run_Tool_Call_Budget()
    {
        // Arrange
        var calls = 0;
        var extension = new Mock<IAskAiToolProvider>();
        extension.Setup(p => p.GetTools(It.IsAny<TenantHarness>()))
            .Returns([AIFunctionFactory.Create(() => ++calls, "count_calls")]);
        var toolCalls = Enumerable.Range(1, 65).Select(i => (AIContent)new FunctionCallContent(
            $"call-{i}", "count_calls", new Dictionary<string, object?>())).ToList();
        var orchestrator = CreateOrchestrator(new InMemoryJobStore(), [extension.Object],
            [new ChatResponse(new ChatMessage(ChatRole.Assistant, toolCalls)),
             Final("Conversation", "Finished.")]);
        var run = CreateRun();

        // Act
        await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.ExecuteRunAsync(run));

        // Assert
        Assert.Equal(64, calls);
        Assert.Null(run.Answer);
    }

    [Fact]
    public async Task Arbitrary_Callback_Urls_Are_Not_Automatically_Called()
    {
        // Arrange
        var run = new AskAiRun
        {
            RunId = "one",
            TenantId = "tenant-one",
            Question = "Hi",
            ReplyChannel = new AskAiReplyChannel { CallbackWebhookUrl = "http://localhost/callback" }
        };
        var orchestrator = CreateOrchestrator(new InMemoryJobStore());

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.ExecuteRunAsync(run));

        // Assert
        Assert.Contains("Callback delivery is not enabled", exception.Message);
    }

    [Fact]
    public async Task Human_Review_Is_Not_An_Advertised_Model_Tool()
    {
        // Arrange
        var tenant = new TenantHarness { TenantId = "tenant-one", DisplayName = "Test" };
        var harness = new Mock<IHarnessStore>();
        harness.Setup(s => s.GetTenantAsync("tenant-one", It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        ChatOptions? captured = null;
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((_, options, _) => captured = options)
            .ReturnsAsync(Final("Conversation", "Hello"));
        var catalog = new ModelCatalog(customClients: new Dictionary<string, IChatClient> { [tenant.DefaultModelId] = client.Object });
        var orchestrator = new AskAiOrchestrator(harness.Object, new InMemoryJobStore(), catalog, [],
            NullLogger<AskAiOrchestrator>.Instance);

        // Act
        await orchestrator.ExecuteRunAsync(CreateRun("Hi"));

        // Assert
        Assert.NotNull(captured);
        Assert.DoesNotContain(captured.Tools!, tool => tool.Name == "inspect_human_review");
        Assert.Contains(captured.Tools!, tool => tool.Name == "inspect_document_slice");
    }

    [Fact]
    public async Task Search_Tool_Advertises_The_Exact_Server_Bounds_Without_Model_Control_Of_Access()
    {
        // Arrange
        var tenant = new TenantHarness { TenantId = "tenant-one", DisplayName = "Test" };
        var harness = new Mock<IHarnessStore>();
        harness.Setup(s => s.GetTenantAsync(tenant.TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        ChatOptions? captured = null;
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((_, options, _) => captured = options)
            .ReturnsAsync(Final("Conversation", "Hello"));
        var catalog = new ModelCatalog(customClients: new Dictionary<string, IChatClient>
        {
            [tenant.DefaultModelId] = client.Object
        });
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        var orchestrator = new AskAiOrchestrator(harness.Object, store.Object, catalog, [],
            NullLogger<AskAiOrchestrator>.Instance);

        // Act
        await orchestrator.ExecuteRunAsync(CreateRun("Hi"));

        // Assert
        var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(captured!.Tools!,
            tool => tool.Name == "search_documents"));
        var schema = function.JsonSchema;
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var query = schema.GetProperty("properties").GetProperty("query");
        Assert.False(query.GetProperty("additionalProperties").GetBoolean());
        var properties = query.GetProperty("properties");
        var fields = properties.EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.Equal(new[] { "continuationToken", "documentType", "entities", "pageSize", "searchText" }, fields);
        var required = query.GetProperty("required").EnumerateArray()
            .Select(value => Assert.IsType<string>(value.GetString())).Order().ToArray();
        Assert.Equal(fields, required);
        var sizes = properties.GetProperty("pageSize").GetProperty("enum").EnumerateArray()
            .Select(value => value.GetInt32()).ToArray();
        Assert.Equal(Enumerable.Range(1, 20).ToArray(), sizes);
        foreach (var size in sizes) new DocumentQuery { PageSize = size }.Validate();
        Assert.Equal("array", properties.GetProperty("entities").GetProperty("type").GetString());
        Assert.Contains("never null", properties.GetProperty("entities").GetProperty("description").GetString());
        Assert.False(properties.GetProperty("entities").GetProperty("items")
            .GetProperty("additionalProperties").GetBoolean());
        Assert.DoesNotContain("authorizedJobIds", schema.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenantId", schema.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"sql\"", schema.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pageSize must be 1 through 20", captured.Instructions);
        Assert.Contains("entities=[]", captured.Instructions);
        Assert.Contains("normally starting with sliceIndex=0 and textOffset=0", captured.Instructions);
        Assert.Contains("A jobId identifies a document; it is NEVER an evidenceId", captured.Instructions);
        Assert.Contains("not citation evidence", function.Description);
        var answer = Assert.IsType<ChatResponseFormatJson>(captured.ResponseFormat).Schema!.Value;
        Assert.False(answer.GetProperty("additionalProperties").GetBoolean());
        var answerProperties = answer.GetProperty("properties");
        Assert.Equal("string", answerProperties.GetProperty("kind").GetProperty("type").GetString());
        Assert.Equal(new[] { "Conversation", "Clarification", "NoEvidence", "DocumentAnswer" },
            answerProperties.GetProperty("kind").GetProperty("enum").EnumerateArray()
                .Select(value => Assert.IsType<string>(value.GetString())).ToArray());
        Assert.Equal("array", answerProperties.GetProperty("evidenceIds").GetProperty("type").GetString());
        Assert.Contains("Never jobId values", answerProperties.GetProperty("evidenceIds")
            .GetProperty("description").GetString());
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_Job_Id_From_Search_Cannot_Be_Used_As_A_Citation_Without_Inspection()
    {
        // Arrange
        var store = await CreateDocumentStoreAsync();
        var orchestrator = CreateOrchestrator(store,
            SearchCall(), Final("DocumentAnswer", "A matching document [1].", "source"));
        var run = CreateRun("List matching documents");

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ExecuteRunAsync(run));

        // Assert
        Assert.Equal("UnknownCitation", AskAiFailureDiagnostics.GetCode(failure));
        Assert.Single(run.Steps, step => step.StepName == "search_documents");
        Assert.DoesNotContain(run.Steps, step => step.StepName == "inspect_document_slice");
        Assert.Null(run.Answer);
        Assert.Empty(run.Citations);
    }

    [Theory]
    [InlineData("""{"searchText":"Sample customer","entities":[],"pageSize":0}""")]
    [InlineData("""{"searchText":"Sample customer","entities":[],"pageSize":100}""")]
    [InlineData("""{"searchText":"Sample customer","entities":null,"pageSize":10}""")]
    [InlineData("""{"entities":[{"name":"","value":"Sample customer"}],"pageSize":10}""")]
    public async Task Invalid_Model_Search_Arguments_Still_Fail_Before_Reading_Any_Documents(string query)
    {
        // Arrange
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        var call = ToolCall("search_documents", new Dictionary<string, object?>
        {
            ["query"] = JsonSerializer.Deserialize<JsonElement>(query)
        });
        var orchestrator = CreateOrchestrator(store.Object, call,
            Final("NoEvidence", "No documents found."));
        var run = CreateRun("Find documents for Sample customer");

        // Act
        var failure = await Record.ExceptionAsync(() => orchestrator.ExecuteRunAsync(run));

        // Assert
        Assert.NotNull(failure);
        Assert.IsAssignableFrom<ArgumentException>(failure.GetBaseException());
        Assert.Null(run.Answer);
        Assert.Empty(run.Citations);
        store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(20)]
    public async Task A_Valid_Customer_Name_Search_Uses_The_Original_Text_And_Bounded_Page_Size(int pageSize)
    {
        // Arrange
        const string name = "Sample customer";
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        store.Setup(s => s.SearchDocumentsAsync("tenant-one",
                It.Is<DocumentQuery>(query => query.SearchText == name && query.PageSize == pageSize &&
                    query.Entities.Count == 0 && query.DocumentType == null && query.ContinuationToken == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentQueryPage([], null));
        var call = ToolCall("search_documents", new Dictionary<string, object?>
        {
            ["query"] = JsonSerializer.SerializeToElement(new DocumentQuery { SearchText = name, PageSize = pageSize })
        });
        var orchestrator = CreateOrchestrator(store.Object, call, Final("NoEvidence", "No matching documents found."));

        // Act
        var run = await orchestrator.ExecuteRunAsync(CreateRun("Find documents for " + name));

        // Assert
        Assert.Equal(JobStatus.Completed, run.Status);
        Assert.Empty(run.Citations);
        Assert.Single(run.ExecutedCosmosQueries);
        Assert.DoesNotContain(name, run.ExecutedCosmosQueries[0]);
        store.VerifyAll();
        store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(64, false)]
    [InlineData(5_000, true)]
    public async Task File_Insights_Are_Explicitly_Scoped_Context_And_Not_Source_Citation_Text(
        int summaryLength, bool truncated)
    {
        // Arrange
        var store = await CreateDocumentStoreAsync();
        var job = (await store.GetJobAsync("source", tenantId: "tenant-one"))!;
        job.PacketSummary = new string('x', summaryLength);
        job.Segments[0].InsightSummary = null;
        await store.UpsertJobAsync(job);
        var tenant = new TenantHarness { TenantId = "tenant-one", DisplayName = "Test" };
        var harness = new Mock<IHarnessStore>();
        harness.Setup(item => item.GetTenantAsync("tenant-one", It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        object? inspection = null;
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<ChatMessage> _, ChatOptions options, CancellationToken token) =>
            {
                var search = Assert.IsAssignableFrom<AIFunction>(
                    Assert.Single(options.Tools!, candidate => candidate.Name == "search_documents"));
                await search.InvokeAsync(new AIFunctionArguments
                {
                    ["query"] = JsonSerializer.SerializeToElement(new DocumentQuery())
                }, token);
                var tool = Assert.IsAssignableFrom<AIFunction>(
                    Assert.Single(options.Tools!, candidate => candidate.Name == "inspect_document_slice"));
                inspection = await tool.InvokeAsync(new AIFunctionArguments
                {
                    ["jobId"] = "source", ["sliceIndex"] = 0, ["textOffset"] = 0
                }, token);
                return Final("DocumentAnswer", "The source agreement [1].", "e1");
            });
        var catalog = new ModelCatalog(customClients:
            new Dictionary<string, IChatClient> { [tenant.DefaultModelId] = client.Object });
        var orchestrator = new AskAiOrchestrator(harness.Object, store, catalog, [],
            NullLogger<AskAiOrchestrator>.Instance);

        // Act
        var run = await orchestrator.ExecuteRunAsync(CreateRun());

        // Assert
        using var result = JsonSerializer.SerializeToDocument(inspection);
        Assert.Equal("CompleteFile", result.RootElement.GetProperty("fileInsight").GetProperty("scope").GetString());
        Assert.Equal(job.PacketSummary[..Math.Min(4_000, summaryLength)],
            result.RootElement.GetProperty("fileInsight").GetProperty("summary").GetString());
        Assert.Equal(truncated, result.RootElement.GetProperty("fileInsight").GetProperty("summaryTruncated").GetBoolean());
        Assert.DoesNotContain(job.PacketSummary!, Assert.Single(run.Citations).Excerpt);
    }

    private static async Task<InMemoryJobStore> CreateDocumentStoreAsync()
    {
        var store = new InMemoryJobStore();
        await store.UpsertJobAsync(new DocumentJob
        {
            JobId = "source",
            TenantId = "tenant-one",
            TenantDisplayName = "Test",
            AppliedHarnessVersion = 1,
            FileName = "source.pdf",
            Status = JobStatus.Completed,
            Segments =
            [
                new DocumentSlice
                {
                    StartPage = 2, EndPage = 3, BodyText = "Original Woodgrove agreement text.",
                    InsightSummary = "Derived summary, not a source quotation."
                }
            ]
        });
        return store;
    }

    private static ChatResponse SearchCall() => ToolCall("search_documents",
        new Dictionary<string, object?> { ["query"] = JsonSerializer.SerializeToElement(new DocumentQuery()) });

    private static ChatResponse InspectCall() => ToolCall("inspect_document_slice",
        new Dictionary<string, object?> { ["jobId"] = "source", ["sliceIndex"] = 0, ["textOffset"] = 0 });

    private static ChatResponse ToolCall(string name, Dictionary<string, object?> arguments) => new(
        new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, arguments)]));

    private static ChatResponse Final(string kind, string answer, params string[] evidenceIds) => new(
        new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(new { kind, answer, evidenceIds })));

    private static AskAiRun CreateRun(string question = "What agreements exist?") => new()
    {
        RunId = "one",
        TenantId = "tenant-one",
        Question = question
    };

    private static AskAiOrchestrator CreateOrchestrator(IJobStore store, params ChatResponse[] responses) =>
        CreateOrchestrator(store, [], responses);

    private static AskAiOrchestrator CreateOrchestrator(
        IJobStore store, IReadOnlyList<IAskAiToolProvider> tools, ChatResponse[] responses,
        ITenantAccessResolver? resolver = null, ReviewService? reviews = null, IReviewStore? reviewStore = null)
    {
        var tenant = new TenantHarness { TenantId = "tenant-one", DisplayName = "Test" };
        var harness = new Mock<IHarnessStore>();
        harness.Setup(s => s.GetTenantAsync(tenant.TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        var client = new Mock<IChatClient>();
        var sequence = client.SetupSequence(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()));
        foreach (var response in responses)
        {
            sequence.ReturnsAsync(response);
        }
        var catalog = new ModelCatalog(customClients: new Dictionary<string, IChatClient>
        {
            [tenant.DefaultModelId] = client.Object
        });
        return new AskAiOrchestrator(
            harness.Object, store, catalog, tools, NullLogger<AskAiOrchestrator>.Instance, resolver, reviews, reviewStore);
    }
}
