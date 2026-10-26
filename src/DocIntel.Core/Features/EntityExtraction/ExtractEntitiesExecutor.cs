using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace DocIntel.Core.Features.EntityExtraction;

/// <summary>
/// Feature 5 Executor: Extracts structured entities from each PDF slice using <see cref="ChatClientAgent"/>
/// guided by the admin's configured <see cref="EntityExtractionConfig.ExtractionPrompt"/>.
/// </summary>
[SendsMessage(typeof(SplittingState))]
public sealed class ExtractEntitiesExecutor : Executor<SplittingState, SplittingState>
{
    private static readonly JsonElement ResponseSchema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["entities"],
          "properties": {
            "entities": {
              "type": "array",
              "description": "Only entities supported by the document. Omit absent values; return [] if none are found.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name", "value", "category", "confidence"],
                "properties": {
                  "name": { "type": "string", "description": "A non-empty entity label." },
                  "value": { "type": "string", "description": "A non-empty value present in the document." },
                  "category": { "type": "string", "description": "A non-empty category, or General." },
                  "confidence": {
                    "type": "number",
                    "description": "An uncalibrated confidence signal from 0 to 1, never a percentage from 0 to 100."
                  }
                }
              }
            }
          }
        }
        """);
    private readonly IModelCatalog _modelCatalog;

    public ExtractEntitiesExecutor(IModelCatalog modelCatalog)
        : base(nameof(ExtractEntitiesExecutor))
    {
        _modelCatalog = modelCatalog ?? throw new ArgumentNullException(nameof(modelCatalog));
    }

    public override async ValueTask<SplittingState> HandleAsync(
        SplittingState message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!message.Tenant.Capabilities.TryGetValue(FeatureHarness.EntityExtractionKey, out var feature) ||
            !feature.Enabled)
        {
            message.ExecutionTrace.Add(new WorkflowStepTrace(
                nameof(ExtractEntitiesExecutor),
                Executed: false,
                Succeeded: true,
                ModelIdUsed: null,
                DurationMs: 0,
                Notes: "Skipped (Entity Extraction is disabled for this tenant)."));
            return message;
        }

        var sw = Stopwatch.StartNew();
        var config = feature.EntityExtraction ?? new EntityExtractionConfig();
        var extractionPrompt = !string.IsNullOrWhiteSpace(feature.CustomSystemPrompt)
            ? feature.CustomSystemPrompt!
            : config.ExtractionPrompt;

        var modelId = _modelCatalog.ResolveModelId(message.Tenant, feature.ModelIdOverride);
        using var chatClient = _modelCatalog.ResolveChatClient(message.Tenant, feature.ModelIdOverride);
        message.HasDevelopmentResults |= _modelCatalog.UsesDevelopmentSimulation(message.Tenant, feature.ModelIdOverride);

        var chatOptions = new ChatOptions
        {
            Instructions = $"[TASK:ENTITY_EXTRACTION]\nYou are a structured entity extraction specialist.\n" +
                $"{extractionPrompt}\n" +
                "Return only entities whose non-empty values are present in the document. " +
                "Omit requested entities that are absent; do not emit null, empty, unknown or not-found placeholders. " +
                "If none are found, return {\"entities\":[]}. " +
                "Use confidence from 0 to 1 (for example, 0.95), never a percentage such as 95. " +
                "These are uncalibrated signals, not verified probabilities.\n" +
                DocumentPromptBoundary.Policy,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(ResponseSchema,
                schemaName: "extracted_entities_response",
                schemaDescription: "Structured list of entities extracted from the document slice")
        };

        var extractionAgent = new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Id = "EntityExtractionAgent",
                Name = "EntityExtractionAgent",
                ChatOptions = chatOptions
            });

        var allEntities = new List<ExtractedEntity>();
        var stagedEntities = new List<(DocumentSlice Slice, List<ExtractedEntity> Entities)>();

        foreach (var slice in message.Slices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceContent = DocumentPromptBoundary.SourceText(slice);

            var response = await extractionAgent.RunAsync(
                DocumentPromptBoundary.Wrap(sourceContent), cancellationToken: cancellationToken);
            var cleanedJson = ReasoningCleanupChatClient.CleanModelText(response.Text ?? string.Empty);

            EntityExtractionEnvelopeDto? parsed = null;
            if (!string.IsNullOrWhiteSpace(cleanedJson))
            {
                parsed = JsonSerializer.Deserialize<EntityExtractionEnvelopeDto>(
                    cleanedJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }

            if (parsed?.Entities is null || parsed.Entities.Any(e => e is null ||
                String.IsNullOrWhiteSpace(e.Name) || String.IsNullOrWhiteSpace(e.Value) ||
                !Double.IsFinite(e.Confidence) || e.Confidence is < 0 or > 1))
            {
                throw new InvalidDataException("Entity extraction returned an invalid entities collection.");
            }
            var sliceEntities = parsed.Entities
                .Select(e => new ExtractedEntity(
                    e.Name!.Trim(),
                    e.Value!.Trim(),
                    e.Category ?? "General",
                    e.Confidence,
                    slice.SegmentIndex))
                .ToList();

            stagedEntities.Add((slice, sliceEntities));
            allEntities.AddRange(sliceEntities);
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var staged in stagedEntities) staged.Slice.ExtractedEntities = staged.Entities;
        message.ExtractedEntities = allEntities;
        sw.Stop();

        message.ExecutionTrace.Add(new WorkflowStepTrace(
            nameof(ExtractEntitiesExecutor),
            Executed: true,
            Succeeded: true,
            ModelIdUsed: modelId,
            DurationMs: sw.Elapsed.TotalMilliseconds,
            Notes: $"Extracted {allEntities.Count} entity(ies) across {message.Slices.Count} slice(s) using model '{modelId}'."));

        return message;
    }

    private sealed class EntityExtractionEnvelopeDto
    {
        [JsonPropertyName("entities")]
        public List<ExtractedEntityDto>? Entities { get; set; }
    }

    private sealed class ExtractedEntityDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }
    }
}
