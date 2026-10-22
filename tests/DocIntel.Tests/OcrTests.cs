using Azure;
using Azure.AI.DocumentIntelligence;
using DocIntel.Core;
using DocIntel.Core.Evidence;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Ocr;
using DocIntel.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class OcrTests
{
    [Theory]
    [InlineData(49, 0.51, true)]
    [InlineData(50, 0.51, false)]
    [InlineData(49, 0.50, false)]
    public async Task Automatic_Ocr_Uses_Both_The_Exact_Text_And_Image_Thresholds(
        int characters, double imageRatio, bool expected)
    {
        // Arrange
        var state = State(characters, imageRatio);
        var provider = Provider();
        var processor = new OcrProcessor(NullLogger<OcrProcessor>.Instance, provider.Object);

        // Act
        await processor.EnrichAsync(state);

        // Assert
        provider.Verify(p => p.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<int>>(),
            It.IsAny<CancellationToken>()), expected ? Times.Once() : Times.Never());
        Assert.Equal(expected ? "Ocr" : "EmbeddedText", state.Pages[0].TextSource);
    }

    [Theory]
    [InlineData(OcrMode.Always, true)]
    [InlineData(OcrMode.Never, false)]
    public async Task Explicit_Ocr_Modes_Override_Automatic_Page_Selection(OcrMode mode, bool expected)
    {
        // Arrange
        var state = State(100, 0);
        state.Tenant.Ocr.Mode = mode;
        var provider = Provider();

        // Act
        await new OcrProcessor(NullLogger<OcrProcessor>.Instance, provider.Object).EnrichAsync(state);

        // Assert
        provider.Verify(p => p.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<int>>(),
            It.IsAny<CancellationToken>()), expected ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task Missing_Ocr_Configuration_Does_Not_Fabricate_Scanned_Text()
    {
        // Arrange
        var state = State(0, 1);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OcrProcessor(NullLogger<OcrProcessor>.Instance).EnrichAsync(state));
        Assert.Empty(state.Pages[0].Words);
        Assert.Empty(state.Pages[0].BodyText);
    }

    [Fact]
    public async Task An_Actual_Image_Only_Pdf_Flows_Through_Recognition_Splitting_And_Persisted_Layout()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "DocIntel.OcrWorkflow.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var pdf = PageEvidenceTests.PdfWithRotation(0, imageOnly: true);
            var artifacts = new LocalFileArtifactStore(directory);
            var provider = Provider();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(provider.Object).AddSingleton<IArtifactStore>(artifacts);
            services.AddDocIntelCore();
            using var application = services.BuildServiceProvider();
            var state = new SplittingState
            {
                Tenant = new TenantHarness
                {
                    TenantId = "tenant",
                    DisplayName = "Test",
                    Capabilities = new()
                    {
                        [FeatureHarness.SplitAndGroupKey] = new() { Enabled = true, SplitAndGroup = new() }
                    }
                },
                FileName = "scanned.pdf",
                DocumentId = "one-a1",
                SourcePdfBytes = pdf
            };

            // Act
            var completed = await application.GetRequiredService<DocumentSplittingWorkflow>().ExecuteAsync(state);

            // Assert
            Assert.Contains("Recognized", Assert.Single(completed.Slices).BodyText);
            Assert.Equal("Ocr", completed.Pages[0].TextSource);
            Assert.False(completed.HasDevelopmentResults);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pdf));
            var layout = await artifacts.GetLayoutAsync("tenant", "one-a1", completed.LayoutSha256!, hash);
            Assert.Equal("Recognized", Assert.Single(Assert.Single(layout!.Pages).Words).Text);
            provider.Verify(p => p.RecognizeAsync(It.Is<byte[]>(bytes => bytes.SequenceEqual(pdf)),
                It.Is<IReadOnlyList<int>>(pages => pages.SequenceEqual(new[] { 1 })),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Recognition_Reuses_Footer_And_Pagination_Rules_Without_Inflating_Zero_Confidence()
    {
        // Arrange
        var state = State(0, 1);
        var provider = Provider();
        provider.Setup(p => p.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OcrPageResult(1, 8.5, 11,
            [
                Word("Agreement", 0, 0.1, 0.1, 0.2, 0.12),
                Word("MBSA", 1, 0.1, 0.94, 0.2, 0.96),
                Word("Page 1 of 2", 2, 0.3, 0.94, 0.5, 0.96)
            ], "ScriptedOCR")]);

        // Act
        await new OcrProcessor(NullLogger<OcrProcessor>.Instance, provider.Object).EnrichAsync(state);

        // Assert
        Assert.Equal("Agreement", state.Pages[0].BodyText);
        Assert.Equal("MBSA", state.Pages[0].FooterText);
        Assert.Equal(1, state.Pages[0].SubDocPageNumber);
        Assert.Equal(2, state.Pages[0].SubDocTotalPages);
        Assert.All(state.Pages[0].Words, word => Assert.Equal(0, word.OcrConfidence));
        Assert.Equal("ScriptedOCR", state.Pages[0].TextProvider);
    }

    [Fact]
    public async Task A_Provider_Error_Is_Not_Replaced_By_Simulated_Or_Native_Success()
    {
        // Arrange
        var state = State(0, 1);
        var provider = Provider();
        provider.Setup(p => p.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "Unavailable."));

        // Act / Assert
        await Assert.ThrowsAsync<RequestFailedException>(() =>
            new OcrProcessor(NullLogger<OcrProcessor>.Instance, provider.Object).EnrichAsync(state));
        Assert.Equal("EmbeddedText", state.Pages[0].TextSource);
    }

    [Fact]
    public async Task Invalid_Provider_Page_Geometry_Does_Not_Publish_Partial_Recognition()
    {
        // Arrange
        var state = State(0, 1);
        var originalPage = state.Pages[0];
        var provider = Provider();
        provider.Setup(p => p.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OcrPageResult(1, 11, 8.5, [Word("Wrong orientation")], "ScriptedOCR")]);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OcrProcessor(NullLogger<OcrProcessor>.Instance, provider.Object).EnrichAsync(state));
        Assert.Same(originalPage, state.Pages[0]);
        Assert.Empty(state.Pages[0].Words);
    }

    [Fact]
    public async Task Cancellation_Is_Observed_Before_Provider_Invocation()
    {
        // Arrange
        var state = State(0, 1);
        var provider = Provider();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OcrProcessor(NullLogger<OcrProcessor>.Instance, provider.Object).EnrichAsync(state, canceled.Token));
        provider.Verify(p => p.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<int>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Azure_Uses_Original_Bytes_And_Explicit_Page_Selection_With_Actual_Scores()
    {
        // Arrange
        var client = new Mock<DocumentIntelligenceClient>();
        var result = DocumentIntelligenceModelFactory.AnalyzeResult(
            apiVersion: "2024-11-30", modelId: "prebuilt-read",
            pages: [AzurePage(1), AzurePage(3)]);
        var operation = new Mock<Operation<AnalyzeResult>>();
        operation.SetupGet(o => o.Value).Returns(result);
        AnalyzeDocumentOptions? captured = null;
        client.Setup(c => c.AnalyzeDocumentAsync(WaitUntil.Completed, It.IsAny<AnalyzeDocumentOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<WaitUntil, AnalyzeDocumentOptions, CancellationToken>((_, options, _) => captured = options)
            .ReturnsAsync(operation.Object);
        var source = "%PDF-original bytes"u8.ToArray();

        // Act
        var pages = await new AzureDocumentIntelligenceOcrProvider(client.Object).RecognizeAsync(source, [3, 1]);

        // Assert
        Assert.Equal("1,3", captured!.Pages);
        Assert.Equal("prebuilt-read", captured.ModelId);
        Assert.Equal(source, captured.BytesSource.ToArray());
        var word = Assert.Single(pages[0].Words);
        Assert.Equal("w_1_0", word.Id);
        Assert.Equal(0.77, word.OcrConfidence!.Value, precision: 6);
        Assert.Equal(0.1, word.Box.Polygon[0], precision: 6);
        Assert.Equal(0.1, word.Box.Polygon[1], precision: 6);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Azure_Rejects_Missing_Pages_Or_Malformed_Geometry(bool missingPage)
    {
        // Arrange
        var client = new Mock<DocumentIntelligenceClient>();
        var result = DocumentIntelligenceModelFactory.AnalyzeResult(
            apiVersion: "2024-11-30", modelId: "prebuilt-read",
            pages: missingPage ? [] : [AzurePage(1, width: null)]);
        var operation = new Mock<Operation<AnalyzeResult>>();
        operation.SetupGet(o => o.Value).Returns(result);
        client.Setup(c => c.AnalyzeDocumentAsync(WaitUntil.Completed, It.IsAny<AnalyzeDocumentOptions>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(operation.Object);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AzureDocumentIntelligenceOcrProvider(client.Object).RecognizeAsync("%PDF-source"u8.ToArray(), [1]));
    }

    [Fact]
    public void Invalid_Ocr_Endpoints_And_Unknown_Policies_Are_Rejected()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAI:ContentUnderstandingEndpoint"] = "http://untrusted.invalid/"
        }).Build();

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddDocIntelCore(configuration));
        Assert.Throws<ArgumentException>(() => new OcrPolicy { Mode = (OcrMode)99 }.Validate());
    }

    private static DocumentPage AzurePage(int page, float? width = 8.5f) =>
        DocumentIntelligenceModelFactory.DocumentPage(pageNumber: page, width: width, height: 11,
            words: [DocumentIntelligenceModelFactory.DocumentWord(content: "Customer",
                polygon: [0.85f, 1.1f, 1.7f, 1.1f, 1.7f, 1.32f, 0.85f, 1.32f], confidence: 0.77f)]);

    private static PageWord Word(string text, int index = 0,
        double left = 0.1, double top = 0.1, double right = 0.2, double bottom = 0.12) =>
        new($"w_1_{index}", text, new BoundingBox(1, [left, top, right, top, right, bottom, left, bottom]), 0);

    private static Mock<IOcrProvider> Provider()
    {
        var provider = new Mock<IOcrProvider>();
        provider.SetupGet(p => p.Name).Returns("ScriptedOCR");
        provider.Setup(p => p.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OcrPageResult(1, 8.5, 11, [Word("Recognized")], "ScriptedOCR")]);
        return provider;
    }

    private static SplittingState State(int characters, double imageRatio) => new()
    {
        Tenant = new TenantHarness { TenantId = "tenant", DisplayName = "Test" },
        FileName = "source.pdf",
        DocumentId = "one-a1",
        SourcePdfBytes = "%PDF-original"u8.ToArray(),
        Pages = [new PageLayout
        {
            PageNumber = 1, PageWidth = 612, PageHeight = 792, ImageAreaRatio = imageRatio,
            BodyText = new string('x', characters), TextSource = "EmbeddedText", TextProvider = "PdfPig"
        }]
    };
}
