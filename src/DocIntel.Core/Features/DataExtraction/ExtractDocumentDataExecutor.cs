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
    private const int TableBatchSourceCharacterThreshold = 12_000;
    private const int TableBatchPageCount = 2;
    private const int MaximumConcurrentTableRequests = 2;
    private const string DefaultExtractionInstructions =
        "Extract structured key-value fields and tables strictly from this document slice according to the defined " +
        "field and column instructions below. Return empty string when a value is not present.";
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
                Notes: "Skipped (Data Extraction is disabled for this workspace)."));
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

            var sourceContent = DocumentPromptBoundary.SourceText(slice);
            var pages = message.Pages.Where(page => page.PageNumber >= slice.StartPage &&
                page.PageNumber <= slice.EndPage).OrderBy(page => page.PageNumber).ToArray();
            var parsed = !slice.WasTranslated && profile.Tables.Count > 0 &&
                sourceContent.Length > TableBatchSourceCharacterThreshold && pages.Length > TableBatchPageCount &&
                pages.Select(page => page.PageNumber).SequenceEqual(
                    Enumerable.Range(slice.StartPage, slice.EndPage - slice.StartPage + 1))
                ? await ExtractBatchesAsync(chatClient, profile, feature.CustomSystemPrompt,
                    sourceContent, pages, cancellationToken)
                : await ExtractAsync(chatClient, profile, feature.CustomSystemPrompt, sourceContent, cancellationToken);
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

    private static async Task<ParsedExtraction> ExtractBatchesAsync(IChatClient client,
        DocumentDataExtractionProfile profile, string? customPrompt, string source,
        PageLayout[] pages, CancellationToken token)
    {
        var headerProfile = new DocumentDataExtractionProfile
            { DocumentName = profile.DocumentName, Fields = profile.Fields };
        var header = profile.Fields.Count == 0 ? new ParsedExtraction([], []) :
            await ExtractAsync(client, headerProfile, customPrompt, source, token);
        if (header.Tables.Count != 0)
            throw new InvalidDataException("Header extraction returned unrequested tables.");
        var tableProfile = new DocumentDataExtractionProfile
            { DocumentName = profile.DocumentName, Tables = profile.Tables };
        var batches = pages.Chunk(TableBatchPageCount).ToArray();
        var results = new ParsedExtraction?[batches.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, batches.Length),
            new ParallelOptions { MaxDegreeOfParallelism = MaximumConcurrentTableRequests, CancellationToken = token },
            async (index, cancellation) =>
            {
                var batch = batches[index];
                var start = batch[0].PageNumber;
                var end = batch[^1].PageNumber;
                var content = String.Join("\n<!-- PageBreak -->\n", pages.Where(page =>
                    page.PageNumber >= start - 1 && page.PageNumber <= end + 1).Select(page =>
                    $"Original source page {page.PageNumber}:\n" +
                    (!String.IsNullOrWhiteSpace(page.Markdown) ? page.Markdown : page.BodyText)));
                var scopedInstructions = (String.IsNullOrWhiteSpace(customPrompt)
                    ? DefaultExtractionInstructions : customPrompt) +
                    $"\nExtract only rows starting on original source pages {start}-{end}. " +
                    "Adjacent source pages are context only: use them to complete a row that crosses a page boundary. " +
                    "Do not repeat rows that start on a context-only page. Never summarize or aggregate product rows.";
                var parsed = await ExtractAsync(client, tableProfile, scopedInstructions, content, cancellation);
                if (parsed.Fields.Count != 0 || parsed.Tables.Any(table => !profile.Tables.Any(expected =>
                        String.Equals(expected.TableName.Trim(), table.TableName?.Trim(), StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidDataException("Table extraction returned unrequested fields or tables.");
                results[index] = parsed;
            });
        token.ThrowIfCancellationRequested();
        var completed = results.OfType<ParsedExtraction>().ToArray();
        if (completed.Length != batches.Length)
            throw new InvalidDataException("Table extraction did not complete every source-page batch.");
        var merged = profile.Tables.Select(expected => new TableDto
        {
            TableName = expected.TableName,
            Rows = completed.SelectMany(result => result.Tables).Where(table =>
                    String.Equals(expected.TableName.Trim(), table.TableName?.Trim(), StringComparison.OrdinalIgnoreCase))
                .SelectMany(table => table.Rows ??
                    throw new InvalidDataException("A table batch returned no row collection.")).ToList()
        }).ToArray();
        return new(header.Fields, merged);
    }

    private static async Task<ParsedExtraction> ExtractAsync(IChatClient client,
        DocumentDataExtractionProfile profile, string? customPrompt, string content, CancellationToken token)
    {
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Id = "DataExtractionAgent", Name = "DataExtractionAgent",
            ChatOptions = new()
            {
                Instructions = BuildDataExtractionInstructions(profile, customPrompt),
                ResponseFormat = ChatResponseFormat.ForJsonSchema<DataExtractionResponseDto>(
                    schemaName: "document_data_extraction",
                    schemaDescription: "Structured key-value fields and tables extracted for a defined document")
            }
        });
        var response = await agent.RunAsync(DocumentPromptBoundary.Wrap(content), cancellationToken: token);
        var cleaned = ReasoningCleanupChatClient.CleanModelText(response.Text ?? String.Empty);
        var parsed = String.IsNullOrWhiteSpace(cleaned) ? null :
            JsonSerializer.Deserialize<DataExtractionResponseDto>(cleaned,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (parsed?.Fields is null || parsed.Tables is null ||
            parsed.Fields.Any(field => field is null || String.IsNullOrWhiteSpace(field.FieldName) ||
                !Double.IsFinite(field.ConfidenceValue) || field.ConfidenceValue is < 0 or > 100) ||
            parsed.Tables.Any(table => table is null || String.IsNullOrWhiteSpace(table.TableName) || table.Rows is null ||
                table.Rows.Any(row => row is null || row.Fields is null || row.Fields.Any(field =>
                    field is null || String.IsNullOrWhiteSpace(field.FieldName) ||
                    !Double.IsFinite(field.ConfidenceValue) || field.ConfidenceValue is < 0 or > 100))))
            throw new InvalidDataException("Structured extraction returned invalid fields, tables, or confidence signals.");
        return new(parsed.Fields, parsed.Tables);
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
            : DefaultExtractionInstructions);
        sb.AppendLine("Return only the configured field, table and column names; copy each name exactly.");
        if (profile.Fields.Count == 0) sb.AppendLine("No key-value fields are requested: return fields as an empty array.");
        if (profile.Tables.Count == 0) sb.AppendLine("No tables are requested: return tables as an empty array.");
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

    private sealed record ParsedExtraction(IReadOnlyList<FieldDto> Fields, IReadOnlyList<TableDto> Tables);

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
