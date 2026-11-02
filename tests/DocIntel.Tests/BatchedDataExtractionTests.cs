using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocIntel.Core.Features.DataExtraction;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class BatchedDataExtractionTests
{
    [Theory]
    [InlineData(false, 3500, 3)]
    [InlineData(false, 2000, 1)]
    [InlineData(true, 3500, 1)]
    public async Task Large_Original_Tables_Are_Batched_And_Merged_In_Source_Order(
        bool translated, int pageCharacters, int expectedRequests)
    {
        // Arrange
        var fixture = new Fixture(translated, pageCharacters);

        // Act
        await fixture.Executor.HandleAsync(fixture.State, Mock.Of<IWorkflowContext>());

        // Assert
        Assert.Equal(expectedRequests, fixture.Requests);
        var result = Assert.Single(fixture.State.DataExtractions);
        Assert.Equal("QA-REFERENCE", Assert.Single(result.Fields).FieldValue);
        Assert.Equal(new[] { "SKU-1", "SKU-2", "SKU-3", "SKU-4" },
            Assert.Single(result.Tables).Rows.Select(row => row.Fields[0].FieldValue));
        Assert.InRange(fixture.Concurrency.DefaultIfEmpty(1).Max(), 1, 2);
        Assert.Equal("1-4", result.PageRange);
    }

    [Fact]
    public async Task A_Failed_Table_Batch_Never_Publishes_Header_Or_Partial_Table_Results()
    {
        // Arrange
        var fixture = new Fixture(false, 3500) { FailSecondBatch = true };

        // Act / Assert
        await Assert.ThrowsAnyAsync<Exception>(() =>
            fixture.Executor.HandleAsync(fixture.State, Mock.Of<IWorkflowContext>()).AsTask());
        Assert.Null(Assert.Single(fixture.State.Slices).DataExtraction);
        Assert.Empty(fixture.State.DataExtractions);
    }

    private sealed class Fixture
    {
        private int _requests;
        private int _active;
        public int Requests => _requests;
        public bool FailSecondBatch { get; set; }
        public ConcurrentBag<int> Concurrency { get; } = [];
        public SplittingState State { get; }
        public ExtractDocumentDataExecutor Executor { get; }

        public Fixture(bool translated, int pageCharacters)
        {
            var pages = Enumerable.Range(1, 4).Select(number => new PageLayout
            {
                PageNumber = number, BodyText = $"SOURCE-PAGE-{number}\n" + new string('x', pageCharacters),
                Markdown = $"SOURCE-PAGE-{number}\n" + new string('x', pageCharacters)
            }).ToArray();
            var content = String.Join("\n", pages.Select(page => page.BodyText));
            State = new()
            {
                Tenant = new()
                {
                    TenantId = "batch", DisplayName = "Batch",
                    Capabilities = new(StringComparer.OrdinalIgnoreCase)
                    {
                        [FeatureHarness.DataExtractionKey] = new()
                        {
                            Enabled = true, DataExtraction = new() { Profiles =
                            [
                                new()
                                {
                                    DocumentName = "Quote",
                                    Fields = [new() { FieldName = "Reference", Instruction = "Original reference." }],
                                    Tables = [new() { TableName = "Lines", Instruction = "All source rows.",
                                        Columns = [new() { ColumnName = "SKU", Instruction = "Original SKU." }] }]
                                }
                            ] }
                        }
                    }
                },
                FileName = "batch.pdf", Pages = pages.ToList(),
                Slices = [new() { SegmentIndex = 0, StartPage = 1, EndPage = 4,
                    IdentifiedDocumentName = "Quote", BodyText = content, Markdown = content,
                    WasTranslated = translated, TranslatedText = translated ? content : null }]
            };
            var client = new Mock<IChatClient>();
            client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .Returns(async (IEnumerable<ChatMessage> messages, ChatOptions options, CancellationToken token) =>
                {
                    Interlocked.Increment(ref _requests);
                    var fields = options.Instructions!.Contains("FIELD|Reference|", StringComparison.Ordinal);
                    var tables = options.Instructions.Contains("TABLE|Lines|", StringComparison.Ordinal);
                    var text = messages.Last().Text!;
                    var scope = Regex.Match(options.Instructions,
                        @"Extract only rows starting on original source pages (\d+)-(\d+)");
                    var first = scope.Success ? Int32.Parse(scope.Groups[1].Value,
                        System.Globalization.CultureInfo.InvariantCulture) : 1;
                    var last = scope.Success ? Int32.Parse(scope.Groups[2].Value,
                        System.Globalization.CultureInfo.InvariantCulture) : 4;
                    if (tables)
                    {
                        Concurrency.Add(Interlocked.Increment(ref _active));
                        try
                        {
                            if (FailSecondBatch && first == 3)
                                throw new InvalidDataException("Injected batch failure.");
                            await Task.Delay(20, token);
                        }
                        finally { Interlocked.Decrement(ref _active); }
                    }
                    var response = new
                    {
                        fields = fields
                            ? new[] { new { fieldName = "Reference", fieldValue = "QA-REFERENCE", confidenceValue = 99 } }
                            : [],
                        tables = tables ? new[]
                        {
                            new { tableName = "Lines", rows = Enumerable.Range(first, last - first + 1).Where(number =>
                                text.Contains($"SOURCE-PAGE-{number}", StringComparison.Ordinal)).Select(number =>
                                new { fields = new[] { new { fieldName = "SKU", fieldValue = $"SKU-{number}",
                                    confidenceValue = 99 } } }).ToArray() }
                        } : []
                    };
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(response)));
                });
            Executor = new(new ModelCatalog(customClients:
                new Dictionary<string, IChatClient> { [State.Tenant.DefaultModelId] = client.Object }));
        }
    }
}
