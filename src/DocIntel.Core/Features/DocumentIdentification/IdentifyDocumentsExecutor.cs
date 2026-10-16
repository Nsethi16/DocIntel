using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace DocIntel.Core.Features.DocumentIdentification;

/// <summary>
/// Feature 2 Executor: Uses a Microsoft Decision Model (<c>phi-4</c> / <c>mai-reasoning</c>) via
/// <see cref="ChatClientAgent"/> to classify each split PDF slice into one of the tenant's admin-defined
/// document names (using each document's identification prompt) or <c>"Undefined"</c>.
/// </summary>
[SendsMessage(typeof(SplittingState))]
public sealed class IdentifyDocumentsExecutor : Executor<SplittingState, SplittingState>
{
    public const string UndefinedDocumentName = "Undefined";

    private readonly IModelCatalog _modelCatalog;

    public IdentifyDocumentsExecutor(IModelCatalog modelCatalog)
        : base(nameof(IdentifyDocumentsExecutor))
    {
        _modelCatalog = modelCatalog ?? throw new ArgumentNullException(nameof(modelCatalog));
    }

    public override async ValueTask<SplittingState> HandleAsync(
        SplittingState message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!message.Tenant.Capabilities.TryGetValue(FeatureHarness.IdentifyDocumentsKey, out var feature) ||
            !feature.Enabled)
        {
            message.ExecutionTrace.Add(new WorkflowStepTrace(
                nameof(IdentifyDocumentsExecutor),
                Executed: false,
                Succeeded: true,
                ModelIdUsed: null,
                DurationMs: 0,
                Notes: "Skipped (Identify Documents is disabled for this tenant)."));
            return message;
        }

        var sw = Stopwatch.StartNew();
        var config = feature.IdentifyDocuments ?? new DocumentIdentificationConfig();
        var activeRules = config.Rules
            .Where(r => r.IsActive && !string.IsNullOrWhiteSpace(r.DocumentName) && !string.IsNullOrWhiteSpace(r.IdentificationPrompt))
            .ToList();

        var preferredModel = !string.IsNullOrWhiteSpace(message.Tenant.DecisionModelId)
            ? message.Tenant.DecisionModelId
            : (!string.IsNullOrWhiteSpace(config.DecisionModelId) ? config.DecisionModelId : feature.ModelIdOverride);

        var modelId = _modelCatalog.ResolveModelId(message.Tenant, preferredModel);
        var chatClient = _modelCatalog.ResolveChatClient(message.Tenant, preferredModel);

        if (activeRules.Count == 0)
        {
            foreach (var slice in message.Slices)
            {
                slice.IdentifiedDocumentName = UndefinedDocumentName;
                slice.DocumentTypeName = UndefinedDocumentName;
                slice.IdentificationConfidence = 1.0;
                slice.MatchedIdentificationRule = null;
            }

            sw.Stop();
            message.ExecutionTrace.Add(new WorkflowStepTrace(
                nameof(IdentifyDocumentsExecutor),
                Executed: true,
                Succeeded: true,
                ModelIdUsed: modelId,
                DurationMs: sw.Elapsed.TotalMilliseconds,
                Notes: $"Marked {message.Slices.Count} slice(s) as '{UndefinedDocumentName}' (no active identification rules configured)."));
            return message;
        }

        var instructions = BuildDecisionModelInstructions(activeRules, feature.CustomSystemPrompt);
        var chatOptions = new ChatOptions
        {
            Instructions = instructions,
            ResponseFormat = ChatResponseFormat.ForJsonSchema<DecisionClassificationDto>(
                schemaName: "document_identification_decision",
                schemaDescription: "Closed-set classification of a PDF slice into a defined document name or Undefined")
        };

        var decisionAgent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Id = "DocumentIdentificationDecisionAgent",
                Name = "DocumentIdentificationDecisionAgent",
                ChatOptions = chatOptions
            });

        var ruleLookup = activeRules.ToDictionary(r => r.DocumentName.Trim(), r => r, StringComparer.OrdinalIgnoreCase);

        foreach (var slice in message.Slices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var topLines = slice.BodyText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Take(6);

            var slicePrompt = $"""
                Slice Pages: {slice.StartPage}-{slice.EndPage}
                Document Footers: {string.Join(", ", slice.Footers)}
                Document Title / Top Lines:
                {string.Join(Environment.NewLine, topLines)}
                """;

            var response = await decisionAgent.RunAsync(slicePrompt, cancellationToken: cancellationToken);
            var cleanedJson = ReasoningCleanupChatClient.CleanModelText(response.Text ?? string.Empty);

            DecisionClassificationDto? decision = null;
            if (!string.IsNullOrWhiteSpace(cleanedJson))
            {
                decision = JsonSerializer.Deserialize<DecisionClassificationDto>(
                    cleanedJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }

            var candidateName = decision?.IdentifiedDocumentName?.Trim();
            if (!string.IsNullOrWhiteSpace(candidateName) &&
                ruleLookup.TryGetValue(candidateName, out var matchedRule))
            {
                slice.IdentifiedDocumentName = matchedRule.DocumentName;
                slice.DocumentTypeName = matchedRule.DocumentName;
                slice.IsInferred = false;
                slice.IdentificationConfidence = decision?.Confidence ?? 0.95;
                slice.MatchedIdentificationRule = matchedRule.IdentificationPrompt;
            }
            else
            {
                slice.IdentifiedDocumentName = UndefinedDocumentName;
                slice.DocumentTypeName = UndefinedDocumentName;
                slice.IsInferred = true;
                slice.IdentificationConfidence = decision?.Confidence ?? 0.50;
                slice.MatchedIdentificationRule = null;
            }
        }

        sw.Stop();
        message.ModelIdUsed = modelId;
        message.ExecutionTrace.Add(new WorkflowStepTrace(
            nameof(IdentifyDocumentsExecutor),
            Executed: true,
            Succeeded: true,
            ModelIdUsed: modelId,
            DurationMs: sw.Elapsed.TotalMilliseconds,
            Notes: $"Classified {message.Slices.Count} slice(s) against {activeRules.Count} defined rule(s) using Microsoft Decision Model '{modelId}'."));

        return message;
    }

    private static string BuildDecisionModelInstructions(
        IReadOnlyList<DocumentIdentificationRule> activeRules,
        string? customPromptOverride)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[TASK:IDENTIFY_DOCUMENT]");
        sb.AppendLine(!string.IsNullOrWhiteSpace(customPromptOverride)
            ? customPromptOverride
            : "You are a Microsoft Decision Model for document identification. Evaluate the PDF slice's footers and title/top lines against the admin-defined identification rules below. Return the matching Document Name, or 'Undefined' if no rule matches.");
        sb.AppendLine();
        sb.AppendLine("Defined Document Identification Rules:");
        foreach (var rule in activeRules)
        {
            sb.Append("RULE|").Append(rule.DocumentName.Trim()).Append('|').AppendLine(rule.IdentificationPrompt.Trim());
        }
        sb.AppendLine($"Allowed values for identifiedDocumentName: [{string.Join(", ", activeRules.Select(r => $"\"{r.DocumentName}\""))}, \"{UndefinedDocumentName}\"]");
        return sb.ToString();
    }

    private sealed class DecisionClassificationDto
    {
        [JsonPropertyName("identifiedDocumentName")]
        public string? IdentifiedDocumentName { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("matchedRule")]
        public string? MatchedRule { get; set; }
    }
}
