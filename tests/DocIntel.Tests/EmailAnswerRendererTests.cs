using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocIntel.Core.Channels;
using DocIntel.Core.Features.AskAi;
using Xunit;

namespace DocIntel.Tests;

public sealed class EmailAnswerRendererTests
{
    [Fact]
    public void Email_Starts_With_The_Answer_Without_A_Disclaimer_Question_Or_Agent_Banner()
    {
        // Arrange
        var run = Run();
        run.Answer = "## Summary\n\n**Tailwind** is the largest contributor [1].\n\nData is incomplete.";

        // Act
        var html = EmailAnswerRenderer.Render(run, "https://docintel.example");

        // Assert
        Assert.Contains("<h2 style=\"font-size:18px;margin:22px 0 10px;\">Summary</h2>", html);
        Assert.Contains("<strong>Tailwind</strong>", html);
        Assert.Contains("Data is incomplete.", html);
        Assert.DoesNotContain(run.Question, html);
        Assert.DoesNotContain("Automatic answer", html);
        Assert.DoesNotContain("Original email question", html);
        Assert.DoesNotContain("Q:", html);
        Assert.Contains("id=\"source-1\"", html);
        Assert.Contains("href=\"#source-1\"", html);
        Assert.Contains("https://docintel.example/api/harness/tenants/workspace/jobs/source/slices/0/pdf", html);
        Assert.Contains("https://docintel.example/ask-run.html?tenant=workspace&amp;run=example", html);
    }

    [Fact]
    public void Analytical_Data_Becomes_Real_Tables_And_Email_Safe_Bars_With_Values_Shares_And_Citations()
    {
        // Arrange
        var run = Run();
        run.Analyses.Add(Analysis());

        // Act
        var html = EmailAnswerRenderer.Render(run);

        // Assert
        Assert.Contains("aria-label=\"Bar chart\"", html);
        Assert.Contains("role=\"presentation\"", html);
        Assert.Contains("bgcolor=\"#2563eb\"", html);
        Assert.Contains("width=\"100%\"", html);
        Assert.Contains("width=\"8.38%\"", html);
        Assert.Contains("680,000.00", html);
        Assert.Contains("57,000.00", html);
        Assert.Contains("92.27%", html);
        Assert.Contains("7.73%", html);
        Assert.Contains("737,000.00", html);
        Assert.Contains("<tfoot>", html);
        Assert.Contains("<caption", html);
        Assert.Contains("Supporting data", html);
        Assert.Contains("text-align:right;", html);
        Assert.Contains("4 source records; 1 duplicates excluded; 1 records with missing values.", html);
        Assert.Contains("Additional documents were not inspected.", html);
        Assert.DoesNotContain("<svg", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("\u2588", html);
    }

    [Fact]
    public void Stored_Json_Numbers_And_Culture_Changes_Do_Not_Change_The_Email_Values()
    {
        // Arrange
        var run = Run();
        run.Analyses.Add(JsonSerializer.Deserialize<AskAiAnalysis>(JsonSerializer.Serialize(Analysis()))!);
        var initial = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            // Act
            var html = EmailAnswerRenderer.Render(run);

            // Assert
            Assert.Contains("680,000.00", html);
            Assert.Contains("92.27%", html);
            Assert.Contains("width=\"8.38%\"", html);
        }
        finally
        {
            CultureInfo.CurrentCulture = initial;
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("negative")]
    [InlineData("mixed-unit")]
    public void Unsupported_Bar_Data_Stays_In_A_Table_Without_Inventing_Zeroes_Or_Mixing_Units(string condition)
    {
        // Arrange
        var run = Run();
        var analysis = Analysis();
        if (condition == "missing") analysis.Rows[1].Cells["value"] = null;
        if (condition == "negative") analysis.Rows[1].Cells["value"] = -57_000m;
        if (condition == "mixed-unit") analysis.Rows[1].Cells["unit"] = "EUR";
        run.Analyses.Add(analysis);

        // Act
        var html = EmailAnswerRenderer.Render(run);

        // Assert
        Assert.DoesNotContain("aria-label=\"Bar chart\"", html);
        Assert.Contains("A table is used instead of bars", html);
        Assert.Contains("Supporting data", html);
        Assert.Contains(condition == "missing" ? "Not available" :
            condition == "negative" ? "-57,000.00" : "EUR", html);
    }

    [Fact]
    public void Zeroes_Are_Labelled_Without_Dividing_By_Zero()
    {
        // Arrange
        var run = Run();
        var analysis = Analysis();
        foreach (var row in analysis.Rows) row.Cells["value"] = 0m;
        run.Analyses.Add(analysis);

        // Act
        var html = EmailAnswerRenderer.Render(run);

        // Assert
        Assert.Contains("aria-label=\"Bar chart\"", html);
        Assert.Contains("0 USD", html);
        Assert.DoesNotContain("NaN", html);
        Assert.DoesNotContain("Infinity", html);
        Assert.DoesNotContain("bgcolor=\"#2563eb\"", html);
    }

    [Fact]
    public void Small_Nonzero_Amounts_Remain_Exact_In_The_Data_Table()
    {
        // Arrange
        var run = Run();
        var analysis = Analysis();
        analysis.Rows[1].Cells["value"] = 0.0000001m;
        run.Analyses.Add(analysis);

        // Act
        var html = EmailAnswerRenderer.Render(run);

        // Assert
        Assert.Contains("0.0000001", html);
        Assert.Contains("Very small values may not be visible; exact values are in the table.", html);
    }

    [Fact]
    public void Model_Source_And_Stored_Links_Cannot_Inject_Html_Scripts_Images_Or_External_Tracking()
    {
        // Arrange
        var run = Run();
        run.Answer = "<script>alert(1)</script>\n\n![tracker](https://external.test/a.png)\n\n[open](javascript:alert(1))";
        run.Citations[0] = run.Citations[0] with
        {
            FileName = "<img src=x onerror=alert(1)>",
            WorkspaceUrl = "javascript:alert(1)",
            PdfUrl = "https://external.test/tracker"
        };
        var analysis = Analysis();
        analysis.Rows[0].Cells["category"] = "<img src=private> **literal source text**";
        run.Analyses.Add(analysis);

        // Act
        var html = EmailAnswerRenderer.Render(run, "https://docintel.example");

        // Assert
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("href=\"javascript", html);
        Assert.DoesNotContain("src=", String.Join("", Regex.Matches(html, "<[^>]+>").Select(tag => tag.Value)));
        Assert.DoesNotContain("href=\"https://external", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("**literal source text**", html);
        Assert.Contains("/jobs/source/slices/0/pdf", html);
    }

    [Fact]
    public void Markdown_Tables_Preserve_Empty_Cells_Escaped_Pipes_And_Citations()
    {
        // Arrange
        var run = Run();
        run.Answer = """
            | Customer | Amount | Source |
            | :--- | ---: | --- |
            | A \| B | 12,450 USD | [1] |
            | | Missing | [1] |
            """;

        // Act
        var html = EmailAnswerRenderer.Render(run);

        // Assert
        Assert.Contains("<thead>", html);
        Assert.Contains("A | B", html);
        Assert.Contains("12,450 USD", html);
        Assert.Contains("Missing", html);
        Assert.Equal(6, Regex.Matches(html, "<td ").Count);
        Assert.Contains("href=\"#source-1\"", html);
    }

    [Fact]
    public void Long_Datasets_Are_Explicitly_Previewed_And_Long_Answers_Fit_The_Graph_Body_Budget()
    {
        // Arrange
        var run = Run();
        run.Answer = new string('&', 64_000);
        var analysis = Analysis();
        for (var index = 0; index < 40; index++) analysis.Rows.Add(analysis.Rows[0]);
        run.Analyses.Add(analysis);

        // Act
        var html = EmailAnswerRenderer.Render(run);

        // Assert
        Assert.True(html.Length <= EmailAnswerRenderer.MaximumBodyCharacters);
        Assert.Contains("Email preview shortened to fit message limits", html);
        Assert.Contains("Showing 5 of 42 rows", html);
        Assert.Contains("View full answer and original sources", html);
        Assert.Contains("Sources", html);
    }

    [Theory]
    [InlineData("http://docintel.example")]
    [InlineData("https://user:password@docintel.example")]
    [InlineData("https://docintel.example/path")]
    [InlineData("https://docintel.example/?redirect=external")]
    public void Public_Links_Require_The_Approved_Application_Origin(string origin)
    {
        // Arrange
        var run = Run();

        // Act / Assert
        Assert.Throws<ArgumentException>(() => EmailAnswerRenderer.Render(run, origin));
    }

    private static AskAiRun Run() => new()
    {
        RunId = "example", TenantId = "workspace", Question = "PRIVATE_ORIGINAL_QUESTION",
        Answer = "Tailwind contributes the largest share [1].",
        Citations =
        [
            new(1, "source", "quotes.pdf", "Quotes", 0, "USD quotes", "p.1-2",
                String.Empty, String.Empty, String.Empty, "USD", String.Empty, "private source",
                "/?tenant=workspace&job=source", "/api/harness/tenants/workspace/jobs/source/slices/0/pdf")
        ]
    };

    private static AskAiAnalysis Analysis() => new()
    {
        AnalysisId = "a1", Title = "Deal value by customer", Operation = "sum", Visualization = "bar",
        XColumn = "category", YColumn = "value",
        Columns =
        [
            new("category", "Customer", "text"), new("unit", "Currency", "text"),
            new("value", "Total deal value", "number"), new("sharePercent", "Share of total", "number")
        ],
        Rows =
        [
            new() { Cells = new() { ["category"] = "Tailwind Aerospace", ["unit"] = "USD",
                ["value"] = 680_000m, ["sharePercent"] = 92.2659m }, CitationNumbers = [1] },
            new() { Cells = new() { ["category"] = "Alderwind", ["unit"] = "USD",
                ["value"] = 57_000m, ["sharePercent"] = 7.7341m }, CitationNumbers = [1] }
        ],
        Coverage = new() { RecordsIncluded = 4, DuplicatesExcluded = 1, MissingValues = 1,
            DocumentsMatched = 7, DocumentsInspected = 4, HasMoreDocuments = true },
        Notes = ["Missing amounts were excluded, not treated as zero."]
    };
}
