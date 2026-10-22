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

namespace DocIntel.Core.Features.DataExtraction;

/// <summary>
/// Feature 6 Executor: Runs per-document structured Data Extraction (key-value fields + tables with columns)
/// on each PDF slice based on its identified document name and the admin's defined <see cref="DocumentDataExtractionProfile"/>.
/// </summary>
[SendsMessage(typeof(SplittingState))]
public sealed class ExtractDocumentDataExecutor : Executor<SplittingState, SplittingState>
{
    private readonly IModelCatalog _modelCatalog;

    public ExtractDocumentDataExecutor(IModelCatalog modelCatalog)
        : base(nameof(ExtractDocumentDataExecutor))
    {
        _modelCatalog = modelCatalog ?? throw new ArgumentNullException(nameof(modelCatalog));
    }

    public override async ValueTask<SplittingState> HandleAsync(
        SplittingState message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!message.Tenant.Capabilities.TryGetValue(FeatureHarness.DataExtractionKey, out var feature) ||
            !feature.Enabled)
        {
            message.ExecutionTrace.Add(new WorkflowStepTrace(
                nameof(ExtractDocumentDataExecutor),
                Executed: false,
                Succeeded: true,
                ModelIdUsed: null,
                DurationMs: 0,
                Notes: "Skipped (Data Extraction is disabled for this tenant)."));
            return message;
        }

        var sw = Stopwatch.StartNew();
        var config = feature.DataExtraction ?? new DataExtractionConfig();
        var profilesByDocName = config.Profiles
            .Where(p => !string.IsNullOrWhiteSpace(p.DocumentName))
            .ToDictionary(p => p.DocumentName.Trim(), p => p, StringComparer.OrdinalIgnoreCase);

        if (!message.Slices.Any(slice => profilesByDocName.ContainsKey(
            (slice.IdentifiedDocumentName ?? slice.DocumentTypeName ?? "Undefined").Trim())))
        {
            message.ExecutionTrace.Add(new WorkflowStepTrace(
                nameof(ExtractDocumentDataExecutor), false, true, null, sw.Elapsed.TotalMilliseconds,
                "Skipped (No matching document data-extraction profile)."));
            return message;
        }

        var modelId = _modelCatalog.ResolveModelId(message.Tenant, feature.ModelIdOverride);
        using var chatClient = _modelCatalog.ResolveChatClient(message.Tenant, feature.ModelIdOverride);
        message.HasDevelopmentResults |= _modelCatalog.UsesDevelopmentSimulation(message.Tenant, feature.ModelIdOverride);

        var extractedDocs = new List<DocumentDataExtractionResult>();

        foreach (var slice in message.Slices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var docName = !string.IsNullOrWhiteSpace(slice.IdentifiedDocumentName)
                ? slice.IdentifiedDocumentName!
                : (slice.DocumentTypeName ?? "Undefined");

            if (!profilesByDocName.TryGetValue(docName.Trim(), out var profile))
            {
                continue;
            }

            var instructions = BuildDataExtractionInstructions(profile, feature.CustomSystemPrompt);
            var chatOptions = new ChatOptions
            {
                Instructions = instructions,
                ResponseFormat = ChatResponseFormat.ForJsonSchema<DataExtractionResponseDto>(
                    schemaName: "document_data_extraction",
                    schemaDescription: "Structured key-value fields and tables extracted for a defined document")
            };

            var agent = new ChatClientAgent(
                chatClient,
                new ChatClientAgentOptions
                {
                    Id = "DataExtractionAgent",
                    Name = "DataExtractionAgent",
                    ChatOptions = chatOptions
                });

            var sourceContent = DocumentPromptBoundary.SourceText(slice);

            var response = await agent.RunAsync(
                DocumentPromptBoundary.Wrap(sourceContent), cancellationToken: cancellationToken);
            var cleanedJson = ReasoningCleanupChatClient.CleanModelText(response.Text ?? string.Empty);

            DataExtractionResponseDto? parsed = null;
            if (!string.IsNullOrWhiteSpace(cleanedJson))
            {
                parsed = JsonSerializer.Deserialize<DataExtractionResponseDto>(
                    cleanedJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }

            if (parsed?.Fields is null || parsed.Tables is null ||
                parsed.Fields.Any(f => f is null || String.IsNullOrWhiteSpace(f.FieldName) ||
                    !Double.IsFinite(f.ConfidenceValue) || f.ConfidenceValue is < 0 or > 100) ||
                parsed.Tables.Any(t => t is null || String.IsNullOrWhiteSpace(t.TableName) || t.Rows is null ||
                    t.Rows.Any(r => r is null || r.Fields is null || r.Fields.Any(f =>
                        f is null || String.IsNullOrWhiteSpace(f.FieldName) || !Double.IsFinite(f.ConfidenceValue) ||
                        f.ConfidenceValue is < 0 or > 100))))
            {
                throw new InvalidDataException("Structured extraction returned invalid fields, tables, or confidence signals.");
            }
            var fields = parsed.Fields
                .Where(f => !string.IsNullOrWhiteSpace(f.FieldName))
                .Select(f => new ExtractedFieldValue(
                    f.FieldName!.Trim(),
                    f.FieldValue?.Trim() ?? string.Empty,
                    f.ConfidenceValue))
                .ToList();

            var tables = parsed.Tables
                .Where(t => !string.IsNullOrWhiteSpace(t.TableName))
                .Select(t => new ExtractedTableValue(
                    t.TableName!.Trim(),
                    (t.Rows ?? [])
                        .Select(r => new ExtractedTableRow(
                            (r.Fields ?? [])
                                .Where(c => !string.IsNullOrWhiteSpace(c.FieldName))
                                .Select(c => new ExtractedFieldValue(
                                    c.FieldName!.Trim(),
                                    c.FieldValue?.Trim() ?? string.Empty,
                                    c.ConfidenceValue))
                                .ToList()))
                        .ToList()))
                .ToList();

            var docResult = new DocumentDataExtractionResult(
                DocumentName: profile.DocumentName,
                FooterId: string.Join(", ", slice.Footers),
                PageRange: $"{slice.StartPage}-{slice.EndPage}",
                Fields: fields,
                Tables: tables);

            slice.DataExtraction = docResult;
            extractedDocs.Add(docResult);
        }

        message.DataExtractions = extractedDocs;
        sw.Stop();

        message.ExecutionTrace.Add(new WorkflowStepTrace(
            nameof(ExtractDocumentDataExecutor),
            Executed: true,
            Succeeded: true,
            ModelIdUsed: modelId,
            DurationMs: sw.Elapsed.TotalMilliseconds,
            Notes: $"Extracted structured data (fields + tables) for {extractedDocs.Count} of {message.Slices.Count} slice(s) using model '{modelId}'."));

        return message;
    }

    private static string BuildDataExtractionInstructions(
        DocumentDataExtractionProfile profile,
        string? customPromptOverride)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[TASK:DATA_EXTRACTION]");
        sb.AppendLine($"DOCUMENT_NAME|{profile.DocumentName}");
        sb.AppendLine(!string.IsNullOrWhiteSpace(customPromptOverride)
            ? customPromptOverride
            : "Extract structured key-value fields and tables strictly from this document slice according to the defined field and column instructions below. Return empty string when a value is not present.");
        sb.AppendLine();

        if (profile.Fields.Count > 0)
        {
            sb.AppendLine("Key-Value Fields to Extract:");
            foreach (var field in profile.Fields)
            {
                sb.Append("FIELD|").Append(field.FieldName.Trim()).Append('|').AppendLine(field.Instruction.Trim());
            }
        }

        if (profile.Tables.Count > 0)
        {
            sb.AppendLine("Tables to Extract:");
            foreach (var table in profile.Tables)
            {
                sb.Append("TABLE|").Append(table.TableName.Trim()).Append('|').AppendLine(table.Instruction.Trim());
                foreach (var col in table.Columns)
                {
                    sb.Append("COLUMN|").Append(table.TableName.Trim()).Append('|').Append(col.ColumnName.Trim()).Append('|').AppendLine(col.Instruction.Trim());
                }
            }
        }

        sb.AppendLine(DocumentPromptBoundary.Policy);
        return sb.ToString();
    }

    private sealed class DataExtractionResponseDto
    {
        [JsonPropertyName("fields")]
        public List<FieldDto>? Fields { get; set; }

        [JsonPropertyName("tables")]
        public List<TableDto>? Tables { get; set; }
    }

    private sealed class FieldDto
    {
        [JsonPropertyName("fieldName")]
        public string? FieldName { get; set; }

        [JsonPropertyName("fieldValue")]
        public string? FieldValue { get; set; }

        [JsonPropertyName("confidenceValue")]
        public double ConfidenceValue { get; set; }
    }

    private sealed class TableDto
    {
        [JsonPropertyName("tableName")]
        public string? TableName { get; set; }

        [JsonPropertyName("rows")]
        public List<RowDto>? Rows { get; set; }
    }

    private sealed class RowDto
    {
        [JsonPropertyName("fields")]
        public List<FieldDto>? Fields { get; set; }
    }
}
