using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace DocIntel.Core.Features.DocumentInsight;

/// <summary>
/// Feature 4 Executor: Generates per-slice and packet-level executive summaries using
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

        if (!message.Tenant.Capabilities.TryGetValue(FeatureHarness.DocumentInsightKey, out var feature) ||
            !feature.Enabled)
        {
            message.ExecutionTrace.Add(new WorkflowStepTrace(
                nameof(GenerateDocumentInsightsExecutor),
                Executed: false,
                Succeeded: true,
                ModelIdUsed: null,
                DurationMs: 0,
                Notes: "Skipped (Document Insight is disabled for this tenant)."));
            return message;
        }

        var sw = Stopwatch.StartNew();
        var config = feature.DocumentInsight ?? new DocumentInsightConfig();
        var summaryInstructions = !string.IsNullOrWhiteSpace(feature.CustomSystemPrompt)
            ? feature.CustomSystemPrompt!
            : config.SummaryInstructions;

        var modelId = _modelCatalog.ResolveModelId(message.Tenant, feature.ModelIdOverride);
        var chatClient = _modelCatalog.ResolveChatClient(message.Tenant, feature.ModelIdOverride);

        var chatOptions = new ChatOptions
        {
            Instructions = $"[TASK:DOCUMENT_INSIGHT]\nYou are an enterprise document insight assistant.\n{summaryInstructions}",
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

        var sliceSummaries = new List<string>();

        foreach (var slice in message.Slices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceContent = !string.IsNullOrWhiteSpace(slice.TranslatedText)
                ? slice.TranslatedText!
                : slice.BodyText;

            var response = await insightAgent.RunAsync(sourceContent, cancellationToken: cancellationToken);
            var cleanedJson = ReasoningCleanupChatClient.CleanModelText(response.Text ?? string.Empty);

            InsightResponseDto? parsed = null;
            if (!string.IsNullOrWhiteSpace(cleanedJson))
            {
                parsed = JsonSerializer.Deserialize<InsightResponseDto>(
                    cleanedJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }

            slice.InsightSummary = parsed?.Summary ?? cleanedJson;
            sliceSummaries.Add($"Slice #{slice.SegmentIndex + 1} ({slice.DocumentTypeName ?? "Document"}, Pages {slice.StartPage}-{slice.EndPage}): {slice.InsightSummary}");
        }

        message.PacketSummary = string.Join(Environment.NewLine, sliceSummaries);
        sw.Stop();

        message.ExecutionTrace.Add(new WorkflowStepTrace(
            nameof(GenerateDocumentInsightsExecutor),
            Executed: true,
            Succeeded: true,
            ModelIdUsed: modelId,
            DurationMs: sw.Elapsed.TotalMilliseconds,
            Notes: $"Generated insights for {message.Slices.Count} slice(s) using model '{modelId}'."));

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

        if (!tenant.Capabilities.TryGetValue(FeatureHarness.DocumentInsightKey, out var feature) || !feature.Enabled)
        {
            job.PacketSummary = null;
            foreach (var s in job.Segments)
            {
                s.InsightSummary = null;
            }
            return;
        }

        var config = feature.DocumentInsight ?? new DocumentInsightConfig();
        var summaryInstructions = !string.IsNullOrWhiteSpace(feature.CustomSystemPrompt)
            ? feature.CustomSystemPrompt!
            : config.SummaryInstructions;

        var chatClient = modelCatalog.ResolveChatClient(tenant, feature.ModelIdOverride);
        var chatOptions = new ChatOptions
        {
            Instructions = $"[TASK:DOCUMENT_INSIGHT]\nYou are an enterprise document insight assistant.\n{summaryInstructions}",
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

        var sliceSummaries = new List<string>();
        foreach (var slice in job.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceContent = !string.IsNullOrWhiteSpace(slice.TranslatedText)
                ? slice.TranslatedText!
                : slice.BodyText;

            var response = await insightAgent.RunAsync(sourceContent, cancellationToken: cancellationToken);
            var cleanedJson = ReasoningCleanupChatClient.CleanModelText(response.Text ?? string.Empty);

            InsightResponseDto? parsed = null;
            if (!string.IsNullOrWhiteSpace(cleanedJson))
            {
                parsed = JsonSerializer.Deserialize<InsightResponseDto>(
                    cleanedJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }

            slice.InsightSummary = parsed?.Summary ?? cleanedJson;
            sliceSummaries.Add($"Slice #{slice.SegmentIndex + 1} ({slice.IdentifiedDocumentName ?? slice.DocumentTypeName ?? "Document"}, Pages {slice.StartPage}-{slice.EndPage}): {slice.InsightSummary}");
        }

        job.PacketSummary = string.Join(Environment.NewLine, sliceSummaries);
    }

    private sealed class InsightResponseDto
    {
        [JsonPropertyName("summary")]
        public string? Summary { get; set; }
    }
}
