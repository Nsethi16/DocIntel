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
/// Classifies split slices using the configured decision model. Microsoft Decision uses its native
/// closed-choice scoring API; explicitly selected chat models retain their structured-output path.
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
                Notes: "Skipped (Identify Documents is disabled for this workspace)."));
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
        if (activeRules.Count == 0)
        {
            foreach (var slice in message.Slices)
            {
                slice.IdentifiedDocumentName = UndefinedDocumentName;
                slice.DocumentTypeName = UndefinedDocumentName;
                slice.IdentificationConfidence = 0;
                slice.MatchedIdentificationRule = null;
            }

            sw.Stop();
            message.ExecutionTrace.Add(new WorkflowStepTrace(
                nameof(IdentifyDocumentsExecutor),
                Executed: true,
                Succeeded: true,
                ModelIdUsed: null,
                DurationMs: sw.Elapsed.TotalMilliseconds,
                Notes: $"Marked {message.Slices.Count} slice(s) as '{UndefinedDocumentName}' (no active identification rules configured)."));
            return message;
        }

        using var nativeDecision = _modelCatalog.ResolveDecisionClient(message.Tenant, preferredModel);
        using var chatClient = nativeDecision is null
            ? _modelCatalog.ResolveChatClient(message.Tenant, preferredModel) : null;
        message.HasDevelopmentResults |= _modelCatalog.UsesDevelopmentSimulation(message.Tenant, preferredModel);
        var instructions = BuildDecisionModelInstructions(activeRules, feature.CustomSystemPrompt);
        var chatOptions = new ChatOptions
        {
            Instructions = instructions,
            ResponseFormat = ChatResponseFormat.ForJsonSchema<DecisionClassificationDto>(
                schemaName: "document_identification_decision",
                schemaDescription: "Closed-set classification of a PDF slice into a defined document name or Undefined")
        };

        var decisionAgent = chatClient is null ? null : new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Id = "DocumentIdentificationDecisionAgent",
                Name = "DocumentIdentificationDecisionAgent",
                ChatOptions = chatOptions
            });

        var ruleLookup = activeRules.ToDictionary(r => r.DocumentName.Trim(), r => r, StringComparer.OrdinalIgnoreCase);
        var choiceRules = activeRules.Select((rule, index) => (Key: $"form_{index}", Rule: rule))
            .ToDictionary(entry => entry.Key, entry => entry.Rule, StringComparer.Ordinal);
        var criteria = choiceRules.ToDictionary(entry => entry.Key,
            entry => $"Document name: {entry.Value.DocumentName.Trim()}\nIdentification criteria: {entry.Value.IdentificationPrompt.Trim()}",
            StringComparer.Ordinal);
        criteria.Add("undefined", "No defined form matches, or the document evidence is insufficient to identify one.");
        var nativeInstructions = "Identify the form using the supplied criteria. Select exactly one option. " +
            "Use undefined when none matches or the supplied evidence is insufficient.\n" +
            (feature.CustomSystemPrompt ?? String.Empty) + "\n" + DocumentPromptBoundary.Policy;
        var staged = new List<(DocumentSlice Slice, string Name, double Confidence, string? Rule, bool Inferred)>();
        string? nativeDeployment = null;

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

            if (nativeDecision is not null)
            {
                var result = await nativeDecision.ChooseAsync(DocumentPromptBoundary.Wrap(slicePrompt),
                    nativeInstructions, criteria, cancellationToken);
                if (!Double.IsFinite(result.Probability) || result.Probability is < 0 or > 1)
                    throw new InvalidDataException("The decision model returned an invalid form probability.");
                if (choiceRules.TryGetValue(result.Choice, out var nativeRule))
                    staged.Add((slice, nativeRule.DocumentName, result.Probability, nativeRule.IdentificationPrompt, false));
                else if (String.Equals(result.Choice, "undefined", StringComparison.Ordinal))
                    staged.Add((slice, UndefinedDocumentName, result.Probability, null, true));
                else
                    throw new InvalidDataException("The decision model returned an unsupported form choice.");
                nativeDeployment = result.DeploymentName;
                continue;
            }
            var response = await decisionAgent!.RunAsync(
                DocumentPromptBoundary.Wrap(slicePrompt), cancellationToken: cancellationToken);
            var cleanedJson = ReasoningCleanupChatClient.CleanModelText(response.Text ?? string.Empty);

            DecisionClassificationDto? decision = null;
            if (!string.IsNullOrWhiteSpace(cleanedJson))
            {
                decision = JsonSerializer.Deserialize<DecisionClassificationDto>(
                    cleanedJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }

            var candidateName = decision?.IdentifiedDocumentName?.Trim();
            if (String.IsNullOrWhiteSpace(candidateName) || decision is null ||
                (!String.Equals(candidateName, UndefinedDocumentName, StringComparison.OrdinalIgnoreCase) &&
                 !ruleLookup.ContainsKey(candidateName)) ||
                !Double.IsFinite(decision.Confidence) || decision.Confidence is < 0 or > 1)
            {
                throw new InvalidDataException("Document identification returned an invalid closed-set decision.");
            }
            if (!string.IsNullOrWhiteSpace(candidateName) &&
                ruleLookup.TryGetValue(candidateName, out var matchedRule))
            {
                staged.Add((slice, matchedRule.DocumentName, decision.Confidence, matchedRule.IdentificationPrompt, false));
            }
            else
            {
                staged.Add((slice, UndefinedDocumentName, decision.Confidence, null, true));
            }
        }

        foreach (var result in staged)
        {
            result.Slice.IdentifiedDocumentName = result.Name;
            result.Slice.DocumentTypeName = result.Name;
            result.Slice.IsInferred = result.Inferred;
            result.Slice.IdentificationConfidence = result.Confidence;
            result.Slice.MatchedIdentificationRule = result.Rule;
        }
        sw.Stop();
        message.ModelIdUsed = modelId;
        message.ExecutionTrace.Add(new WorkflowStepTrace(
            nameof(IdentifyDocumentsExecutor),
            Executed: true,
            Succeeded: true,
            ModelIdUsed: modelId,
            DurationMs: sw.Elapsed.TotalMilliseconds,
            Notes: nativeDeployment is not null
                ? $"Classified {message.Slices.Count} slice(s) with Microsoft Decision deployment '{nativeDeployment}' using native choice scoring."
                : $"Classified {message.Slices.Count} slice(s) against {activeRules.Count} rules using configured chat model '{modelId}'."));

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
        sb.AppendLine(DocumentPromptBoundary.Policy);
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
