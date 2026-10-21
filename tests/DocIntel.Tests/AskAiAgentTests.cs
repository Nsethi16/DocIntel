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
        var toolCalls = Enumerable.Range(1, 25).Select(i => (AIContent)new FunctionCallContent(
            $"call-{i}", "count_calls", new Dictionary<string, object?>())).ToList();
        var orchestrator = CreateOrchestrator(new InMemoryJobStore(), [extension.Object],
            [new ChatResponse(new ChatMessage(ChatRole.Assistant, toolCalls)),
             Final("Conversation", "Finished.")]);
        var run = CreateRun();

        // Act
        await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.ExecuteRunAsync(run));

        // Assert
        Assert.Equal(24, calls);
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
    public async Task Human_Corrections_Have_Explicit_Review_Evidence_Not_A_False_Original_Quotation()
    {
        // Arrange
        var store = await CreateDocumentStoreAsync();
        var job = (await store.GetJobAsync("source", tenantId: "tenant-one"))!;
        job.OriginalSha256 = new string('A', 64);
        job.Segments[0].ExtractedEntities = [new ExtractedEntity("Customer", "Original", "Party", 0.9, 0)];
        var reviewStore = new InMemoryReviewStore();
        var reviews = new ReviewService(reviewStore);
        var actor = new ActorIdentity(Guid.NewGuid(), Guid.NewGuid());
        job.ReviewId = await reviews.CreateAsync(job, new ReviewPolicy());
        var claimed = await reviews.ChangeAsync(job, actor, 1, "claim");
        var corrected = await reviews.ChangeAsync(job, actor, claimed.Version, "field",
            "entity:0:0", FieldReviewOutcome.Corrected, "Human verified customer", "Original checked.");
        var approved = await reviews.ChangeAsync(job, actor, corrected.Version, "submit");
        var call = ToolCall("inspect_human_review",
            new Dictionary<string, object?> { ["jobId"] = "source", ["sliceIndex"] = 0, ["offset"] = 0, ["pageSize"] = 5 });
        var orchestrator = CreateOrchestrator(store, [], [
            SearchCall(), call, Final("DocumentAnswer", "Human review corrected the customer [1].", "e2")
        ], reviews: reviews, reviewStore: reviewStore);

        // Act
        var run = await orchestrator.ExecuteRunAsync(CreateRun());

        // Assert
        var citation = Assert.Single(run.Citations);
        Assert.Equal("HumanReviewedField", citation.EvidenceKind);
        Assert.Equal(approved.Version, citation.ReviewVersion);
        Assert.Equal("Human verified customer", citation.Excerpt);
        Assert.StartsWith("/review.html?", citation.WorkspaceUrl);
        Assert.Equal("Original Woodgrove agreement text.", job.Segments[0].BodyText);
    }

    [Fact]
    public async Task Pending_Human_Review_Does_Not_Expose_Unapproved_Effective_Value_Evidence()
    {
        // Arrange
        var store = await CreateDocumentStoreAsync();
        var job = (await store.GetJobAsync("source", tenantId: "tenant-one"))!;
        job.OriginalSha256 = new string('A', 64);
        var reviewStore = new InMemoryReviewStore();
        var reviews = new ReviewService(reviewStore);
        job.ReviewId = await reviews.CreateAsync(job, new ReviewPolicy());
        var call = ToolCall("inspect_human_review", new Dictionary<string, object?> { ["jobId"] = "source" });
        var orchestrator = CreateOrchestrator(store, [], [
            SearchCall(), call, Final("DocumentAnswer", "The document is pending human review [1].", "e1")
        ], reviews: reviews, reviewStore: reviewStore);

        // Act
        var run = await orchestrator.ExecuteRunAsync(CreateRun());

        // Assert
        var citation = Assert.Single(run.Citations);
        Assert.Equal("HumanReviewStatus", citation.EvidenceKind);
        Assert.Equal("Pending", citation.Excerpt);
    }

    [Fact]
    public async Task A_Review_Change_Before_Answer_Publication_Invalidates_The_Old_Status_Evidence()
    {
        // Arrange
        var store = await CreateDocumentStoreAsync();
        var job = (await store.GetJobAsync("source", tenantId: "tenant-one"))!;
        job.OriginalSha256 = new string('A', 64);
        var initial = ReviewEngine.Create(job, new ReviewPolicy(), DateTimeOffset.UtcNow);
        var created = ReviewEngine.Audit(initial, "Created", null, initial.CreatedAtUtc);
        initial = initial with { LastAuditHash = created.Hash };
        job.ReviewId = initial.Id;
        var reviewStore = new Mock<IReviewStore>();
        reviewStore.SetupSequence(s => s.GetAsync("tenant-one", initial.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(initial).ReturnsAsync(initial with { Version = 2, Status = ReviewStatus.InReview });
        reviewStore.Setup(s => s.GetAuditAsync("tenant-one", initial.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([created]);
        var reviews = new ReviewService(reviewStore.Object);
        var orchestrator = CreateOrchestrator(store, [], [
            SearchCall(), ToolCall("inspect_human_review", new Dictionary<string, object?> { ["jobId"] = "source" }),
            Final("DocumentAnswer", "Review is pending [1].", "e1")
        ], reviews: reviews, reviewStore: reviewStore.Object);
        var run = CreateRun();

        // Act / Assert
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ExecuteRunAsync(run));
        Assert.Contains("review changed", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(run.Answer);
        Assert.Empty(run.Citations);
    }

    [Fact]
    public async Task Human_Review_Inspection_Cannot_Bypass_The_Preceding_Authorized_Search()
    {
        // Arrange
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        var orchestrator = CreateOrchestrator(store.Object,
            ToolCall("inspect_human_review", new Dictionary<string, object?> { ["jobId"] = "source" }),
            Final("NoEvidence", "No reviewed values."));

        // Act / Assert
        await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.ExecuteRunAsync(CreateRun()));
        store.VerifyNoOtherCalls();
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
