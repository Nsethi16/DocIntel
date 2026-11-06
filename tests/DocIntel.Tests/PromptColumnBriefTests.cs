using DocIntel.Core.Features.PromptColumns;
using DocIntel.Core.Harness;
using Xunit;

namespace DocIntel.Tests;

public sealed class PromptColumnBriefTests
{
    private const string Source = "The approved order totals 158,400.00 USD. Privacy approval is pending.";
    private const string Brief =
        "- **Answer:** Approved order value is 158,400.00 USD.\n" +
        "- **Evidence:** \"The approved order totals 158,400.00 USD.\"\n" +
        "- **Next:** Procurement should confirm amendment coverage.";

    [Fact]
    public void Defaults_And_Reusable_Presets_Share_The_Same_Bounded_Direct_Answer_Contract()
    {
        // Arrange
        var tenant = new TenantHarness { TenantId = "one", DisplayName = "One" };
        var columns = tenant.Capabilities[FeatureHarness.PromptColumnsKey].PromptColumns!.Columns;

        // Act / Assert
        Assert.Equal(PromptColumnBriefs.SummaryInstructions, columns[0].PromptInstructions);
        Assert.Equal(PromptColumnBriefs.HighlightsInstructions, columns[1].PromptInstructions);
        Assert.Equal(PromptColumnBriefs.ContractInstructions, columns[2].PromptInstructions);
        Assert.Equal("Or", columns[2].FilterMode);
        Assert.Equal(2, columns[2].EntityFilters.Count);
        Assert.Equal(6, PromptColumnBriefs.Templates.Count);
        Assert.All(PromptColumnBriefs.Templates, template =>
        {
            Assert.Contains("at most 60 words", template.Instructions);
            Assert.Contains("**Answer:**", template.Instructions);
            Assert.Contains("**Evidence:**", template.Instructions);
            Assert.Contains("**Next:**", template.Instructions);
            Assert.Contains("do not infer facts from another file", template.Instructions);
        });
        Assert.Contains("not proof of an executed", PromptColumnBriefs.ContractInstructions);
        Assert.Contains("without a supplied baseline", PromptColumnBriefs.ExceptionsInstructions);
    }

    [Fact]
    public void A_Short_Direct_Answer_With_Verbatim_Source_Evidence_Is_Accepted()
    {
        // Arrange / Act
        var error = PromptColumnBriefs.GetError(Brief, Source);

        // Assert
        Assert.Null(error);
    }

    [Theory]
    [InlineData("- **Answer:** One.\n- **Next:** Two.", "PromptNotConcise")]
    [InlineData("Introduction.\n- **Answer:** One.\n- **Evidence:** Not stated in this document.\n- **Next:** Confirm.", "PromptNotConcise")]
    [InlineData("- **Answer:** Approved.\n- **Evidence:** \"The contract was fully executed.\"\n- **Next:** Confirm.", "PromptEvidenceMismatch")]
    [InlineData("- **Answer:** Approved.\n- **Evidence:** \"   \"\n- **Next:** Confirm.", "PromptEvidenceMismatch")]
    [InlineData("- **Answer:** Approved.\n- **Evidence:** \"158,400.00\"\n- **Next:** Confirm.", "PromptEvidenceMismatch")]
    public void Unsupported_Formats_And_Invented_Quotations_Are_Explicitly_Rejected(string text, string expected)
    {
        // Arrange / Act
        var error = PromptColumnBriefs.GetError(text, Source);

        // Assert
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Long_Output_Is_Rejected_Not_Clipped()
    {
        // Arrange
        var text = Brief + " " + String.Join(" ", Enumerable.Repeat("extra", 61));

        // Act
        var error = PromptColumnBriefs.GetError(text, Source);

        // Assert
        Assert.Equal("PromptNotConcise", error);
        Assert.EndsWith("extra", text);
    }

    [Theory]
    [InlineData("\"Not stated in this document.\"", "Not stated in this document.")]
    [InlineData("\u201cNot stated in this document.\u201d", "Not stated in this document.")]
    [InlineData("\"A fabricated source excerpt.\"", "\"A fabricated source excerpt.\"")]
    public void Only_The_Explicit_Missing_Evidence_Marker_Is_Normalized(string value, string expected)
    {
        // Arrange
        var text = $"- **Answer:** Approval not evidenced.\n- **Evidence:** {value}\n- **Next:** Confirm with Contracts.";

        // Act
        var normalized = PromptColumnBriefs.NormalizeMissingEvidence(text);

        // Assert
        Assert.Contains("- **Evidence:** " + expected, normalized);
    }
}
