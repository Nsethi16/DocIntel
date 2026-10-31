using DocIntel.Core.Harness;
using Xunit;

namespace DocIntel.Tests;

public sealed class DocumentCatalogValidationTests
{
    [Theory]
    [InlineData("invoice")]
    [InlineData(" Invoice ")]
    public void Duplicate_Identification_Names_Are_Rejected_Before_Accepting_A_Harness(string duplicate)
    {
        // Arrange
        var tenant = new TenantHarness { TenantId = "workspace", DisplayName = "Workspace" };
        tenant.Capabilities[FeatureHarness.IdentifyDocumentsKey].IdentifyDocuments = new()
        {
            Rules = [new() { DocumentName = "Invoice", IdentificationPrompt = "Invoice header" },
                new() { DocumentName = duplicate, IdentificationPrompt = "Other invoice header" }]
        };

        // Act
        var error = tenant.ValidateAndNormalizeDocumentCatalog();

        // Assert
        Assert.NotNull(error);
        Assert.Contains("Identification rules", error);
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData(" Invoice ")]
    public void Duplicate_Data_Profile_Names_Are_Rejected_Before_Accepting_A_Harness(string duplicate)
    {
        // Arrange
        var tenant = new TenantHarness { TenantId = "workspace", DisplayName = "Workspace" };
        tenant.Capabilities[FeatureHarness.DataExtractionKey].DataExtraction = new()
            { Profiles = [new() { DocumentName = "Invoice" }, new() { DocumentName = duplicate }] };

        // Act
        var error = tenant.ValidateAndNormalizeDocumentCatalog();

        // Assert
        Assert.NotNull(error);
        Assert.Contains("Data extraction profiles", error);
    }

    [Fact]
    public void Accepted_Document_Keys_Are_Consistent_Across_Identification_And_Extraction()
    {
        // Arrange
        var tenant = new TenantHarness { TenantId = "workspace", DisplayName = "Workspace" };
        tenant.Capabilities[FeatureHarness.IdentifyDocumentsKey].IdentifyDocuments = new()
            { Rules = [new() { DocumentName = " Invoice ", IdentificationPrompt = "Invoice header" }] };
        tenant.Capabilities[FeatureHarness.DataExtractionKey].DataExtraction = new()
            { Profiles = [new() { DocumentName = " Invoice " }] };

        // Act
        var error = tenant.ValidateAndNormalizeDocumentCatalog();

        // Assert
        Assert.Null(error);
        Assert.Equal("Invoice", Assert.Single(tenant.Capabilities[FeatureHarness.IdentifyDocumentsKey]
            .IdentifyDocuments!.Rules).DocumentName);
        Assert.Equal("Invoice", Assert.Single(tenant.Capabilities[FeatureHarness.DataExtractionKey]
            .DataExtraction!.Profiles).DocumentName);
    }
}
