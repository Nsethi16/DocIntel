using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using Xunit;

namespace DocIntel.Tests;

public sealed class FooterCommentTests
{
    [Theory]
    [InlineData("<!-- PageFooter=\"M130\" -->")]
    [InlineData("<!-- PageFooter = \"M130\" -->")]
    [InlineData("<!-- PageFooter: M130 -->")]
    [InlineData("<!-- PageFooter: \"M130\" -->")]
    [InlineData("<!-- PageFooter M130 -->")]
    [InlineData("<!-- PageFooter\"M130\" -->")]
    public void Footer_Comment_Formats_Preserve_The_Amendment_Identifier(string comment)
    {
        // Arrange
        var page = new PageLayout
        {
            PageNumber = 1, FooterText = "CTM", BodyText = "Amendment details",
            Markdown = "Amendment details\n" + comment
        };

        // Act
        SliceBoundaryAnalyzer.IdentifyAmendmentPages([page], ["CTM"]);

        // Assert
        Assert.True(page.IsAmendment);
        Assert.Equal("M130", page.AmendmentId);
    }

    [Fact]
    public async Task Native_Layout_Recognizes_The_Exact_Quoted_Equals_Comment_It_Emits()
    {
        // Arrange
        var extractor = new PdfPigLayoutExtractor();
        var pdf = PdfPigLayoutExtractor.CreateSamplePdf(
            [("Amendment details\n<!-- PageFooter=\"M130\" -->", String.Empty)]);

        // Act
        var pages = await extractor.ExtractPagesAsync(pdf, new SplittingConfig());
        var page = Assert.Single(pages);

        // Assert
        Assert.Equal("M130", page.FooterText);
        Assert.Contains("<!-- PageFooter=\"M130\" -->", page.Markdown);
    }

    [Fact]
    public async Task Emitted_Footer_Metadata_Is_Read_By_Amendment_Grouping_Without_Raw_Footer_Fallback()
    {
        // Arrange
        var extractor = new PdfPigLayoutExtractor();
        var pdf = PdfPigLayoutExtractor.CreateSamplePdf([("Amendment details", "M130")]);
        var page = Assert.Single(await extractor.ExtractPagesAsync(pdf, new SplittingConfig()));
        page.FooterText = "CTM";

        // Act
        SliceBoundaryAnalyzer.IdentifyAmendmentPages([page], ["CTM"]);

        // Assert
        Assert.Equal("M130", page.AmendmentId);
    }
}
