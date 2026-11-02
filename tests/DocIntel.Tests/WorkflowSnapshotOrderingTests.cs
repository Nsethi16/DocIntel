using System.Collections.Concurrent;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.DocumentTranslation;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;
using DocIntel.Core.Ocr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class WorkflowSnapshotOrderingTests
{
    [Fact]
    public async Task Earlier_Stage_Snapshots_Complete_Before_A_Slow_Provider_Stage_Starts()
    {
        // Arrange
        var fixture = new Fixture();
        var snapshots = new ConcurrentQueue<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Setup(client => client.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken token) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return fixture.Response();
            });

        // Act
        var running = fixture.Workflow.ExecuteAsync(fixture.State, async (state, token) =>
        {
            await Task.Delay(20, token);
            snapshots.Enqueue(state.ExecutionTrace[^1].ExecutorName);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        try
        {
            Assert.Equal(new[]
            {
                "ExtractPageLayoutsExecutor", "DetectSliceBoundariesExecutor", "WritePdfSlicesExecutor"
            }, snapshots.ToArray());
        }
        finally { release.TrySetResult(); }
        var completed = await running;
        Assert.Equal(completed.ExecutionTrace.Count, snapshots.Count);
        Assert.Equal(completed.ExecutionTrace.Select(step => step.ExecutorName), snapshots);
    }

    [Fact]
    public async Task A_Rejected_Progress_Snapshot_Prevents_Downstream_Provider_Execution()
    {
        // Arrange
        var fixture = new Fixture();

        // Act / Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Workflow.ExecuteAsync(fixture.State, (_, _) =>
                throw new UnauthorizedAccessException("Current access was revoked.")));
        fixture.Client.Verify(client => client.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class Fixture
    {
        public Mock<IChatClient> Client { get; } = new();
        public SplittingState State { get; }
        public DocumentSplittingWorkflow Workflow { get; }

        public Fixture()
        {
            var tenant = new TenantHarness
            {
                TenantId = "ordering", DisplayName = "Ordering", DefaultModelId = "fake", DecisionModelId = "fake",
                AllowedModels = ["fake"], Ocr = new() { Mode = OcrMode.Never },
                Capabilities = new(StringComparer.OrdinalIgnoreCase)
                {
                    [FeatureHarness.SplitAndGroupKey] = new() { Enabled = true, SplitAndGroup = new() },
                    [FeatureHarness.IdentifyDocumentsKey] = new()
                    {
                        Enabled = true, IdentifyDocuments = new() { DecisionModelId = "fake",
                            Rules = [new() { DocumentName = "Quote", IdentificationPrompt = "Quote heading." }] }
                    }
                }
            };
            State = new()
            {
                Tenant = tenant, FileName = "ordering.pdf",
                SourcePdfBytes = PdfPigLayoutExtractor.CreateSamplePdf([("Quote\nQuote Number: QA-1", "Form-Quote-v1")])
            };
            var extractor = new PdfPigLayoutExtractor();
            Workflow = new(extractor, extractor,
                new ModelCatalog(customClients: new Dictionary<string, IChatClient> { ["fake"] = Client.Object }),
                Mock.Of<IDocumentTranslationService>(), NullLogger<DocumentSplittingWorkflow>.Instance);
        }

        public ChatResponse Response() => new(new ChatMessage(ChatRole.Assistant,
            """{"identifiedDocumentName":"Quote","confidence":0.99,"matchedRule":"Quote heading."}"""));
    }
}
