using DocIntel.Core.Features.AskAi;
using Xunit;

namespace DocIntel.Tests;

public sealed class AskAiAnalysisTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Quote QA-001 Customer Contoso Amount 100.00 USD")]
    [InlineData("Quote QA-001; Customer Contoso; Amount 100.00 USD.")]
    public void Explicit_Record_Ids_Allow_Repeated_Headers_And_Deduplicate_Identical_Document_Copies(string quote)
    {
        // Arrange
        const string header = "Quote QA-001; Customer Contoso; Amount 100.00 USD.";
        var evidence = new Dictionary<string, AskAiAnalysisEvidence>
        {
            ["e1"] = new(header + "\nPage two repeats:\n" + header, "source-A"),
            ["e2"] = new(header + "\nA different physical copy.", "source-B")
        };
        var request = new AskAiAnalysisRequest
        {
            Title = "Quote totals", Operation = "sum", Visualization = "bar", GroupBy = "customer",
            ValueColumn = "amount", RecordIdColumn = "quote_id",
            Columns =
            [
                new() { Key = "customer", Label = "Customer", Kind = "text" },
                new() { Key = "amount", Label = "Amount", Kind = "number" },
                new() { Key = "quote_id", Label = "Quote ID", Kind = "text" }
            ],
            Rows = new[] { "e1", "e2" }.Select(id => new AskAiAnalysisInputRow
            {
                EvidenceId = id, SourceQuote = quote, Unit = "USD",
                Cells = new() { ["customer"] = "Contoso", ["amount"] = "100.00", ["quote_id"] = "QA-001" }
            }).ToList()
        };

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", request, evidence);

        // Assert
        Assert.Equal("bar", result.Visualization);
        Assert.Equal(100m, Assert.Single(result.Rows).Cells["value"]);
        Assert.Equal(1, result.Coverage.RecordsIncluded);
        Assert.Equal(1, result.Coverage.DuplicatesExcluded);
        Assert.Equal(100m, Assert.Single(result.Rows).Cells["sharePercent"]);
    }

    [Fact]
    public void Repeated_Headers_Remain_Ambiguous_Without_An_Explicit_Record_Identifier()
    {
        // Arrange
        var fixture = Invoices("sum", "bar", "supplier", ("Contoso", "100.00", "USD", "2026-01-01"));
        fixture.Evidence["e1"] = fixture.Evidence["e1"] with
        {
            SourceText = fixture.Evidence["e1"].SourceText + "\n" + fixture.Evidence["e1"].SourceText
        };

        // Act / Assert
        Assert.Throws<ArgumentException>(() => AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence));
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("missing-id")]
    [InlineData("group-as-id")]
    [InlineData("unit-as-id")]
    public void Declared_Record_Ids_Cannot_Merge_Conflicting_Facts_Or_Use_A_Customer_Or_Unit_As_Identity(string issue)
    {
        // Arrange
        var evidence = new Dictionary<string, AskAiAnalysisEvidence>
        {
            ["e1"] = new("Quote QA-001; Customer Contoso; Amount 100.00 USD.", "source-A"),
            ["e2"] = new("Quote QA-001; Customer Contoso; Amount 150.00 USD.", "source-B")
        };
        var request = new AskAiAnalysisRequest
        {
            Title = "Quote totals", Operation = "sum", Visualization = "bar", GroupBy = "customer",
            ValueColumn = "amount", RecordIdColumn = issue == "group-as-id" ? "customer" : "quote_id",
            Columns =
            [
                new() { Key = "customer", Label = "Customer", Kind = "text" },
                new() { Key = "amount", Label = "Amount", Kind = "number" },
                new() { Key = "quote_id", Label = "Quote ID", Kind = "text" }
            ],
            Rows = new[] { "e1", "e2" }.Select(id => new AskAiAnalysisInputRow
            {
                EvidenceId = id, SourceQuote = String.Empty, Unit = "USD",
                Cells = new() { ["customer"] = "Contoso", ["amount"] = id == "e1" ? "100.00" : "150.00",
                    ["quote_id"] = issue == "missing-id" ? null : issue == "unit-as-id" ? "USD" : "QA-001" }
            }).ToList()
        };

        // Act / Assert
        Assert.Throws<ArgumentException>(() => AskAiAnalysisEngine.Compute("a1", request, evidence));
    }

    [Fact]
    public void Rejected_Analysis_Diagnostics_Never_Expose_Unexpected_Exception_Content()
    {
        // Arrange
        var exception = new ArgumentException("private source excerpt or amount");

        // Act
        var code = AskAiAnalysisEngine.RejectionCode(exception);

        // Assert
        Assert.Equal("AnalysisInputRejected", code);
        Assert.DoesNotContain("private", code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Contoso's invoice dated 2026-01-01 is for 100.00 USD.")]
    public void Exact_Source_Values_Can_Locate_Their_Own_Verbatim_Span_Without_A_Model_Retyped_Paragraph(string hint)
    {
        // Arrange
        var fixture = Invoices("sum", "bar", "supplier", ("Contoso", "100.00", "USD", "2026-01-01"));
        var row = fixture.Request.Rows[0];
        fixture.Request.Rows[0] = new()
        {
            EvidenceId = row.EvidenceId, SourceQuote = hint, Cells = row.Cells, Unit = row.Unit
        };

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Equal("bar", result.Visualization);
        Assert.Equal(100m, Assert.Single(result.Rows).Cells["value"]);
        Assert.Equal(100m, Assert.Single(result.Rows).Cells["sharePercent"]);
        Assert.Contains(result.Notes, note => note.Contains("exact field values"));
        Assert.Equal(["e1"], result.EvidenceIds);
    }

    [Theory]
    [InlineData("credit")]
    [InlineData("substring")]
    [InlineData("wrong-unit")]
    [InlineData("repeated-number")]
    [InlineData("distant")]
    public void Located_Spans_Still_Reject_Lost_Signs_Substrings_Wrong_Units_Ambiguity_And_Distant_Records(string issue)
    {
        // Arrange
        var fixture = Invoices("sum", "bar", "supplier",
            ("Contoso", issue == "credit" ? "-100.00" : issue == "substring" ? "10,000.00" : "100.00",
                "EUR", "2026-01-01"));
        var row = fixture.Request.Rows[0];
        row.Cells["amount"] = issue == "substring" ? "10" : "100.00";
        if (issue == "wrong-unit")
            fixture.Evidence["e1"] = fixture.Evidence["e1"] with
                { SourceText = fixture.Evidence["e1"].SourceText + "\nOther invoice uses USD." };
        if (issue == "repeated-number")
            fixture.Evidence["e1"] = fixture.Evidence["e1"] with
                { SourceText = fixture.Evidence["e1"].SourceText + "\nUnrelated amount 100.00 EUR." };
        if (issue == "distant")
            fixture.Evidence["e1"] = fixture.Evidence["e1"] with
                { SourceText = fixture.Evidence["e1"].SourceText.Replace(
                    "; Amount", "; " + new string('x', 2_100) + " Amount", StringComparison.Ordinal) };
        fixture.Request.Rows[0] = new()
        {
            EvidenceId = row.EvidenceId, SourceQuote = String.Empty, Cells = row.Cells,
            Unit = issue == "wrong-unit" ? "USD" : "EUR"
        };

        // Act / Assert
        Assert.Throws<ArgumentException>(() => AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence));
    }

    [Fact]
    public void A_Located_Numeric_Span_Requires_A_Unique_Nonunit_Text_Anchor()
    {
        // Arrange
        var fixture = Invoices("sum", "bar", "supplier", ("Contoso", "100.00", "USD", "2026-01-01"));
        var row = fixture.Request.Rows[0];
        row.Cells["supplier"] = null;
        fixture.Request.Rows[0] = new()
        {
            EvidenceId = row.EvidenceId, SourceQuote = String.Empty, Cells = row.Cells, Unit = row.Unit
        };

        // Act / Assert
        Assert.Throws<ArgumentException>(() => AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence));
    }

    [Fact]
    public void Rejected_Values_Identify_Their_Column_For_Repair_Without_Silently_Changing_The_Amount()
    {
        // Arrange
        var fixture = Invoices("sum", "bar", "supplier", ("Contoso", "100.00", "USD", "2026-01-01"));
        var row = fixture.Request.Rows[0];
        row.Cells["amount"] = "999.00";
        fixture.Request.Rows[0] = new()
        {
            EvidenceId = row.EvidenceId, SourceQuote = String.Empty, Cells = row.Cells, Unit = row.Unit
        };

        // Act
        var error = Assert.Throws<ArgumentException>(() =>
            AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence));

        // Assert
        Assert.Equal("AnalysisCell", AskAiAnalysisEngine.RejectionCode(error));
        Assert.Equal("e1", error.Data["AnalysisEvidenceId"]);
        Assert.Equal("amount", error.Data["AnalysisColumn"]);
        Assert.DoesNotContain("999.00", error.Message);
        Assert.DoesNotContain("Contoso", error.Message);
    }

    [Theory]
    [InlineData("-100.00")]
    [InlineData("(100.00)")]
    [InlineData("- 100.00")]
    [InlineData("\u2212100.00")]
    [InlineData("1 100.00")]
    public void Numeric_Cells_Cannot_Drop_Credit_Markers_Or_Separated_Thousands(string sourceAmount)
    {
        // Arrange
        var fixture = Invoices("sum", "table", "supplier", ("Contoso", sourceAmount, "USD", "2026-01-01"));
        fixture.Request.Rows[0].Cells["amount"] = "100.00";

        // Act / Assert
        Assert.Throws<ArgumentException>(() => AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence));
    }

    [Theory]
    [InlineData("-100.00")]
    [InlineData("(100.00)")]
    [InlineData("- 100.00")]
    [InlineData("\u2212100.00")]
    public void Complete_Quoted_Credit_Amounts_Retain_Their_Negative_Value(string sourceAmount)
    {
        // Arrange
        var fixture = Invoices("sum", "table", "supplier", ("Credit", sourceAmount, "USD", "2026-01-01"));

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Equal(-100m, Assert.Single(result.Rows).Cells["value"]);
    }

    [Fact]
    public void Outlier_Quartiles_Do_Not_Combine_Case_Sensitive_Physical_Units()
    {
        // Arrange
        var fixture = Invoices("outliers", "table", "supplier",
            ("Reading 1", "1", "mW", "2026-01-01"),
            ("Reading 2", "1", "mW", "2026-01-02"),
            ("Reading 3", "1", "mW", "2026-01-03"),
            ("Reading 4", "1000", "MW", "2026-01-04"));

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Empty(result.Rows);
        Assert.Contains(result.Notes, note => note.Contains("Insufficient records for outlier detection in mW."));
        Assert.Contains(result.Notes, note => note.Contains("Insufficient records for outlier detection in MW."));
    }

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
    public void Shares_Are_Computed_Against_The_Same_Unit_Total_Without_Currency_Conversion()
    {
        // Arrange
        var fixture = Invoices("sum", "table", "supplier",
            ("Contoso", "100", "USD", "2026-01-01"),
            ("Woodgrove", "300", "USD", "2026-01-02"),
            ("Contoso", "40", "EUR", "2026-01-03"));

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Equal(25m, result.Rows.Single(row => (string)row.Cells["category"]! == "Contoso" &&
            (string)row.Cells["unit"]! == "USD").Cells["sharePercent"]);
        Assert.Equal(75m, result.Rows.Single(row => (string)row.Cells["category"]! == "Woodgrove").Cells["sharePercent"]);
        Assert.Equal(100m, result.Rows.Single(row => (string)row.Cells["unit"]! == "EUR").Cells["sharePercent"]);
    }

    [Fact]
    public void Credits_Do_Not_Produce_Misleading_Positive_Share_Percentages()
    {
        // Arrange
        var fixture = Invoices("sum", "table", "supplier",
            ("Contoso", "100", "USD", "2026-01-01"), ("Credit", "-50", "USD", "2026-01-02"));

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.All(result.Rows, row => Assert.Null(row.Cells["sharePercent"]));
        Assert.Contains(result.Notes, note => note.Contains("credits or non-positive totals"));
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
        var fixture = Invoices("sum", "bar", "supplier", ("Contoso", "10,000.00", "USD", "2026-01-01"));
        fixture.Request.Rows[0].Cells["amount"] = "10";

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

    [Theory]
    [InlineData("100.00", "USD", "Supplier Contoso; Date 2026-01-01; Unit USD; Amount 100.00.")]
    [InlineData("100ms", "ms", "Supplier Contoso; Date 2026-01-01; Measurement 100ms.")]
    public void Literal_Numbers_With_Sentence_Punctuation_And_Attached_Units_Remain_Usable(
        string amount, string unit, string quote)
    {
        // Arrange
        var fixture = Invoices("sum", "bar", "supplier", ("Contoso", amount, unit, "2026-01-01"));
        var originalRow = fixture.Request.Rows[0];
        fixture.Evidence["e1"] = new(quote, "source");
        fixture.Request.Rows[0] = new()
        {
            EvidenceId = "e1", SourceQuote = quote, Unit = unit, Cells = originalRow.Cells
        };

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Equal(100m, Assert.Single(result.Rows).Cells["value"]);
    }

    [Fact]
    public void Case_Sensitive_Physical_Units_Cannot_Be_Relabeled()
    {
        // Arrange
        var fixture = Invoices("sum", "bar", "supplier", ("Contoso", "100", "mW", "2026-01-01"));
        var row = fixture.Request.Rows[0];
        fixture.Request.Rows[0] = new()
        {
            EvidenceId = row.EvidenceId, SourceQuote = row.SourceQuote, Cells = row.Cells, Unit = "MW"
        };

        // Act / Assert
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

    [Theory]
    [InlineData("line")]
    [InlineData("bar")]
    public void Mixed_Unit_Analyses_Return_An_Explicit_Table_Rather_Than_A_Misleading_Chart(string visualization)
    {
        // Arrange
        var fixture = Invoices("sum", visualization, "date",
            ("Contoso", "100.00", "USD", "2026-01-01"),
            ("Contoso", "150.00", "EUR", "2026-03-01"));

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Equal("table", result.Visualization);
        Assert.Contains(result.Notes, note => note.Contains("chart size or unit constraints"));
    }

    [Fact]
    public void Non_Positive_Baselines_Do_Not_Produce_A_Misleading_Percentage_Change()
    {
        // Arrange
        var fixture = Invoices("sum", "line", "date",
            ("Contoso", "0", "USD", "2026-01-01"), ("Contoso", "150", "USD", "2026-03-01"));

        // Act
        var result = AskAiAnalysisEngine.Compute("a1", fixture.Request, fixture.Evidence);

        // Assert
        Assert.Contains(result.Notes, note => note.Contains("non-positive baseline"));
        Assert.Contains(result.Notes, note => note.Contains("missing periods are not filled with zero"));
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
        Assert.Equal("USD", result.Rows[0].Cells["unit"]);
        Assert.Contains(result.Columns, column => column.Key == "unit");
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
