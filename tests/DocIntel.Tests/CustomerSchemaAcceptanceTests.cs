using DocIntel.Api.Diagnostics;
using DocIntel.Core.Features.DocumentSplitting;
using Xunit;

namespace DocIntel.Tests;

public sealed class CustomerSchemaAcceptanceTests
{
    [Theory]
    [InlineData(300, false, false, true)]
    [InlineData(299, false, false, false)]
    [InlineData(300, true, false, false)]
    [InlineData(300, false, true, false)]
    public void Long_Table_Acceptance_Checks_All_Original_Rows_Not_A_Count_Proxy(
        int rows, bool duplicateLast, bool wrongProduct, bool expected)
    {
        // Arrange
        var products = Enumerable.Range(1, rows).Select(number =>
        {
            var actual = duplicateLast && number == rows ? number - 1 : number;
            return new ExtractedTableRow(
            [
                new("PartNumber", $"QA-SKU-{actual:0000}"),
                new("ProductName", wrongProduct && number == rows ? "Invented" : $"Managed service line {actual:000}"),
                new("UnitPrice", "150.00")
            ]);
        }).ToArray();
        var slice = new DocumentSlice
        {
            SegmentIndex = 0, StartPage = 1, EndPage = 12, IdentifiedDocumentName = "Customer Price Sheet",
            DataExtraction = new("Customer Price Sheet", String.Empty, "1-12",
                Fields("QA-LONG-001", "USD", "45,000.00"), [new("SubscriptionPricing", products)])
        };

        // Act
        var checks = CustomerAcceptanceDiagnostic.SchemaAssertions("schema-long", [slice]);

        // Assert
        Assert.Equal(expected ? "Passed" : "Failed", Assert.Single(checks, check => check.Id == "SchemaRows").Status);
        Assert.Equal("Passed", Assert.Single(checks, check => check.Id == "SchemaFields").Status);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("0", false)]
    [InlineData("Not provided", false)]
    public void Unavailable_Monetary_Values_Remain_Empty_Not_Zero_Or_A_Claimed_Numeric_Value(
        string missing, bool expected)
    {
        // Arrange
        var ids = new[] { "QA-CREDIT-001", "QA-POSITIVE-002", "QA-EURO-003", "QA-MISSING-004" };
        var values = new[] { "-500.00", "3,500.00", "2,000.00", missing };
        var slices = ids.Select((id, index) => new DocumentSlice
        {
            SegmentIndex = index, StartPage = index + 1, EndPage = index + 1,
            IdentifiedDocumentName = "Customer Price Sheet",
            DataExtraction = new("Customer Price Sheet", String.Empty, $"{index + 1}-{index + 1}",
                Fields(id, index == 2 ? "EUR" : "USD", values[index]),
                [new("SubscriptionPricing",
                [
                    new([new("PartNumber", "QA-SKU-001"), new("ProductName", "Managed operations subscription"),
                        new("UnitPrice", "150.00")]),
                    new([new("PartNumber", "QA-SKU-002"), new("ProductName", "Reporting and support service"),
                        new("UnitPrice", "75.00")])
                ])])
        }).ToArray();

        // Act
        var checks = CustomerAcceptanceDiagnostic.SchemaAssertions("schema-price", slices);

        // Assert
        Assert.Equal(expected ? "Passed" : "Failed",
            Assert.Single(checks, check => check.Id == "SchemaMissingAmount").Status);
        Assert.Equal("Passed", Assert.Single(checks, check => check.Id == "SchemaFields").Status);
        Assert.Equal("Passed", Assert.Single(checks, check => check.Id == "SchemaRows").Status);
    }

    private static ExtractedFieldValue[] Fields(string quote, string currency, string amount) =>
    [
        new("CustomerName", "QA Alderwind Services LLC"), new("QuoteNumber", quote),
        new("BillingCurrency", currency), new("TotalDealValue", amount)
    ];
}
