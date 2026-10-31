using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using DocIntel.Core;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.DocumentTranslation;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using DocIntel.Core.Storage;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class TranslationReliabilityTests
{
    [Fact]
    public async Task Unsupported_Latin_Is_Reported_Explicitly_Without_Attempting_A_Pdf_Translation()
    {
        // Arrange
        var detection = new DocumentLanguageResult("la")
        {
            UseAutomaticSourceLanguage = true,
            Samples = [new(0, 100, "la", 0.99, false)]
        };
        var detector = Detector("la");
        detector.Setup(provider => provider.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(detection);
        var batch = new Mock<IPdfBatchTranslationClient>(MockBehavior.Strict);
        var service = new AzureDocumentTranslationService(detector.Object, batch.Object);

        // Act
        var failure = await Assert.ThrowsAsync<DocumentTranslationFailureException>(() =>
            service.TranslatePdfSliceAsync("%PDF-original"u8.ToArray(), "source.pdf",
                "Lorem ipsum dolor sit amet.", [], "en", "https://translator.example/", []));

        // Assert
        Assert.Equal("TranslationLanguageUnsupported", failure.Code);
        Assert.Equal("la", failure.Language);
        Assert.Same(detection, failure.LanguageDetection);
        Assert.Contains("not supported", failure.Message);
        batch.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("mul")]
    public async Task Usable_Partial_Or_Lower_Score_Detection_Translates_The_Whole_Pdf_With_Automatic_Source(
        string language)
    {
        // Arrange
        var original = PdfPigLayoutExtractor.CreateSamplePdf([("Original accepted source", "Original")]);
        var translated = PdfPigLayoutExtractor.CreateSamplePdf([("Translated complete document", "Translated")]);
        var detection = new DocumentLanguageResult(language)
        {
            UseAutomaticSourceLanguage = true,
            Samples = [new(0, 100, language == "mul" ? "fr" : language, 0.65, true)]
        };
        var detector = Detector(language);
        detector.Setup(provider => provider.DetectAsync("Original accepted source", It.IsAny<CancellationToken>()))
            .ReturnsAsync(detection);
        var batch = new Mock<IPdfBatchTranslationClient>(MockBehavior.Strict);
        batch.Setup(provider => provider.TranslateAsync(
                original, null, "en", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(translated);
        var service = new AzureDocumentTranslationService(detector.Object, batch.Object);

        // Act
        var result = await service.TranslatePdfSliceAsync(original, "source.pdf", "Original accepted source",
            [], "en", "https://translator.example/", []);

        // Assert
        Assert.True(result.WasTranslated);
        Assert.Equal(language, result.DetectedLanguage);
        Assert.Same(detection, result.LanguageDetection);
        Assert.Equal(translated, result.TranslatedPdfBytes);
        Assert.Contains("Translated complete document", result.TranslatedText);
        batch.Verify(provider => provider.TranslateAsync(original, null, "en",
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Logical_Documents_Have_Independent_Language_Checks_Translation_And_Failures()
    {
        // Arrange
        var source = State();
        source.Slices =
        [
            new() { SegmentIndex = 0, StartPage = 1, EndPage = 1, Markdown = "English document source",
                BodyText = "Original English body", SplitPdfBytes = "%PDF-English"u8.ToArray() },
            new() { SegmentIndex = 1, StartPage = 2, EndPage = 3, Markdown = "French document source",
                BodyText = "Original French body", SplitPdfBytes = "%PDF-French"u8.ToArray() },
            new() { SegmentIndex = 2, StartPage = 4, EndPage = 4, Markdown = "Uncertain document source",
                BodyText = "Original uncertain body", SplitPdfBytes = "%PDF-Uncertain"u8.ToArray() }
        ];
        var english = new DocumentLanguageResult("en") { Samples = [new(0, 100, "en", 0.99, true)] };
        var french = new DocumentLanguageResult("fr")
            { UseAutomaticSourceLanguage = true, Samples = [new(0, 100, "fr", 0.6, true)] };
        var uncertain = new DocumentLanguageResult("und")
            { UseAutomaticSourceLanguage = true, Samples = [new(0, 100, "en", 0.2, true)] };
        var detector = new Mock<IDocumentLanguageDetector>(MockBehavior.Strict);
        detector.Setup(provider => provider.DetectAsync("English document source", It.IsAny<CancellationToken>()))
            .ReturnsAsync(english);
        detector.Setup(provider => provider.DetectAsync("French document source", It.IsAny<CancellationToken>()))
            .ReturnsAsync(french);
        detector.Setup(provider => provider.DetectAsync("Uncertain document source", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DocumentTranslationFailureException("LanguageUndetermined", "No usable chunks.", "und",
                languageDetection: uncertain));
        var batch = new Mock<IPdfBatchTranslationClient>(MockBehavior.Strict);
        var translated = PdfPigLayoutExtractor.CreateSamplePdf(
            [("Translated first French page", "First"), ("Translated final French page", "Second")]);
        batch.Setup(provider => provider.TranslateAsync(source.Slices[1].SplitPdfBytes!, null, "en",
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(translated);
        var service = new AzureDocumentTranslationService(detector.Object, batch.Object);

        // Act
        var result = await new TranslateDocumentsExecutor(service, "https://translator.example/")
            .HandleAsync(source, Mock.Of<IWorkflowContext>());
        var saved = JsonSerializer.Deserialize<SplittingState>(
            JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        // Assert
        Assert.False(result.Slices[0].WasTranslated);
        Assert.Null(result.Slices[0].TranslatedText);
        Assert.True(result.Slices[1].WasTranslated);
        Assert.Equal(translated, result.Slices[1].TranslatedPdfBytes);
        Assert.Contains("Translated final French page", result.Slices[1].TranslatedText!);
        Assert.Equal("LanguageUndetermined", result.Slices[2].TranslationErrorCode);
        Assert.False(Assert.Single(result.ExecutionTrace).Succeeded);
        Assert.Equal(new[] { "en", "fr", "und" }, saved.Slices.Select(slice => slice.DetectedLanguage));
        Assert.Equal(0.6, Assert.Single(saved.Slices[1].LanguageDetection!.Samples).Score);
        Assert.Equal(0.2, Assert.Single(saved.Slices[2].LanguageDetection!.Samples).Score);
        Assert.Equal("Original uncertain body", saved.Slices[2].BodyText);
        detector.Verify(provider => provider.DetectAsync(It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
        batch.Verify(provider => provider.TranslateAsync(It.IsAny<byte[]>(), It.IsAny<string?>(), "en",
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Historical_Slices_Without_Detection_Diagnostics_Keep_Their_Original_Serialization()
    {
        // Arrange / Act
        var original = JsonSerializer.Serialize(new DocumentSlice { BodyText = "Historical source" });

        // Assert
        Assert.DoesNotContain("languageDetection", original);
    }

    [Fact]
    public async Task Same_Language_Skip_Preserves_Complete_Content_Understanding_Source_For_Downstream_Features()
    {
        // Arrange
        var state = State();
        const string markdown = "# Invoice\n| Item | Amount |\n| Bottom-of-page obligation | USD 1234 |";
        state.Slices[0].Markdown = markdown;
        state.Slices[0].BodyText = "Invoice without the bottom-of-page amount";
        var service = new Mock<IDocumentTranslationService>(MockBehavior.Strict);
        service.Setup(provider => provider.TranslatePdfSliceAsync(
                It.IsAny<byte[]>(), It.IsAny<string>(), markdown, It.IsAny<IReadOnlyList<string>>(),
                "en", "https://translator.example/", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentSliceTranslationResult("en", false, markdown));

        // Act
        var result = await new TranslateDocumentsExecutor(service.Object, "https://translator.example/")
            .HandleAsync(state, Mock.Of<IWorkflowContext>());

        // Assert
        Assert.Null(result.Slices[0].TranslatedText);
        Assert.False(result.Slices[0].WasTranslated);
        Assert.Equal(markdown, DocumentPromptBoundary.SourceText(result.Slices[0]));
        Assert.Contains("USD 1234", DocumentPromptBoundary.SourceText(result.Slices[0]));
        Assert.True(Assert.Single(result.ExecutionTrace).Succeeded);
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("fr", "fr")]
    public async Task Same_Language_Retains_Original_Source_Without_A_Pdf_Translation_Request(string source, string target)
    {
        // Arrange
        var detector = Detector(source);
        var batch = new Mock<IPdfBatchTranslationClient>(MockBehavior.Strict);
        var service = new AzureDocumentTranslationService(detector.Object, batch.Object);
        var original = "%PDF-original"u8.ToArray();
        var before = original.ToArray();

        // Act
        var result = await service.TranslatePdfSliceAsync(original, "source.pdf", "Original source text",
            [], target, "https://translator.example/", []);

        // Assert
        Assert.False(result.WasTranslated);
        Assert.Equal(source, result.DetectedLanguage);
        Assert.Equal("Original source text", result.TranslatedText);
        Assert.Null(result.TranslatedPdfBytes);
        Assert.False(result.IsDevelopmentSimulation);
        Assert.Equal(before, original);
        batch.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Unknown_Language_Does_Not_Become_An_English_Skip_Or_A_Blind_Translation()
    {
        // Arrange
        var detector = new Mock<IDocumentLanguageDetector>();
        detector.Setup(client => client.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DocumentTranslationFailureException("LanguageUndetermined", "Unknown language", "und"));
        var batch = new Mock<IPdfBatchTranslationClient>(MockBehavior.Strict);
        var service = new AzureDocumentTranslationService(detector.Object, batch.Object);

        // Act
        var failure = await Assert.ThrowsAsync<DocumentTranslationFailureException>(() => service.TranslatePdfSliceAsync(
            "%PDF-source"u8.ToArray(), "source.pdf", "Ambiguous source", [], "en", "https://translator.example/", []));

        // Assert
        Assert.Equal("LanguageUndetermined", failure.Code);
        Assert.Equal("und", failure.Language);
        batch.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Provider_Response_IO_Failures_Are_Explicit_Translation_Only_Failures(bool detectFailure)
    {
        // Arrange
        var detector = Detector("fr");
        var batch = new Mock<IPdfBatchTranslationClient>(MockBehavior.Strict);
        if (detectFailure)
            detector.Setup(client => client.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("Interrupted private provider response."));
        else
            batch.Setup(client => client.TranslateAsync(
                    It.IsAny<byte[]>(), "fr", "en", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("Interrupted private output stream."));
        var service = new AzureDocumentTranslationService(detector.Object, batch.Object);
        var original = "%PDF-source"u8.ToArray();

        // Act
        var failure = await Assert.ThrowsAsync<DocumentTranslationFailureException>(() =>
            service.TranslatePdfSliceAsync(original, "source.pdf", "Original source", [],
                "en", "https://translator.example/", []));

        // Assert
        Assert.Equal("TranslationProviderFailed", failure.Code);
        Assert.DoesNotContain("private", failure.Message);
        Assert.IsType<IOException>(failure.InnerException);
        Assert.Equal("%PDF-source"u8.ToArray(), original);
    }

    [Fact]
    public async Task An_Unreadable_Provider_Pdf_Is_A_Translation_Warning_Not_A_Source_Failure()
    {
        // Arrange
        var original = PdfPigLayoutExtractor.CreateSamplePdf([("Accepted source evidence", "Fixture")]);
        var batch = new Mock<IPdfBatchTranslationClient>();
        batch.Setup(client => client.TranslateAsync(
                It.IsAny<byte[]>(), "fr", "en", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("%PDF-1.7\nmalformed provider output"u8.ToArray());
        var service = new AzureDocumentTranslationService(Detector("fr").Object, batch.Object);

        // Act
        var failure = await Assert.ThrowsAsync<DocumentTranslationFailureException>(() =>
            service.TranslatePdfSliceAsync(original, "source.pdf", "Accepted source evidence", [],
                "en", "https://translator.example/", []));

        // Assert
        Assert.Equal("TranslationInvalidResponse", failure.Code);
        Assert.Equal("fr", failure.Language);
        Assert.DoesNotContain("malformed", failure.Message);
        Assert.Single(await new PdfPigLayoutExtractor().ExtractPagesAsync(original, new()));
    }

    [Fact]
    public async Task Translation_Failure_Is_Explicit_And_Does_Not_Destroy_Original_Source()
    {
        // Arrange
        var source = State();
        var service = new Mock<IDocumentTranslationService>(MockBehavior.Strict);
        service.Setup(provider => provider.TranslatePdfSliceAsync(
                It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                "en", "https://translator.example/", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DocumentTranslationFailureException("TranslationProviderFailed", "Provider unavailable", "fr", 503));

        // Act
        var result = await new TranslateDocumentsExecutor(service.Object, "https://translator.example/")
            .HandleAsync(source, Mock.Of<IWorkflowContext>());

        // Assert
        Assert.False(Assert.Single(result.ExecutionTrace).Succeeded);
        Assert.Equal("Original evidence", result.Slices[0].BodyText);
        Assert.False(result.Slices[0].WasTranslated);
        Assert.Null(result.Slices[0].TranslatedPdfBytes);
        Assert.Equal("fr", result.Slices[0].DetectedLanguage);
        Assert.Equal("en", result.Slices[0].TargetLanguage);
        Assert.Equal("TranslationProviderFailed", result.Slices[0].TranslationErrorCode);
    }

    [Fact]
    public async Task A_Translated_Pdf_Is_Not_Published_Until_Its_Artifact_Save_Succeeds()
    {
        // Arrange
        var source = State();
        var service = new Mock<IDocumentTranslationService>();
        service.Setup(provider => provider.TranslatePdfSliceAsync(
                It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                "en", "https://translator.example/", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentSliceTranslationResult("fr", true, "Translated", "%PDF-translation"u8.ToArray()));
        var artifacts = new Mock<IArtifactStore>();
        artifacts.Setup(store => store.SaveSlicePdfAsync("workspace", It.IsAny<string>(), 0,
            It.IsAny<byte[]>(), It.IsAny<CancellationToken>(), true))
            .ThrowsAsync(new RequestFailedException(503, "Storage unavailable"));

        // Act
        var result = await new TranslateDocumentsExecutor(service.Object, "https://translator.example/", artifacts.Object)
            .HandleAsync(source, Mock.Of<IWorkflowContext>());

        // Assert
        Assert.False(Assert.Single(result.ExecutionTrace).Succeeded);
        Assert.Equal("TranslationStorageFailed", result.Slices[0].TranslationErrorCode);
        Assert.False(result.Slices[0].WasTranslated);
        Assert.Null(result.Slices[0].TranslatedPdfBytes);
        Assert.Null(result.Slices[0].TranslatedPdfBlobUri);
        Assert.Null(result.Slices[0].TranslatedText);
        Assert.Equal("Original evidence", result.Slices[0].BodyText);
    }

    [Fact]
    public async Task Cancellation_Still_Aborts_The_Workflow_Instead_Of_Becoming_An_Optional_Warning()
    {
        // Arrange
        var source = State();
        using var cancellation = new CancellationTokenSource();
        var service = new Mock<IDocumentTranslationService>();
        service.Setup(provider => provider.TranslatePdfSliceAsync(
                It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                "en", "https://translator.example/", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => cancellation.Cancel()).ThrowsAsync(new OperationCanceledException(cancellation.Token));

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TranslateDocumentsExecutor(service.Object, "https://translator.example/")
                .HandleAsync(source, Mock.Of<IWorkflowContext>(), cancellation.Token).AsTask());

        // Assert
        Assert.Empty(source.ExecutionTrace);
        Assert.Null(source.Slices[0].TranslationErrorCode);
    }

    [Fact]
    public async Task Translation_Failure_Still_Publishes_Durable_Insights_And_Entities_With_An_Explicit_Warning()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "DocIntel.PartialProcessing", Guid.NewGuid().ToString("N"));
        var tenant = new TenantHarness
        {
            TenantId = "workspace", DisplayName = "Workspace", DefaultModelId = "phi-4",
            Capabilities = new(StringComparer.OrdinalIgnoreCase)
            {
                [FeatureHarness.SplitAndGroupKey] = new() { Enabled = true, SplitAndGroup = new() },
                [FeatureHarness.DocumentTranslationKey] = new() { Enabled = true, DocumentTranslation = new() },
                [FeatureHarness.DocumentInsightKey] = new() { Enabled = true, ModelIdOverride = "phi-4", DocumentInsight = new() },
                [FeatureHarness.EntityExtractionKey] = new() { Enabled = true, ModelIdOverride = "phi-4", EntityExtraction = new() }
            }
        };
        var source = PdfPigLayoutExtractor.CreateSamplePdf(
            [("Invoice\nCustomer Name: Northwind Example\nQuote Number: SYN-001\nTotal deal value: 100 USD", "Form-Invoice-v1")]);
        var harness = new Mock<IHarnessStore>();
        harness.Setup(store => store.GetTenantAsync("workspace", It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        var translation = new Mock<IDocumentTranslationService>();
        translation.Setup(provider => provider.TranslatePdfSliceAsync(
                It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DocumentTranslationFailureException("TranslationProviderFailed", "Provider unavailable", "fr", 503,
                languageDetection: new DocumentLanguageResult("fr")
                    { UseAutomaticSourceLanguage = true, Samples = [new(0, 100, "fr", 0.6, true)] }));
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(harness.Object).AddSingleton(translation.Object);
        services.AddDocIntelCore(harnessFilePath: Path.Combine(directory, "harness.json"), allowDevelopmentSimulation: true);
        try
        {
            using var provider = services.BuildServiceProvider();
            var jobs = provider.GetRequiredService<IJobStore>();
            var artifacts = provider.GetRequiredService<IArtifactStore>();
            await artifacts.SaveOriginalPdfAsync("workspace", "job", source);
            await jobs.UpsertJobAsync(new DocumentJob
            {
                JobId = "job", TenantId = "workspace", TenantDisplayName = "Workspace", FileName = "invoice.pdf",
                AppliedHarnessVersion = tenant.HarnessVersion, ProcessingHarness = tenant,
                OriginalSha256 = Convert.ToHexString(SHA256.HashData(source))
            });

            // Act
            await provider.GetRequiredService<DocumentJobProcessor>().ProcessAsync(new("job", "workspace"));
            var completed = (await jobs.GetJobAsync("job", tenantId: "workspace"))!;

            // Assert
            Assert.Equal(JobStatus.Completed, completed.Status);
            Assert.True(completed.HasWarnings);
            Assert.Null(completed.ErrorMessage);
            Assert.NotNull(completed.ProcessingResultSha256);
            Assert.NotNull(await artifacts.GetProcessingResultAsync(completed));
            Assert.Equal(0.6, Assert.Single(completed.Segments[0].LanguageDetection!.Samples).Score);
            using var archive = JsonDocument.Parse((await artifacts.GetProcessingResultAsync(completed))!);
            Assert.Equal(0.6, archive.RootElement.GetProperty("segments")[0]
                .GetProperty("languageDetection").GetProperty("samples")[0].GetProperty("score").GetDouble());
            Assert.False(Assert.Single(completed.ExecutionTrace,
                step => step.ExecutorName == "TranslateDocumentsExecutor").Succeeded);
            Assert.Contains(completed.ExecutionTrace, step => step.ExecutorName == "GenerateDocumentInsightsExecutor" && step.Succeeded);
            Assert.Contains(completed.ExecutionTrace, step => step.ExecutorName == "ExtractEntitiesExecutor" && step.Succeeded);
            Assert.False(String.IsNullOrWhiteSpace(completed.PacketSummary));
            Assert.Equal("TranslationProviderFailed", Assert.Single(completed.Segments).TranslationErrorCode);
            Assert.Equal(source, await artifacts.GetOriginalPdfAsync("workspace", "job"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static Mock<IDocumentLanguageDetector> Detector(string language)
    {
        var detector = new Mock<IDocumentLanguageDetector>(MockBehavior.Strict);
        detector.Setup(client => client.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentLanguageResult(language));
        return detector;
    }

    private static SplittingState State() => new()
    {
        Tenant = new() { TenantId = "workspace", DisplayName = "Workspace" }, FileName = "source.pdf",
        Slices = [new() { SegmentIndex = 0, StartPage = 1, EndPage = 1, BodyText = "Original evidence", SplitPdfBytes = "%PDF-source"u8.ToArray() }]
    };
}
