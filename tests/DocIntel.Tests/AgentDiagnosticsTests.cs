using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.DocumentTranslation;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;
using DocIntel.Core.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class AgentDiagnosticsTests
{
    [Theory]
    [InlineData("succeeded")]
    [InlineData("skipped")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void Operation_Spans_And_Metrics_Close_Exactly_Once_Without_Source_Data(string outcome)
    {
        // Arrange
        var id = Guid.NewGuid().ToString("N");
        var measurements = new List<(string Name, double Value, Dictionary<string, object?> Tags)>();
        Activity? stopped = null;
        using var traces = Listener(activity =>
        {
            if (Equals(activity.GetTagItem("docintel.document_id"), id)) stopped = activity;
        });
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AgentDiagnostics.SourceName) listener.EnableMeasurementEvents(instrument);
        };
        void Capture(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            if (Equals(Activity.Current?.GetTagItem("docintel.document_id"), id))
                measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
        }
        meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Capture(instrument, value, tags));
        meter.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Capture(instrument, value, tags));
        meter.Start();
        var operation = AgentDiagnostics.Begin("test.operation", documentId: id, tenantId: "tenant", harnessVersion: 3);

        // Act
        if (outcome == "succeeded") operation.Complete();
        else if (outcome == "skipped") operation.Complete(skipped: true);
        else if (outcome == "cancelled") operation.Cancel();
        else operation.Fail(new HttpRequestException("PRIVATE_DOCUMENT_OR_CREDENTIAL"));
        operation.Dispose();
        operation.Dispose();

        // Assert
        Assert.NotNull(stopped);
        Assert.Equal(outcome, stopped.GetTagItem("docintel.outcome"));
        Assert.Equal(outcome == "failed" ? ActivityStatusCode.Error :
            outcome == "cancelled" ? ActivityStatusCode.Unset : ActivityStatusCode.Ok, stopped.Status);
        Assert.DoesNotContain("PRIVATE_DOCUMENT_OR_CREDENTIAL", String.Join(",", stopped.TagObjects));
        Assert.DoesNotContain(stopped.Events, item => item.Name == "exception");
        Assert.Equal(new double[] { 1, -1 },
            measurements.Where(item => item.Name == "docintel.operation.active").Select(item => item.Value));
        var completed = Assert.Single(measurements, item => item.Name == "docintel.operation.completed");
        Assert.Equal(outcome, completed.Tags["outcome"]);
        Assert.All(measurements, item => Assert.All(item.Tags.Keys,
            key => Assert.Contains(key, new[] { "operation", "outcome" })));
        Assert.True(Assert.Single(measurements, item => item.Name == "docintel.operation.duration").Value >= 0);
    }

    [Fact]
    public async Task All_Document_Workflow_Stages_Emit_Real_Execution_Spans_Including_Disabled_Stages()
    {
        // Arrange
        var id = Guid.NewGuid().ToString("N");
        var spans = new ConcurrentBag<Activity>();
        using var traces = Listener(activity =>
        {
            if (Equals(activity.GetTagItem("docintel.document_id"), id)) spans.Add(activity);
        });
        var extractor = new PdfPigLayoutExtractor();
        var tenant = new TenantHarness { TenantId = "tenant", DisplayName = "Tenant" };
        foreach (var (name, feature) in tenant.Capabilities) feature.Enabled = name == FeatureHarness.SplitAndGroupKey;
        var workflow = new DocumentSplittingWorkflow(extractor, extractor,
            new ModelCatalog(allowDevelopmentSimulation: true), new AzureDocumentTranslationService(),
            NullLogger<DocumentSplittingWorkflow>.Instance);

        // Act
        await workflow.ExecuteAsync(new SplittingState
        {
            DocumentId = id,
            Tenant = tenant,
            FileName = "PRIVATE_FILENAME.pdf",
            SourcePdfBytes = PdfPigLayoutExtractor.CreateSamplePdf([("PRIVATE_SOURCE_TEXT", "Footer")])
        });

        // Assert
        Assert.Equal(10, spans.Count);
        Assert.Contains(spans, span => span.OperationName == "Executor.IdentifyDocumentsExecutor" &&
            Equals(span.GetTagItem("docintel.outcome"), "skipped"));
        Assert.All(spans, span =>
        {
            Assert.True(span.Duration >= TimeSpan.Zero);
            Assert.DoesNotContain("PRIVATE_", String.Join(",", span.TagObjects));
        });
    }

    [Fact]
    public async Task Model_Token_Metrics_Use_Actual_Provider_Counts_Only()
    {
        // Arrange
        var values = new List<(long Count, string Direction)>();
        using var ambient = new Activity("isolated-token-test").Start();
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AgentDiagnostics.SourceName &&
                instrument.Name == "docintel.model.tokens") listener.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (Activity.Current?.TraceId == ambient.TraceId)
                values.Add((value, tags.ToArray().Single(tag => tag.Key == "direction").Value!.ToString()!));
        });
        meter.Start();
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Response"))
            {
                Usage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 }
            });
        using var measured = new ReasoningCleanupChatClient(client.Object, ownsInnerClient: false);

        // Act
        await measured.GetResponseAsync([new ChatMessage(ChatRole.User, "PRIVATE_QUESTION")]);
        AgentDiagnostics.RecordTokens(null, null);

        // Assert
        Assert.Equal(new[] { (7L, "input"), (3L, "output") }, values);
    }

    [Fact]
    public async Task Progress_Persistence_Sees_Stable_Lists_While_Later_Executors_Are_Ready_To_Run()
    {
        // Arrange
        var extractor = new PdfPigLayoutExtractor();
        var tenant = new TenantHarness { TenantId = "tenant", DisplayName = "Tenant" };
        foreach (var (name, feature) in tenant.Capabilities) feature.Enabled = name == FeatureHarness.SplitAndGroupKey;
        var workflow = new DocumentSplittingWorkflow(extractor, extractor,
            new ModelCatalog(allowDevelopmentSimulation: true), new AzureDocumentTranslationService(),
            NullLogger<DocumentSplittingWorkflow>.Instance);
        var observations = 0;

        // Act
        await workflow.ExecuteAsync(new SplittingState
        {
            Tenant = tenant,
            FileName = "source.pdf",
            SourcePdfBytes = PdfPigLayoutExtractor.CreateSamplePdf([("Source text", "Footer")])
        }, async (state, cancellationToken) =>
        {
            var count = state.ExecutionTrace.Count;
            await Task.Delay(5, cancellationToken);
            var snapshot = JsonSerializer.Serialize(state);
            Assert.Equal(count, state.ExecutionTrace.Count);
            Assert.Equal(count, state.ExecutionTrace.ToList().Count);
            using var parsed = JsonDocument.Parse(snapshot);
            Assert.Equal(count, parsed.RootElement.GetProperty("ExecutionTrace").GetArrayLength());
            observations++;
        });

        // Assert
        Assert.Equal(10, observations);
    }

    private static ActivityListener Listener(Action<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
