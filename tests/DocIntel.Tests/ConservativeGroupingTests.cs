using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using Xunit;

namespace DocIntel.Tests;

public sealed class ConservativeGroupingTests
{
    [Fact]
    public void Continuous_Article_With_Changing_Bottom_Text_Remains_One_Complete_Document()
    {
        // Arrange
        var pages = Enumerable.Range(1, 4).Select(number => new PageLayout
        {
            PageNumber = number, BodyText = $"Somatosensory\nContinuous source page {number}.",
            FooterText = number % 2 == 0 ? $"Ascendingtractmedialpathways{number}" : $"Cerebralrepresentation{number}"
        }).ToArray();

        // Act
        var slices = SliceBoundaryAnalyzer.GroupPagesHeuristically(pages, new SplittingConfig());

        // Assert
        var document = Assert.Single(slices);
        Assert.Equal(1, document.StartPage);
        Assert.Equal(4, document.EndPage);
        Assert.All(pages, page => Assert.False(page.IsAmendment));
        Assert.All(pages, page => Assert.Contains(page.BodyText, document.BodyText));
    }

    [Fact]
    public void Consecutive_Page_Numbering_Prevents_Page_Level_Footer_Identifiers_From_Splitting_An_Article()
    {
        // Arrange
        var pages = Enumerable.Range(1, 4).Select(number => new PageLayout
        {
            PageNumber = number, SubDocPageNumber = number, SubDocTotalPages = 4,
            BodyText = $"Continuous article page {number}", FooterText = $"Posterior-Column-{number}"
        }).ToArray();

        // Act
        var slices = SliceBoundaryAnalyzer.GroupPagesHeuristically(pages, new SplittingConfig());

        // Assert
        Assert.Equal(4, Assert.Single(slices).EndPage);
    }

    [Fact]
    public void Page_Number_Reset_Preserves_Two_Distinct_Instances_Of_The_Same_Form()
    {
        // Arrange
        var pages = Enumerable.Range(1, 4).Select(number => new PageLayout
        {
            PageNumber = number, SubDocPageNumber = (number - 1) % 2 + 1, SubDocTotalPages = 2,
            BodyText = "Invoice", FooterText = "Form-Invoice-v1"
        }).ToArray();

        // Act
        var slices = SliceBoundaryAnalyzer.GroupPagesHeuristically(pages, new SplittingConfig());

        // Assert
        Assert.Equal(2, slices.Count);
        Assert.Equal((1, 2), (slices[0].StartPage, slices[0].EndPage));
        Assert.Equal((3, 4), (slices[1].StartPage, slices[1].EndPage));
    }

    [Fact]
    public void Different_Real_Form_Numbers_Are_Not_Normalized_As_An_OCR_Typo()
    {
        // Arrange
        var pages = new[]
        {
            new PageLayout { PageNumber = 1, FooterText = "Form-ABC-v1", BodyText = "First form" },
            new PageLayout { PageNumber = 2, FooterText = "Form-ABC-v2", BodyText = "Different form" }
        };

        // Act
        var slices = SliceBoundaryAnalyzer.GroupPagesHeuristically(pages, new SplittingConfig());

        // Assert
        Assert.Equal(2, slices.Count);
        Assert.Equal("Form-ABC-v1", pages[0].FooterText);
        Assert.Equal("Form-ABC-v2", pages[1].FooterText);
    }

    [Fact]
    public void Different_Quote_Identifiers_Split_Otherwise_Identical_Form_Types()
    {
        // Arrange
        var pages = new[]
        {
            new PageLayout { PageNumber = 1, FooterText = "Form-Quote-v1", BodyText = "Quote\nQuote Number: Q-001" },
            new PageLayout { PageNumber = 2, FooterText = "Form-Quote-v1", BodyText = "Quote\nQuote Number: Q-002" }
        };

        // Act
        var slices = SliceBoundaryAnalyzer.GroupPagesHeuristically(pages, new SplittingConfig(), ["Quote"]);

        // Assert
        Assert.Equal(2, slices.Count);
        Assert.Equal(1, slices[0].EndPage);
        Assert.Equal(2, slices[1].StartPage);
    }

    [Fact]
    public void Explicit_Catalog_Title_Changes_Split_A_Packet_Without_Footers()
    {
        // Arrange
        var pages = new[]
        {
            new PageLayout { PageNumber = 1, BodyText = "Invoice\nFirst document" },
            new PageLayout { PageNumber = 2, BodyText = "Invoice\nContinuation" },
            new PageLayout { PageNumber = 3, BodyText = "Receipt\nSecond document" }
        };

        // Act
        var slices = SliceBoundaryAnalyzer.GroupPagesHeuristically(pages, new SplittingConfig(), ["Invoice", "Receipt"]);

        // Assert
        Assert.Equal(2, slices.Count);
        Assert.Equal((1, 2), (slices[0].StartPage, slices[0].EndPage));
        Assert.Equal((3, 3), (slices[1].StartPage, slices[1].EndPage));
    }
}
