using Azure;
using Azure.AI.Translation.Document;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.DocumentTranslation;
using DocIntel.Core.Features.EntityExtraction;
using DocIntel.Core.Features.PromptColumns;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

using DocIntel.Core.Evidence;

namespace DocIntel.Tests;

public sealed class ProcessingTrustTests
{
    [Fact]
    public void Document_Processing_Does_Not_Simulate_Without_An_Explicit_Development_Choice()
    {
        // Arrange
        var catalog = new ModelCatalog();

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => catalog.ResolveChatClient(CreateTenant()));
    }

    [Fact]
    public void Explicit_Development_Simulation_Is_Identifiable_And_Still_Disallowed_For_Ask_AI()
    {
        // Arrange
        var catalog = new ModelCatalog(allowDevelopmentSimulation: true);
        var tenant = CreateTenant();

        // Act / Assert
        Assert.IsType<SimulatedSliceChatClient>(catalog.ResolveChatClient(tenant));
        Assert.True(catalog.UsesDevelopmentSimulation(tenant));
        Assert.Throws<InvalidOperationException>(() => catalog.ResolveRequiredChatClient(tenant));
    }

    [Fact]
    public async Task A_Provider_Failure_Is_Not_Replaced_By_Simulation_Even_When_Development_Is_Enabled()
    {
        // Arrange
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Provider unavailable."));
        var catalog = Catalog(client.Object, allowSimulation: true);
        using var resolved = catalog.ResolveChatClient(CreateTenant());

        // Act / Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => resolved.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Question")]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("Plain prose is not the required result schema.")]
    [InlineData("{\"segments\":[]}")]
    public async Task Invalid_Prompt_Output_Is_Not_Replaced_With_A_Deterministic_Template(string output)
    {
        // Arrange
        var client = ResponseClient(output);
        var job = CreateJob();

        // Act
        var exception = await Record.ExceptionAsync(() => EvaluatePromptColumnsExecutor.EvaluateJobPromptsAsync(
            job, CreateTenant(), Catalog(client.Object), forceReevaluateAll: true));

        // Assert
        Assert.NotNull(exception);
        Assert.Empty(job.PromptColumnResults);
        Assert.Empty(job.Segments[0].PromptColumnResults);
        client.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Failed_Pdf_Execution_Is_Not_Silently_Retried_As_Text()
    {
        // Arrange
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("PDF request failed."));

        // Act / Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => EvaluatePromptColumnsExecutor.EvaluateJobPromptsAsync(
            CreateJob(), CreateTenant(), Catalog(client.Object)));
        client.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Text_Only_Models_Use_An_Explicit_Text_Input_Mode()
    {
        // Arrange
        var job = CreateJob();
        job.Segments[0].SplitPdfBytes = null;
        var client = ResponseClient("{\"result\":\"A source-grounded text result.\"}");
        var tenant = CreateTenant("phi-4");

        // Act
        var result = await EvaluatePromptColumnsExecutor.EvaluateJobPromptsAsync(job, tenant,
            Catalog(client.Object, modelId: "phi-4"));

        // Assert
        Assert.False(Assert.Single(result).ProcessedAsFileBase64);
        client.Verify(c => c.GetResponseAsync(
            It.Is<IEnumerable<ChatMessage>>(messages => !messages.SelectMany(m => m.Contents).OfType<DataContent>().Any()),
            It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Missing_Pdf_Artifacts_Are_Not_Reconstructed_From_Plain_Text()
    {
        // Arrange
        var job = CreateJob();
        job.Segments[0].SplitPdfBytes = null;
        var client = new Mock<IChatClient>(MockBehavior.Strict);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EvaluatePromptColumnsExecutor.EvaluateJobPromptsAsync(job, CreateTenant(), Catalog(client.Object)));
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Failed_Reevaluation_Does_Not_Publish_Partial_Slice_Results()
    {
        // Arrange
        var job = CreateJob();
        job.Segments.Add(new DocumentSlice { SegmentIndex = 1, StartPage = 2, EndPage = 2, BodyText = "Second." });
        var original = job.Segments[0].PromptColumnResults;
        var client = ResponseClient("{\"result\":\"First slice result.\"}");

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EvaluatePromptColumnsExecutor.EvaluateJobPromptsAsync(job, CreateTenant(), Catalog(client.Object)));
        Assert.Same(original, job.Segments[0].PromptColumnResults);
        Assert.Empty(job.PromptColumnResults);
    }

    [Fact]
    public async Task Prompt_Caches_Are_Invalidated_When_Source_Content_Changes()
    {
        // Arrange
        var tenant = CreateTenant("phi-4");
        var job = CreateJob();
        var client = new Mock<IChatClient>();
        client.SetupSequence(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"result\":\"First.\"}")))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"result\":\"Second.\"}")));
        var catalog = Catalog(client.Object, modelId: "phi-4");
        await EvaluatePromptColumnsExecutor.EvaluateJobPromptsAsync(job, tenant, catalog);
        var previousFingerprint = job.PromptColumnResults[0].InputFingerprint;
        job.Segments[0].BodyText = "Changed source content.";

        // Act
        await EvaluatePromptColumnsExecutor.EvaluateJobPromptsAsync(job, tenant, catalog);

        // Assert
        Assert.Equal("Second.", job.PromptColumnResults[0].ResultText);
        Assert.NotEqual(previousFingerprint, job.PromptColumnResults[0].InputFingerprint);
    }

    [Fact]
    public async Task Grounding_Metadata_Does_Not_Invalidate_Unchanged_Prompt_Input()
    {
        // Arrange
        var tenant = CreateTenant("phi-4");
        var job = CreateJob();
        job.Segments[0].ExtractedEntities = [new ExtractedEntity("Customer", "Woodgrove", "Party", 0.9)];
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"result\":\"Same input.\"}")));
        var catalog = Catalog(client.Object, modelId: "phi-4");
        await EvaluatePromptColumnsExecutor.EvaluateJobPromptsAsync(job, tenant, catalog);
        var fingerprint = job.PromptColumnResults[0].InputFingerprint;
        job.Segments[0].ExtractedEntities = [job.Segments[0].ExtractedEntities[0] with
        {
            Provenance = new ValueProvenance(GroundingStatus.NotFound, [], [], null),
            ConfidenceSignals = new ConfidenceSignals(0.9, 0)
        }];

        // Act
        await EvaluatePromptColumnsExecutor.EvaluateJobPromptsAsync(job, tenant, catalog);

        // Assert
        Assert.Equal(fingerprint, job.PromptColumnResults[0].InputFingerprint);
        client.Verify(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Missing_Translation_Configuration_Does_Not_Create_A_Fake_Translated_Pdf()
    {
        // Arrange
        var service = new AzureDocumentTranslationService();

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslatePdfSliceAsync(
            [1], "source.pdf", "Formulario Firma Programa", [], "en", null, []));
    }

    [Fact]
    public async Task Translation_Provider_Errors_Propagate_Instead_Of_Using_Local_Phrase_Replacements()
    {
        // Arrange
        var client = new Mock<SingleDocumentTranslationClient>();
        client.Setup(c => c.TranslateAsync(It.IsAny<string>(), It.IsAny<DocumentTranslateContent>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "Translation unavailable."));
        var service = new AzureDocumentTranslationService(allowDevelopmentSimulation: true, client: client.Object);

        // Act / Assert
        await Assert.ThrowsAsync<RequestFailedException>(() => service.TranslatePdfSliceAsync(
            [1], "source.pdf", "Formulario Firma Programa", [], "en", "https://translator.example.invalid", []));
    }

    [Fact]
    public async Task Translation_Uses_Provider_Detection_Instead_Of_Claiming_All_NonSpanish_Text_Is_English()
    {
        // Arrange
        var client = new Mock<SingleDocumentTranslationClient>();
        var translated = PdfPigLayoutExtractor.CreateSamplePdf([("Translated provider output.", "Footer")]);
        client.Setup(c => c.TranslateAsync(It.IsAny<string>(), It.IsAny<DocumentTranslateContent>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(BinaryData.FromBytes(translated), new Mock<Response>().Object));
        var service = new AzureDocumentTranslationService(client: client.Object);

        // Act
        var result = await service.TranslatePdfSliceAsync([1], "source.pdf", "Bonjour le monde",
            [], "en", "https://translator.example.invalid", []);

        // Assert
        Assert.True(result.WasTranslated);
        Assert.Equal("und", result.DetectedLanguage);
        Assert.False(result.IsDevelopmentSimulation);
        Assert.Contains("Translated provider output", result.TranslatedText);
        client.Verify(c => c.TranslateAsync("en", It.IsAny<DocumentTranslateContent>(),
            null, It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Translation_Cancellation_Is_Not_Converted_To_A_Translated_Success()
    {
        // Arrange
        var client = new Mock<SingleDocumentTranslationClient>();
        client.Setup(c => c.TranslateAsync(It.IsAny<string>(), It.IsAny<DocumentTranslateContent>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var service = new AzureDocumentTranslationService(allowDevelopmentSimulation: true, client: client.Object);

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranslatePdfSliceAsync(
            [1], "source.pdf", "Formulario Firma Programa", [], "en", "https://translator.example.invalid", []));
    }

    [Fact]
    public async Task Tenant_Translation_Settings_Cannot_Select_Arbitrary_Credential_Bearing_Endpoints()
    {
        // Arrange
        var tenant = CreateTenant();
        tenant.Capabilities[FeatureHarness.DocumentTranslationKey].DocumentTranslation!.AzureTranslatorEndpoint =
            "https://untrusted.example.invalid";
        var service = new Mock<IDocumentTranslationService>(MockBehavior.Strict);
        var executor = new TranslateDocumentsExecutor(service.Object, "https://trusted.example.invalid");

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await executor.HandleAsync(
            new SplittingState { Tenant = tenant, FileName = "source.pdf" }, new Mock<IWorkflowContext>().Object));
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Zero_Confidence_Is_Not_Inflated_To_Ninety_Five_Percent()
    {
        // Arrange
        var client = ResponseClient("{\"entities\":[{\"name\":\"A\",\"value\":\"B\",\"confidence\":0}]}");
        var state = new SplittingState
        {
            Tenant = CreateTenant(),
            FileName = "source.pdf",
            Slices = [new DocumentSlice { BodyText = "A: B" }]
        };
        var executor = new ExtractEntitiesExecutor(Catalog(client.Object));

        // Act
        await executor.HandleAsync(state, new Mock<IWorkflowContext>().Object);

        // Assert
        Assert.Equal(0, Assert.Single(state.ExtractedEntities).Confidence);
        Assert.Equal(0, new ExtractedFieldValue("A", "B").ConfidenceValue);
    }

    private static TenantHarness CreateTenant(string modelId = "gpt-5-mini")
    {
        var tenant = new TenantHarness { TenantId = "tenant", DisplayName = "Test", DefaultModelId = modelId };
        tenant.Capabilities[FeatureHarness.PromptColumnsKey].ModelIdOverride = modelId;
        tenant.Capabilities[FeatureHarness.PromptColumnsKey].PromptColumns = new PromptColumnsConfig
        {
            Columns = [new PromptColumnDefinition { ColumnId = "one", ColumnName = "Summary",
                PromptInstructions = "Summarize {{Document.Content}}.", FilterMode = "All" }]
        };
        return tenant;
    }

    private static DocumentJob CreateJob() => new()
    {
        JobId = "one",
        TenantId = "tenant",
        TenantDisplayName = "Test",
        AppliedHarnessVersion = 1,
        FileName = "source.pdf",
        Status = JobStatus.Completed,
        Segments = [new DocumentSlice { SegmentIndex = 0, StartPage = 1, EndPage = 1, BodyText = "Original source.",
            SplitPdfBytes = PdfPigLayoutExtractor.CreateSamplePdf([("Original source.", "Footer")]) }]
    };

    private static Mock<IChatClient> ResponseClient(string output)
    {
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, output)));
        return client;
    }

    private static ModelCatalog Catalog(IChatClient client, bool allowSimulation = false, string modelId = "gpt-5-mini") =>
        new(customClients: new Dictionary<string, IChatClient> { [modelId] = client },
            allowDevelopmentSimulation: allowSimulation);
}
