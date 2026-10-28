using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocIntel.Core.Features.DocumentIdentification;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace DocIntel.Core.Features.DocumentInsight;

/// <summary>
/// Generates complete-file or identified-document executive summaries using
/// <see cref="ChatClientAgent"/> guided by the admin's configured <see cref="DocumentInsightConfig.SummaryInstructions"/>.
/// </summary>
[SendsMessage(typeof(SplittingState))]
public sealed class GenerateDocumentInsightsExecutor : Executor<SplittingState, SplittingState>
{
    private readonly IModelCatalog _modelCatalog;

    public GenerateDocumentInsightsExecutor(IModelCatalog modelCatalog)
        : base(nameof(GenerateDocumentInsightsExecutor))
    {
        _modelCatalog = modelCatalog ?? throw new ArgumentNullException(nameof(modelCatalog));
    }

    public override async ValueTask<SplittingState> HandleAsync(
        SplittingState message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        if (!message.Tenant.Capabilities.TryGetValue(FeatureHarness.DocumentInsightKey, out var feature) ||
            !feature.Enabled)
        {
            message.PacketSummary = null;
            foreach (var slice in message.Slices) slice.InsightSummary = null;
            message.ExecutionTrace.Add(new WorkflowStepTrace(
                nameof(GenerateDocumentInsightsExecutor),
                Executed: false,
                Succeeded: true,
                ModelIdUsed: null,
                DurationMs: 0,
                Notes: "Skipped (Document Insight is disabled for this workspace)."));
            return message;
        }

        var sw = Stopwatch.StartNew();
        var result = await GenerateAsync(message.Slices, message.Pages.Count,
            message.Tenant, feature, _modelCatalog, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ApplyDocumentSummaries(message.Slices, result);
        message.PacketSummary = result.PacketSummary;
        message.HasDevelopmentResults |= result.Development;
        sw.Stop();

        message.ExecutionTrace.Add(new WorkflowStepTrace(
            nameof(GenerateDocumentInsightsExecutor),
            Executed: true,
            Succeeded: true,
            ModelIdUsed: result.ModelId,
            DurationMs: sw.Elapsed.TotalMilliseconds,
            Notes: result.Documents.Count == 0
                ? $"Generated one complete-file insight using model '{result.ModelId}'."
                : $"Generated {result.Documents.Count} complete-document insight(s) using model '{result.ModelId}'."));

        return message;
    }

    /// <summary>
    /// Re-evaluates Document Insights on an existing <see cref="DocumentJob"/> when the Admin updates
    /// <see cref="DocumentInsightConfig.SummaryInstructions"/> or toggles Document Insights in Admin Studio.
    /// </summary>
    public static async Task EvaluateJobInsightsAsync(
        DocumentJob job,
        TenantHarness tenant,
        IModelCatalog modelCatalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(modelCatalog);
        cancellationToken.ThrowIfCancellationRequested();

        if (!tenant.Capabilities.TryGetValue(FeatureHarness.DocumentInsightKey, out var feature) || !feature.Enabled)
        {
            job.PacketSummary = null;
            foreach (var s in job.Segments)
            {
                s.InsightSummary = null;
            }
            return;
        }

        var result = await GenerateAsync(job.Segments, job.TotalPages,
            tenant, feature, modelCatalog, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ApplyDocumentSummaries(job.Segments, result);
        job.PacketSummary = result.PacketSummary;
        if (result.Development) job.HasDevelopmentResults = true;
    }

    private static async Task<InsightBatch> GenerateAsync(
        IReadOnlyList<DocumentSlice> slices,
        int totalPages,
        TenantHarness tenant,
        FeatureHarness feature,
        IModelCatalog modelCatalog,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = slices.OrderBy(slice => slice.StartPage).ThenBy(slice => slice.SegmentIndex).ToArray();
        if (ordered.Length == 0)
            throw new InvalidDataException("No document content is available for insight generation.");

        long nextPage = 1;
        foreach (var slice in ordered)
        {
            if (slice.StartPage != nextPage || slice.EndPage < slice.StartPage)
                throw new InvalidDataException("Insight generation requires complete, non-overlapping file page ranges.");
            nextPage = (long)slice.EndPage + 1;
        }
        if (totalPages > 0 && nextPage != (long)totalPages + 1)
            throw new InvalidDataException("Insight generation cannot omit pages from the uploaded file.");

        var documentMode = tenant.Capabilities.TryGetValue(FeatureHarness.SplitAndGroupKey, out var split) &&
            split.Enabled && tenant.Capabilities.TryGetValue(FeatureHarness.IdentifyDocumentsKey, out var identify) &&
            identify.Enabled && ordered.All(slice => !String.IsNullOrWhiteSpace(slice.IdentifiedDocumentName) &&
                !String.Equals(slice.IdentifiedDocumentName.Trim(), IdentifyDocumentsExecutor.UndefinedDocumentName,
                    StringComparison.OrdinalIgnoreCase));
        string[] inputs = documentMode
            ? ordered.Select(DocumentPromptBoundary.SourceText).ToArray()
            : [String.Join(Environment.NewLine + Environment.NewLine,
                ordered.Select(DocumentPromptBoundary.SourceText))];
        if (inputs.Any(String.IsNullOrWhiteSpace))
            throw new InvalidDataException("No document text is available for the requested insight scope.");

        var config = feature.DocumentInsight ?? new DocumentInsightConfig();
        var summaryInstructions = !string.IsNullOrWhiteSpace(feature.CustomSystemPrompt)
            ? feature.CustomSystemPrompt!
            : config.SummaryInstructions;

        var modelId = modelCatalog.ResolveModelId(tenant, feature.ModelIdOverride);
        using var chatClient = modelCatalog.ResolveChatClient(tenant, feature.ModelIdOverride);
        var development = modelCatalog.UsesDevelopmentSimulation(tenant, feature.ModelIdOverride);
        var scope = documentMode ? "one complete identified document" : "the complete uploaded file";
        var chatOptions = new ChatOptions
        {
            Instructions = $"[TASK:DOCUMENT_INSIGHT]\nYou are an enterprise document insight assistant.\n" +
                $"{summaryInstructions}\nThe supplied content covers {scope}. " +
                "Return one integrated summary covering all of its pages. " +
                "Do not generate page-by-page summaries or separate sections for individual pages.\n" +
                DocumentPromptBoundary.Policy,
            ResponseFormat = ChatResponseFormat.ForJsonSchema<InsightResponseDto>(
                schemaName: "document_insight_summary",
                schemaDescription: "Structured document insight summary")
        };

        var insightAgent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Id = "DocumentInsightAgent",
                Name = "DocumentInsightAgent",
                ChatOptions = chatOptions
            });

        var summaries = new List<string>();
        foreach (var sourceContent in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await insightAgent.RunAsync(
                DocumentPromptBoundary.Wrap(sourceContent), cancellationToken: cancellationToken);
            var cleanedJson = ReasoningCleanupChatClient.CleanModelText(response.Text ?? string.Empty);

            InsightResponseDto? parsed = null;
            if (!string.IsNullOrWhiteSpace(cleanedJson))
            {
                parsed = JsonSerializer.Deserialize<InsightResponseDto>(
                    cleanedJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }

            var summary = parsed?.Summary;
            if (String.IsNullOrWhiteSpace(summary))
            {
                throw new InvalidDataException("The insight provider did not return a summary.");
            }
            summaries.Add(summary);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var documents = documentMode
            ? ordered.Select((slice, index) => (Slice: slice, Summary: summaries[index])).ToArray()
            : Array.Empty<(DocumentSlice Slice, string Summary)>();
        var packetSummary = documentMode
            ? String.Join(Environment.NewLine + Environment.NewLine, documents.Select((document, index) =>
                $"Document {index + 1}: {document.Slice.IdentifiedDocumentName} " +
                $"(Pages {document.Slice.StartPage}-{document.Slice.EndPage})\n{document.Summary}"))
            : summaries[0];
        return new InsightBatch(packetSummary, documents, modelId, development);
    }

    private static void ApplyDocumentSummaries(IEnumerable<DocumentSlice> slices, InsightBatch result)
    {
        foreach (var slice in slices) slice.InsightSummary = null;
        foreach (var document in result.Documents) document.Slice.InsightSummary = document.Summary;
    }

    private sealed record InsightBatch(
        string PacketSummary,
        IReadOnlyList<(DocumentSlice Slice, string Summary)> Documents,
        string ModelId,
        bool Development);

    private sealed class InsightResponseDto
    {
        [JsonPropertyName("summary")]
        public string? Summary { get; set; }
    }
}
