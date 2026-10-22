using Azure;
using Azure.AI.ContentUnderstanding;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Ocr;
using DocIntel.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class ContentUnderstandingTests
{
    [Fact]
    public async Task Native_GA_Layout_Uses_Original_Bytes_And_Preserves_Markdown_Words_And_Geometry()
    {
        // Arrange
        var fixture = new Fixture();
        var source = "%PDF-original"u8.ToArray();

        // Act
        var pages = await fixture.Provider.RecognizeAsync(source, [1]);

        // Assert
        Assert.Equal(source, fixture.Bytes);
        Assert.Equal("1", fixture.Range);
        var page = Assert.Single(pages);
        Assert.Equal("# Agreement\n\n| Name | Value |\n| --- | --- |\n| Company | Contoso |", page.Markdown);
        Assert.Contains("\"apiVersion\":\"2025-11-01\"", page.NativeLayoutJson);
        Assert.Contains("\"source\":\"D(1,", page.NativeLayoutJson);
        var word = Assert.Single(page.Words);
        Assert.Equal("w_1_0", word.Id);
        Assert.Equal(0, word.OcrConfidence);
        Assert.Equal(0.1, word.Box.Polygon[0], 5);
        Assert.True(fixture.Provider.UsesFullLayout);
    }

    [Theory]
    [InlineData("missing-page")]
    [InlineData("wrong-model")]
    [InlineData("warning")]
    [InlineData("polygon")]
    [InlineData("span")]
    [InlineData("encoding")]
    public async Task Incomplete_Or_Untrustworthy_Provider_Results_Do_Not_Create_Successful_Layouts(string issue)
    {
        // Arrange
        var fixture = new Fixture(issue);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Provider.RecognizeAsync("%PDF-original"u8.ToArray(), [1]));
    }

    [Fact]
    public async Task All_Pages_Use_Layout_Irrespective_Of_Legacy_Ocr_Mode_And_Reuse_The_Native_Cache()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "DocIntel.CU.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = new Fixture();
            var artifacts = new LocalFileArtifactStore(directory);
            var usage = new InMemoryOcrUsageStore();
            var processor = new OcrProcessor(NullLogger<OcrProcessor>.Instance, fixture.Provider, artifacts, usage);
            SplittingState State() => new()
            {
                DocumentId = Guid.NewGuid().ToString("N"),
                FileName = "file.pdf",
                Tenant = new TenantHarness { TenantId = "tenant", DisplayName = "Tenant", Ocr = new() { Mode = OcrMode.Never } },
                SourcePdfBytes = "%PDF-original"u8.ToArray(),
                Pages = [new PageLayout { PageNumber = 1, PageWidth = 612, PageHeight = 792, BodyText = "Native text ignored" }]
            };
            var first = State();
            await processor.EnrichAsync(first);
            var next = State();

            // Act
            await processor.EnrichAsync(next);

            // Assert
            Assert.Equal("ContentUnderstandingLayout", first.Pages[0].TextSource);
            Assert.Equal(first.Pages[0].Markdown, next.Pages[0].Markdown);
            Assert.Contains("| Company | Contoso |", next.Pages[0].Markdown);
            Assert.Equal(first.Pages[0].NativeLayoutJson, next.Pages[0].NativeLayoutJson);
            Assert.True(next.Pages[0].OcrCacheHit);
            Assert.Equal("Agreement", first.Pages[0].BodyText);
            Assert.Equal(1, fixture.Calls);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PdfPig_Can_Validate_Page_Geometry_Without_Extracting_A_Competing_Text_Representation()
    {
        // Arrange
        var pdf = PdfPigLayoutExtractor.CreateSamplePdf([("Embedded source text", "Footer")]);

        // Act
        var pages = await new PdfPigLayoutExtractor().ExtractGeometryAsync(pdf, new SplittingConfig());

        // Assert
        var page = Assert.Single(pages);
        Assert.Empty(page.Words);
        Assert.Empty(page.BodyText);
        Assert.Empty(page.Markdown);
        Assert.Equal("PendingContentUnderstanding", page.TextSource);
        Assert.True(page.PageWidth > 0);
    }

    private sealed class Fixture
    {
        public byte[]? Bytes { get; private set; }
        public string? Range { get; private set; }
        public int Calls { get; private set; }
        public AzureContentUnderstandingLayoutProvider Provider { get; }
        public Fixture(string? issue = null)
        {
            const string markdown = "# Agreement\n\n| Name | Value |\n| --- | --- |\n| Company | Contoso |";
            var word = ContentUnderstandingModelFactory.DocumentWord("Agreement",
                issue == "polygon" ? "D(1)" : "D(1,0.85,1.1,1.7,1.1,1.7,1.32,0.85,1.32)",
                ContentUnderstandingModelFactory.ContentSpan(2, 9), 0);
            var page = ContentUnderstandingModelFactory.DocumentPage(1, 8.5f, 11f,
                spans: [ContentUnderstandingModelFactory.ContentSpan(0, issue == "span" ? 1_000_000 : markdown.Length)], words: [word]);
            var content = ContentUnderstandingModelFactory.DocumentContent(
                markdown: markdown, startPageNumber: 1, endPageNumber: 1,
                pages: issue == "missing-page" ? [] : [page]);
            var result = ContentUnderstandingModelFactory.AnalysisResult(
                analyzerId: issue == "wrong-model" ? "other" : "prebuilt-layout", apiVersion: "2025-11-01",
                stringEncoding: issue == "encoding" ? "unknown" : "utf16", contents: [content],
                warnings: issue == "warning" ? [new ResponseError("Incomplete", "A page was skipped")] : []);
            var operation = new Mock<Operation<AnalysisResult>>();
            operation.SetupGet(o => o.Value).Returns(result);
            var client = new Mock<ContentUnderstandingClient>();
            client.Setup(c => c.AnalyzeBinaryAsync(WaitUntil.Completed, "prebuilt-layout", It.IsAny<BinaryData>(),
                    It.IsAny<ContentRange?>(), "application/pdf", It.IsAny<ProcessingLocation?>(), It.IsAny<CancellationToken>()))
                .Callback<WaitUntil, string, BinaryData, ContentRange?, string, ProcessingLocation?, CancellationToken>(
                    (_, _, bytes, range, _, _, _) => { Bytes = bytes.ToArray(); Range = range.ToString(); Calls++; })
                .ReturnsAsync(operation.Object);
            Provider = new AzureContentUnderstandingLayoutProvider(client.Object, new Uri("https://cu.example.services.ai.azure.com/"));
        }
    }
}
