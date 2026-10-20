using DocIntel.Core.Evidence;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using Microsoft.Agents.AI.Workflows;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class ValueGroundingTests
{
    [Fact]
    public async Task Native_Value_Matches_Return_Actual_Source_Word_Ids_And_Polygons()
    {
        // Arrange
        var pdf = PdfPigLayoutExtractor.CreateSamplePdf([("Customer: Woodgrove Bank", "Form-1")]);
        var pages = await new PdfPigLayoutExtractor().ExtractPagesAsync(pdf, new SplittingConfig());

        // Act
        var match = new ValueGrounding(pages).Find("Woodgrove Bank", 1, 1);

        // Assert
        Assert.True(match.Grounded);
        Assert.Equal("Woodgrove Bank", match.Excerpt);
        Assert.Equal(2, match.WordIds.Count);
        var original = pages[0].Words.Where(word => match.WordIds.Contains(word.Id)).ToList();
        Assert.Equal(original.Select(word => word.Id), match.WordIds);
        Assert.All(match.Boxes, box => Assert.Contains(original, word => ReferenceEquals(word.Box, box)));
    }

    [Fact]
    public void Whitespace_And_Ordinal_Case_Matching_Still_Quote_Actual_Source_Casing()
    {
        // Arrange
        var page = Page(1, "Woodgrove", "Bank");

        // Act
        var match = new ValueGrounding([page]).Find("  WOODGROVE\t\nBANK  ", 1, 1);

        // Assert
        Assert.Equal(GroundingStatus.ExactTextMatch, match.Status);
        Assert.Equal("Woodgrove Bank", match.Excerpt);
    }

    [Fact]
    public void Duplicate_Source_Occurrences_Are_Ambiguous_Not_A_Convenient_First_Citation()
    {
        // Arrange
        var page = Page(1, "100", "100");

        // Act
        var match = new ValueGrounding([page]).Find("100", 1, 1);

        // Assert
        Assert.Equal(GroundingStatus.Ambiguous, match.Status);
        Assert.False(match.Grounded);
        Assert.Empty(match.WordIds);
        Assert.Empty(match.Boxes);
    }

    [Fact]
    public void A_Match_In_Another_Slice_Cannot_Ground_This_Slices_Value()
    {
        // Arrange
        var index = new ValueGrounding([Page(1, "First"), Page(2, "Second")]);

        // Act / Assert
        Assert.Equal(GroundingStatus.NotFound, index.Find("First", 2, 2).Status);
        Assert.Equal(2, Assert.Single(index.Find("Second", 2, 2).Boxes).Page);
        Assert.Equal(GroundingStatus.NotFound, index.Find("First Second", 1, 2).Status);
    }

    [Theory]
    [InlineData("100", "1000")]
    [InlineData("Bank", "Banking")]
    [InlineData("1,000.00", "$1,000.00")]
    [InlineData("Program", "Programa")]
    public void Partial_Or_Translated_Tokens_Are_Not_Fuzzy_Source_Matches(string value, string source)
    {
        // Arrange
        var index = new ValueGrounding([Page(1, source)]);

        // Act / Assert
        Assert.Equal(GroundingStatus.NotFound, index.Find(value, 1, 1).Status);
    }

    [Fact]
    public void Unrecognized_Or_Missing_Page_Geometry_Does_Not_Produce_A_Complete_Source_Claim()
    {
        // Arrange
        var legacy = new PageLayout { PageNumber = 1, BodyText = "Known without words" };
        var scanned = new PageLayout { PageNumber = 2, PageWidth = 612, PageHeight = 792, ImageAreaRatio = 1 };

        // Act / Assert
        Assert.Equal(GroundingStatus.Unavailable, new ValueGrounding([legacy]).Find("Known", 1, 1).Status);
        Assert.Equal(GroundingStatus.Unavailable,
            new ValueGrounding([Page(1, "Known"), scanned]).Find("Known", 1, 2).Status);
        Assert.Equal(GroundingStatus.Unavailable, new ValueGrounding([Page(1, "Known")]).Find("Known", 1, 2).Status);
    }

    [Fact]
    public void Zero_And_Unknown_Scores_Are_Not_Inflated_Or_Averaged_With_Text_Presence()
    {
        // Arrange
        var match = new ValueGrounding([Page(1, "Known")]).Find("Known", 1, 1);

        // Act
        var signals = ValueGrounding.Signals(0, 1, match);
        var invalid = ValueGrounding.Signals(Double.NaN, 100, match);

        // Assert
        Assert.Equal(0, signals.ModelScore);
        Assert.Equal(1, signals.GroundingScore);
        Assert.False(signals.Calibrated);
        Assert.Null(invalid.ModelScore);
    }

    [Fact]
    public async Task Entities_Fields_And_Table_Cells_Receive_Consistent_Additive_Provenance()
    {
        // Arrange
        var state = new SplittingState
        {
            Tenant = new TenantHarness { TenantId = "tenant", DisplayName = "Test" },
            FileName = "source.pdf",
            Pages = [Page(1, "Woodgrove", "100")],
            Slices =
            [
                new DocumentSlice
                {
                    SegmentIndex = 0, StartPage = 1, EndPage = 1,
                    ExtractedEntities = [new ExtractedEntity("Customer", "Woodgrove", "Party", 0, 0)],
                    DataExtraction = new DocumentDataExtractionResult("Invoice", "Form", "1-1",
                        [new ExtractedFieldValue("Customer", "Woodgrove", 85)],
                        [new ExtractedTableValue("Amounts",
                            [new ExtractedTableRow([new ExtractedFieldValue("Amount", "100", 90)])])])
                }
            ]
        };

        // Act
        await new GroundExtractedValuesExecutor().HandleAsync(state, Mock.Of<IWorkflowContext>());

        // Assert
        var entity = Assert.Single(state.ExtractedEntities);
        Assert.True(entity.Provenance!.Grounded);
        Assert.Equal(0, entity.Confidence);
        Assert.Equal(0, entity.ConfidenceSignals!.ModelScore);
        var data = Assert.Single(state.DataExtractions);
        Assert.Equal(0.85, data.Fields[0].ConfidenceSignals!.ModelScore);
        Assert.True(data.Tables[0].Rows[0].Fields[0].Provenance!.Grounded);
        Assert.Equal("Woodgrove", entity.Value);
    }

    [Fact]
    public void Large_Matches_Are_Not_Partially_Cited_As_If_All_Words_Were_Preserved()
    {
        // Arrange
        var words = Enumerable.Range(0, 65).Select(index => "word" + index).ToArray();

        // Act
        var match = new ValueGrounding([Page(1, words)]).Find(String.Join(" ", words), 1, 1);

        // Assert
        Assert.Equal(GroundingStatus.ReferenceLimitExceeded, match.Status);
        Assert.Empty(match.WordIds);
        Assert.False(match.Grounded);
    }

    [Fact]
    public async Task Total_Attached_Geometry_Is_Bounded_Without_Partially_Citing_Excess_Values()
    {
        // Arrange
        var words = Enumerable.Range(0, 1_050).Select(index =>
        {
            var left = 0.01 + index % 20 * 0.02;
            var top = 0.01 + index / 20 * 0.01;
            return new PageWord($"w_1_{index}", $"v{index}",
                new BoundingBox(1, [left, top, left + 0.01, top, left + 0.01, top + 0.005, left, top + 0.005]));
        }).ToList();
        var state = new SplittingState
        {
            Tenant = new TenantHarness { TenantId = "tenant", DisplayName = "Test" },
            FileName = "source.pdf",
            Pages = [new PageLayout { PageNumber = 1, PageWidth = 612, PageHeight = 792, Words = words }],
            Slices = [new DocumentSlice
            {
                StartPage = 1, EndPage = 1,
                ExtractedEntities = words.Select(word => new ExtractedEntity(word.Id, word.Text, "Test", 0.9)).ToList()
            }]
        };

        // Act
        await new GroundExtractedValuesExecutor().HandleAsync(state, Mock.Of<IWorkflowContext>());

        // Assert
        Assert.Equal(1_024, state.ExtractedEntities.Sum(entity => entity.Provenance!.WordIds.Count));
        Assert.Contains(state.ExtractedEntities, entity =>
            entity.Provenance!.Status == GroundingStatus.ReferenceLimitExceeded);
    }

    [Fact]
    public void Cancellation_Stops_Indexing_Or_Lookup()
    {
        // Arrange
        var index = new ValueGrounding([Page(1, "Known")]);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act / Assert
        Assert.ThrowsAny<OperationCanceledException>(() => index.Find("Known", 1, 1, canceled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => new ValueGrounding([Page(1, "Known")], canceled.Token));
    }

    private static PageLayout Page(int page, params string[] text) => new()
    {
        PageNumber = page,
        PageWidth = 612,
        PageHeight = 792,
        TextSource = "EmbeddedText",
        Words = text.Select((word, index) =>
        {
            var left = 0.01 + index * 0.01;
            return new PageWord($"w_{page}_{index}", word,
                new BoundingBox(page, [left, 0.1, left + 0.009, 0.1, left + 0.009, 0.12, left, 0.12]));
        }).ToArray()
    };
}
