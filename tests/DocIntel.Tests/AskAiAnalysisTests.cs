using DocIntel.Core.Features.AskAi;
using Xunit;

namespace DocIntel.Tests;

public sealed class AskAiAnalysisTests
{
    [Theory]
    [InlineData("sum", 150)]
    [InlineData("average", 75)]
    [InlineData("min", 50)]
    [InlineData("max", 100)]
    public void Arithmetic_Is_Deterministic_And_Different_Currencies_Are_Never_Added_Together(
        string operation, decimal expected)
    {
        // Arrange
        var fixture = Invoices(operation, "table", "supplier",
            ("Contoso", "100.00", "USD", "2026-01-01"),
            ("Contoso", "50.00", "USD", "2026-01-02"),
            ("Contoso", "40.00", "EUR", "2026-01-03"));

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(expected, result.Rows.Single(row => (string)row.Cells["unit"]! == "USD").Cells["value"]);
        Assert.Equal(40m, result.Rows.Single(row => (string)row.Cells["unit"]! == "EUR").Cells["value"]);
        Assert.Equal(3, result.Coverage.RecordsIncluded);
        Assert.Contains(result.Notes, note => note.Contains("not a certified complete-workspace total"));
    }

    [Fact]
    public void Missing_Amounts_Are_Excluded_And_Disclosed_Instead_Of_Becoming_Zero()
    {
        // Arrange
        var fixture = Invoices("average", "table", "supplier",
            ("Contoso", "100.00", "USD", "2026-01-01"),
            ("Contoso", "", "USD", "2026-01-02"));

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Equal(100m, Assert.Single(result.Rows).Cells["value"]);
        Assert.Equal(1, result.Coverage.MissingValues);
        Assert.Equal(1, result.Coverage.RecordsIncluded);
        Assert.Contains(result.Notes, note => note.Contains("no missing value became zero"));
    }

    [Fact]
    public void Repeated_Inspections_Of_The_Same_Source_Record_Do_Not_Inflate_Totals()
    {
        // Arrange
        var fixture = Invoices("sum", "bar", "supplier", ("Contoso", "100.00", "USD", "2026-01-01"));
        fixture.Request.Rows.Add(fixture.Request.Rows[0]);

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Equal(100m, Assert.Single(result.Rows).Cells["value"]);
        Assert.Equal(1, result.Coverage.DuplicatesExcluded);
        Assert.Equal(1, result.Coverage.RecordsIncluded);
    }

    [Fact]
    public void Numeric_Substrings_Cannot_Fabricate_A_Different_Amount()
    {
        // Arrange
        var fixture = Invoices("sum", "bar", "supplier", ("Contoso", "1,000.00", "USD", "2026-01-01"));
        fixture.Request.Rows[0].Cells["amount"] = "1";

        // Act / Assert
        Assert.Throws<ArgumentException>(() =>
            AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence));
    }

    [Fact]
    public void Values_Outside_The_Quoted_Record_And_Uninspected_Evidence_Are_Rejected()
    {
        // Arrange
        var fixture = Invoices("sum", "bar", "supplier", ("Contoso", "100.00", "USD", "2026-01-01"));
        fixture.Request.Rows[0].Cells["amount"] = "999.00";

        // Act / Assert
        Assert.Throws<ArgumentException>(() =>
            AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence));
        fixture.Evidence.Clear();
        Assert.Throws<ArgumentException>(() =>
            AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence));
    }

    [Fact]
    public void Ambiguous_Currency_Symbols_Are_Not_Guessed_As_US_Dollars()
    {
        // Arrange
        var fixture = Invoices("sum", "bar", "supplier", ("Contoso", "100.00", "$", "2026-01-01"));

        // Act / Assert
        Assert.Throws<ArgumentException>(() =>
            AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence));
    }

    [Fact]
    public void A_Currency_In_Another_Record_Cannot_Relabel_The_Current_Records_Amount()
    {
        // Arrange
        var fixture = Invoices("sum", "bar", "supplier", ("Contoso", "100.00", "EUR", "2026-01-01"));
        var row = fixture.Request.Rows[0];
        fixture.Evidence["e1"] = fixture.Evidence["e1"] with
            { SourceText = fixture.Evidence["e1"].SourceText + "\nOther invoice uses USD." };
        fixture.Request.Rows[0] = new()
        {
            EvidenceId = row.EvidenceId, SourceQuote = row.SourceQuote, Cells = row.Cells, Unit = "USD"
        };

        // Act / Assert
        Assert.Throws<ArgumentException>(() =>
            AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence));
    }

    [Fact]
    public void Different_Quotes_Of_The_Same_Record_Cannot_Double_Count_Identical_Facts()
    {
        // Arrange
        var fixture = Invoices("sum", "bar", "supplier", ("Contoso", "100.00", "USD", "2026-01-01"));
        var row = fixture.Request.Rows[0];
        fixture.Request.Rows.Add(new()
        {
            EvidenceId = row.EvidenceId, SourceQuote = row.SourceQuote["Invoice 1: ".Length..],
            Cells = row.Cells, Unit = row.Unit
        });

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Equal(100m, Assert.Single(result.Rows).Cells["value"]);
        Assert.Equal(1, result.Coverage.DuplicatesExcluded);
    }

    [Fact]
    public void Explicit_Comma_Decimal_Formats_Are_Parsed_Without_Using_The_Host_Locale()
    {
        // Arrange
        var fixture = Invoices("sum", "table", "supplier", ("Contoso", "1.234,56", "EUR", "2026-01-01"));
        fixture.Request.Columns[1] = new()
            { Key = "amount", Label = "Amount", Kind = "number", NumberFormat = "comma-decimal" };

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Equal(1234.56m, Assert.Single(result.Rows).Cells["value"]);
    }

    [Fact]
    public void Date_Grouping_And_Changes_Use_Actual_Dates_And_Server_Calculations()
    {
        // Arrange
        var fixture = Invoices("sum", "line", "date",
            ("Contoso", "100.00", "USD", "2026-01-01"),
            ("Contoso", "150.00", "USD", "2026-03-01"));

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Equal("line", result.Visualization);
        Assert.Equal("2026-01-01", result.Rows[0].Cells["category"]);
        Assert.Equal("2026-03-01", result.Rows[1].Cells["category"]);
        Assert.Contains(result.Notes, note => note == "First-to-last change: 50 USD (50%).");
    }

    [Fact]
    public void Mixed_Unit_Time_Series_Return_An_Explicit_Table_Rather_Than_A_Misleading_Chart()
    {
        // Arrange
        var fixture = Invoices("sum", "line", "date",
            ("Contoso", "100.00", "USD", "2026-01-01"),
            ("Contoso", "150.00", "EUR", "2026-03-01"));

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Equal("table", result.Visualization);
        Assert.Contains(result.Notes, note => note.Contains("chart size or unit constraints"));
    }

    [Fact]
    public void Outliers_Are_Computed_From_A_Defined_Rule_And_Retain_All_Contributing_Evidence()
    {
        // Arrange
        var fixture = Invoices("outliers", "bar", "supplier",
            ("Contoso", "100", "USD", "2026-01-01"), ("Contoso", "100", "USD", "2026-01-02"),
            ("Contoso", "100", "USD", "2026-01-03"), ("Contoso", "1000", "USD", "2026-01-04"));

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Equal(1000m, Assert.Single(result.Rows).Cells["value"]);
        Assert.Equal(4, result.EvidenceIds.Count);
        Assert.Equal(4, result.Coverage.RecordsIncluded);
        Assert.Contains(result.Notes, note => note.Contains("1.5xIQR"));
    }

    [Fact]
    public void Small_Samples_Disclose_That_Outlier_Detection_Cannot_Be_Performed()
    {
        // Arrange
        var fixture = Invoices("outliers", "bar", "supplier", ("Contoso", "100", "USD", "2026-01-01"));

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Empty(result.Rows);
        Assert.Equal("table", result.Visualization);
        Assert.Contains(result.Notes, note => note.Contains("Insufficient records"));
    }

    [Fact]
    public void Source_Tables_Preserve_Missing_Cells_As_Null()
    {
        // Arrange
        var fixture = Invoices("rows", "table", "supplier", ("Contoso", "", "USD", "2026-01-01"));

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Null(Assert.Single(result.Rows).Cells["amount"]);
        Assert.Equal(1, result.Coverage.MissingValues);
    }

    internal static (AskAiAnalysisRequest Request, Dictionary<string, AskAiAnalysisEvidence> Evidence) Invoices(
        string operation, string visualization, string groupBy,
        params (string Supplier, string Amount, string Unit, string Date)[] invoices)
    {
        var evidence = new Dictionary<string, AskAiAnalysisEvidence>(StringComparer.Ordinal);
        var rows = new List<AskAiAnalysisInputRow>();
        for (var index = 0; index < invoices.Length; index++)
        {
            var invoice = invoices[index];
            var id = $"e{index + 1}";
            var quote = $"Invoice {index + 1}: Supplier {invoice.Supplier}; Amount {invoice.Amount} " +
                $"{invoice.Unit}; Date {invoice.Date}.";
            evidence[id] = new(quote, $"source-{index}");
            rows.Add(new()
            {
                EvidenceId = id, SourceQuote = quote, Unit = invoice.Unit,
                Cells = new(StringComparer.Ordinal)
                {
                    ["supplier"] = invoice.Supplier, ["amount"] = invoice.Amount, ["date"] = invoice.Date
                }
            });
        }
        return (new AskAiAnalysisRequest
        {
            Title = "Invoice analysis", Operation = operation, Visualization = visualization, GroupBy = groupBy,
            ValueColumn = "amount", Rows = rows,
            Columns =
            [
                new() { Key = "supplier", Label = "Supplier", Kind = "text" },
                new() { Key = "amount", Label = "Amount", Kind = "number" },
                new() { Key = "date", Label = "Invoice Date", Kind = "date" }
            ]
        }, evidence);
    }
}
