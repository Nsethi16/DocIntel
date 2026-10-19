using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Features.AskAi;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AskAiChannelType
{
    Web,
    Email,
    Teams,
    Webhook
}

/// <summary>
/// Delivery channel metadata for omnichannel Ask AI triggers (Web UI, Email Mailbox, Microsoft Teams Bot).
/// </summary>
public sealed class AskAiReplyChannel
{
    public AskAiChannelType ChannelType { get; set; } = AskAiChannelType.Web;

    public string? FromAddress { get; set; }

    public string? Subject { get; set; }

    public string? MessageId { get; set; }

    public string? ConversationId { get; set; }

    public string? ServiceUrl { get; set; }

    public string? CallbackWebhookUrl { get; set; }
}

/// <summary>
/// Authoritative, verified document citation resolved from a matched <see cref="DocumentJob"/> and slice.
/// </summary>
public sealed record AskAiCitation(
    [property: JsonPropertyName("citationNumber")] int CitationNumber,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("cleanName")] string CleanName,
    [property: JsonPropertyName("sliceIndex")] int SliceIndex,
    [property: JsonPropertyName("documentName")] string DocumentName,
    [property: JsonPropertyName("pageRange")] string PageRange,
    [property: JsonPropertyName("customerName")] string CustomerName,
    [property: JsonPropertyName("mbsaNumber")] string MbsaNumber,
    [property: JsonPropertyName("contractNumber")] string ContractNumber,
    [property: JsonPropertyName("billingCurrency")] string BillingCurrency,
    [property: JsonPropertyName("totalDealValue")] string TotalDealValue,
    [property: JsonPropertyName("excerpt")] string Excerpt,
    [property: JsonPropertyName("workspaceUrl")] string WorkspaceUrl,
    [property: JsonPropertyName("pdfUrl")] string PdfUrl);

/// <summary>
/// Step trace recorded during an agentic Ask AI run (schema injection, Cosmos SQL generation, tool execution, citation resolution).
/// </summary>
public sealed record AskAiStepTrace(
    [property: JsonPropertyName("stepName")] string StepName,
    [property: JsonPropertyName("toolOrQuery")] string? ToolOrQuery,
    [property: JsonPropertyName("durationMs")] double DurationMs,
    [property: JsonPropertyName("notes")] string Notes);

/// <summary>
/// Tracked state of a short or long-running Ask AI execution across tenant documents.
/// </summary>
public sealed class AskAiRun
{
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    [JsonPropertyName("question")]
    public required string Question { get; init; }

    [JsonPropertyName("status")]
    public JobStatus Status { get; set; } = JobStatus.Queued;

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("modelIdUsed")]
    public string? ModelIdUsed { get; set; }

    [JsonPropertyName("answer")]
    public string? Answer { get; set; }

    [JsonPropertyName("executedCosmosQueries")]
    public List<string> ExecutedCosmosQueries { get; set; } = [];

    [JsonPropertyName("documentsEvaluated")]
    public int DocumentsEvaluated { get; set; }

    [JsonPropertyName("citations")]
    public List<AskAiCitation> Citations { get; set; } = [];

    [JsonPropertyName("steps")]
    public List<AskAiStepTrace> Steps { get; set; } = [];

    [JsonPropertyName("replyChannel")]
    public AskAiReplyChannel ReplyChannel { get; init; } = new();

    [JsonPropertyName("emailHtmlBody")]
    public string? EmailHtmlBody { get; set; }

    [JsonPropertyName("teamsAdaptiveCardJson")]
    public string? TeamsAdaptiveCardJson { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("completedAtUtc")]
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

/// <summary>
/// Extensibility hook allowing external/MCP tools to be registered into the Ask AI agent loop.
/// </summary>
public interface IAskAiToolProvider
{
    IReadOnlyList<AIFunction> GetTools(TenantHarness tenant);
}

public interface IAskAiRunStore
{
    Task UpsertRunAsync(AskAiRun run, CancellationToken cancellationToken = default);

    Task<AskAiRun?> GetRunAsync(string runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AskAiRun>> ListRunsByTenantAsync(string tenantId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryAskAiRunStore : IAskAiRunStore
{
    private readonly ConcurrentDictionary<string, AskAiRun> _runs = new(StringComparer.OrdinalIgnoreCase);

    public Task UpsertRunAsync(AskAiRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        _runs[run.RunId] = run;
        return Task.CompletedTask;
    }

    public Task<AskAiRun?> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        _runs.TryGetValue(runId, out var run);
        return Task.FromResult(run);
    }

    public Task<IReadOnlyList<AskAiRun>> ListRunsByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AskAiRun> list = _runs.Values
            .Where(r => string.Equals(r.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(50)
            .ToList();
        return Task.FromResult(list);
    }
}

public sealed record AskAiQueueTicket(string RunId, string TenantId);

public interface IAskAiQueue
{
    ValueTask EnqueueAsync(AskAiQueueTicket ticket, CancellationToken cancellationToken = default);

    IAsyncEnumerable<AskAiQueueTicket> ReadAllAsync(CancellationToken cancellationToken = default);
}

public sealed class ChannelAskAiQueue : IAskAiQueue
{
    private readonly Channel<AskAiQueueTicket> _channel = Channel.CreateUnbounded<AskAiQueueTicket>();

    public ValueTask EnqueueAsync(AskAiQueueTicket ticket, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(ticket, cancellationToken);

    public IAsyncEnumerable<AskAiQueueTicket> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// Dynamically builds the system instructions for Ask AI by injecting the tenant's active
/// Document Identification rules, Entity Extraction definitions, Data Extraction schemas,
/// Prompt Column definitions, and the Cosmos DB <c>jobs</c> container JSON schema.
/// </summary>
public static class AskAiPromptBuilder
{
    public static string BuildSystemPrompt(TenantHarness tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        TenantHarness.EnsureDefaultCapabilities(tenant);

        var sb = new StringBuilder();
        sb.AppendLine($"You are the Enterprise Document Intelligence Ask AI Agent for tenant '{tenant.DisplayName}' (tenantId: '{tenant.TenantId}').");
        sb.AppendLine("You have access to all processed documents, extracted entities, structured tables, and Prompt Column results stored in the Azure Cosmos DB 'jobs' container (partitioned by /tenantId).");
        sb.AppendLine();

        // 1. Defined Document Types
        sb.AppendLine("=== 1. DEFINED DOCUMENT TYPES ===");
        if (tenant.Capabilities.TryGetValue(FeatureHarness.IdentifyDocumentsKey, out var idCap) &&
            idCap.IdentifyDocuments?.Rules is { Count: > 0 } rules)
        {
            foreach (var r in rules)
            {
                sb.AppendLine($"- \"{r.DocumentName}\": {r.IdentificationPrompt}");
            }
        }
        sb.AppendLine("- \"Undefined\": Unmatched document slices");
        sb.AppendLine();

        // 2. Entity Extraction Schema
        sb.AppendLine("=== 2. EXTRACTED ENTITY DEFINITIONS ===");
        if (tenant.Capabilities.TryGetValue(FeatureHarness.EntityExtractionKey, out var exCap) &&
            exCap.EntityExtraction is not null)
        {
            sb.AppendLine($"Configured Entity Extraction Prompt: {exCap.EntityExtraction.ExtractionPrompt}");
        }
        sb.AppendLine("Standard Entity Names in c.extractedEntities[] and s.extractedEntities[]:");
        sb.AppendLine("- \"Customer Name\", \"MBA/MBSA Number\", \"Contract Number\", \"Quote Number\", \"Billing Currency\", \"Total Deal Value\", \"Tenant ID\", \"Document Heading\"");
        sb.AppendLine();

        // 3. Data Extraction Schemas
        sb.AppendLine("=== 3. STRUCTURED DATA EXTRACTION SCHEMAS ===");
        if (tenant.Capabilities.TryGetValue(FeatureHarness.DataExtractionKey, out var deCap) &&
            deCap.DataExtraction?.Profiles is { Count: > 0 } profiles)
        {
            foreach (var p in profiles)
            {
                var fieldNames = string.Join(", ", p.Fields.Select(f => $"{f.FieldName} ({f.Instruction})"));
                var tableNames = string.Join(", ", p.Tables.Select(t => $"{t.TableName}[{string.Join(",", t.Columns.Select(c => c.ColumnName))}]"));
                sb.AppendLine($"- Document \"{p.DocumentName}\": Fields=[{fieldNames}]; Tables=[{tableNames}]");
            }
        }
        sb.AppendLine();

        // 4. Prompt Column Definitions (Admin-defined custom prompts)
        sb.AppendLine("=== 4. ADMIN-DEFINED PROMPT COLUMNS (c.promptColumnResults[] & s.promptColumnResults[]) ===");
        if (tenant.Capabilities.TryGetValue(FeatureHarness.PromptColumnsKey, out var pcCap) &&
            pcCap.PromptColumns?.Columns is { Count: > 0 } cols)
        {
            foreach (var col in cols.Where(c => c.IsActive))
            {
                var filterDesc = (col.EntityFilters is { Count: > 0 } && !string.Equals(col.FilterMode, "All", StringComparison.OrdinalIgnoreCase))
                    ? string.Join($" {col.FilterMode.ToUpperInvariant()} ", col.EntityFilters.Select(f => $"{f.EntityName} {f.Operator} {f.ExpectedValue}".Trim()))
                    : "All Documents";
                sb.AppendLine($"- Column \"{col.ColumnName}\" (columnId: \"{col.ColumnId}\", EntityFilter: [{filterDesc}]): {col.PromptInstructions}");
            }
        }
        sb.AppendLine();

        // 5. Cosmos DB DocumentJob JSON Schema
        sb.AppendLine("=== 5. COSMOS DB 'jobs' CONTAINER JSON SCHEMA (PartitionKey: /tenantId) ===");
        sb.AppendLine("""
        Each document `c` in the Cosmos DB `jobs` container has the following structure:
        {
          "id": "<jobId>",
          "jobId": "<jobId>",
          "tenantId": "<tenantId>",
          "fileName": "Woodgrove-Bank-Global-MBSA-Packet.pdf",
          "status": "Completed",
          "totalPages": 3,
          "packetSummary": "...",
          "extractedEntities": [
            { "name": "Customer Name", "value": "Woodgrove Bank NA", "category": "Party" },
            { "name": "MBA/MBSA Number", "value": "U8821904", "category": "Identifier" },
            { "name": "Billing Currency", "value": "USD", "category": "Financial" },
            { "name": "Total Deal Value", "value": "1,850,000.00", "category": "Financial" }
          ],
          "promptColumnResults": [
            {
              "columnId": "col-summary",
              "columnName": "Summary",
              "filterMatched": true,
              "documentName": "Program Signature Form",
              "pageRange": "1-1",
              "resultText": "..."
            }
          ],
          "segments": [
            {
              "segmentIndex": 0,
              "identifiedDocumentName": "Program Signature Form",
              "startPage": 1,
              "endPage": 1,
              "detectedLanguage": "es",
              "wasTranslated": true,
              "insightSummary": "...",
              "bodyText": "...",
              "translatedText": "..."
            }
          ]
        }
        """);

        return sb.ToString();
    }
}

/// <summary>
/// Orchestrates multi-step Agentic Ask AI execution:
/// 1. Injects the tenant's <see cref="TenantHarness"/> schema and Prompt Column definitions.
/// 2. Generates a read-only Cosmos DB SQL query tailored to the user's question.
/// 3. Executes the Cosmos DB query (and any registered <see cref="IAskAiToolProvider"/> tools).
/// 4. Resolves verified <see cref="AskAiCitation"/> records with deep links.
/// 5. Synthesizes a user-friendly response with inline [1], [2] citations plus Email HTML and Teams Adaptive Card payloads.
/// </summary>
public sealed class AskAiOrchestrator
{
    private readonly IHarnessStore _harnessStore;
    private readonly IJobStore _jobStore;
    private readonly IModelCatalog _modelCatalog;
    private readonly IEnumerable<IAskAiToolProvider> _toolProviders;
    private readonly ILogger<AskAiOrchestrator> _logger;

    public AskAiOrchestrator(
        IHarnessStore harnessStore,
        IJobStore jobStore,
        IModelCatalog modelCatalog,
        IEnumerable<IAskAiToolProvider>? toolProviders,
        ILogger<AskAiOrchestrator> logger)
    {
        _harnessStore = harnessStore ?? throw new ArgumentNullException(nameof(harnessStore));
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _modelCatalog = modelCatalog ?? throw new ArgumentNullException(nameof(modelCatalog));
        _toolProviders = toolProviders ?? [];
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AskAiRun> ExecuteRunAsync(
        AskAiRun run,
        Func<AskAiRun, CancellationToken, Task>? onStepProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var tenant = await _harnessStore.GetTenantAsync(run.TenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant '{run.TenantId}' not found.");

        if (!tenant.IsActive ||
            !tenant.Capabilities.TryGetValue(FeatureHarness.AskAiKey, out var askFeature) || !askFeature.Enabled)
        {
            throw new InvalidOperationException("Ask AI is not enabled for this tenant.");
        }

        run.Status = JobStatus.Running;
        var modelId = _modelCatalog.ResolveModelId(tenant, askFeature.ModelIdOverride);
        run.ModelIdUsed = modelId;
        var chatClient = _modelCatalog.ResolveRequiredChatClient(tenant, askFeature.ModelIdOverride);

        // Step 1: Build Dynamic System Prompt with Tenant Schema & Prompt Column Definitions
        var stepSw = Stopwatch.StartNew();
        var systemPrompt = AskAiPromptBuilder.BuildSystemPrompt(tenant);
        var externalTools = _toolProviders.SelectMany(tp => tp.GetTools(tenant)).ToList();
        stepSw.Stop();

        run.Steps.Add(new AskAiStepTrace(
            "BuildTenantSchemaPrompt",
            ToolOrQuery: null,
            DurationMs: stepSw.Elapsed.TotalMilliseconds,
            Notes: $"Injected Cosmos DB 'jobs' schema, Prompt Column definitions, Entity definitions, and {externalTools.Count} external tool(s)."));
        if (onStepProgress is not null) await onStepProgress(run, cancellationToken);

        // Step 2: Generate Cosmos DB SQL Query via LLM
        stepSw.Restart();
        var generatedSql = await GenerateCosmosSqlForQuestionAsync(systemPrompt, tenant.TenantId, run.Question, chatClient, cancellationToken);
        stepSw.Stop();

        run.ExecutedCosmosQueries.Add(generatedSql);
        run.Steps.Add(new AskAiStepTrace(
            "GenerateCosmosSqlQuery",
            ToolOrQuery: generatedSql,
            DurationMs: stepSw.Elapsed.TotalMilliseconds,
            Notes: "Generated read-only Cosmos DB SQL query from natural-language question and tenant schema."));
        if (onStepProgress is not null) await onStepProgress(run, cancellationToken);

        // Step 3: Execute query_cosmos_documents Tool against Cosmos DB (PartitionKey: /tenantId)
        stepSw.Restart();
        var (jsonRows, matchedJobIds) = await _jobStore.ExecuteReadOnlyQueryAsync(tenant.TenantId, generatedSql, cancellationToken);
        stepSw.Stop();

        run.DocumentsEvaluated = matchedJobIds.Count;
        run.Steps.Add(new AskAiStepTrace(
            "ExecuteCosmosQueryTool",
            ToolOrQuery: "query_cosmos_documents",
            DurationMs: stepSw.Elapsed.TotalMilliseconds,
            Notes: $"Executed Cosmos SQL on partition '{tenant.TenantId}'; matched {matchedJobIds.Count} document(s)."));
        if (onStepProgress is not null) await onStepProgress(run, cancellationToken);

        // Step 4: Resolve Authoritative Document Citations (resolve_document_citations Tool)
        stepSw.Restart();
        var allJobs = await _jobStore.ListJobsByTenantAsync(tenant.TenantId, cancellationToken);
        var matchedJobs = allJobs
            .Where(j => matchedJobIds.Contains(j.JobId, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (matchedJobs.Count == 0)
        {
            matchedJobs = allJobs.Where(j => j.Status == JobStatus.Completed).ToList();
        }

        var citations = ResolveVerifiedCitations(tenant.TenantId, run.Question, matchedJobs);
        run.Citations = citations;
        stepSw.Stop();

        run.Steps.Add(new AskAiStepTrace(
            "ResolveDocumentCitationsTool",
            ToolOrQuery: "resolve_document_citations",
            DurationMs: stepSw.Elapsed.TotalMilliseconds,
            Notes: $"Resolved {citations.Count} verified document citation(s) with slice page ranges and deep links."));
        if (onStepProgress is not null) await onStepProgress(run, cancellationToken);

        // Step 5: Synthesize User-Friendly Response with Citations + Channel Payloads (Email HTML & Teams Adaptive Card)
        stepSw.Restart();
        var userFriendlyAnswer = await SynthesizeUserFriendlyCitedResponseAsync(
            systemPrompt,
            run.Question,
            generatedSql,
            jsonRows,
            citations,
            chatClient,
            cancellationToken);

        run.Answer = userFriendlyAnswer;
        run.EmailHtmlBody = FormatEmailHtmlResponse(tenant.DisplayName, run.Question, userFriendlyAnswer, citations);
        run.TeamsAdaptiveCardJson = FormatTeamsAdaptiveCardJson(tenant.DisplayName, run.Question, userFriendlyAnswer, citations);
        run.Status = JobStatus.Completed;
        run.CompletedAtUtc = DateTimeOffset.UtcNow;
        stepSw.Stop();

        run.Steps.Add(new AskAiStepTrace(
            "SynthesizeResponseAndChannelPayloads",
            ToolOrQuery: null,
            DurationMs: stepSw.Elapsed.TotalMilliseconds,
            Notes: $"Synthesized user-friendly response with {citations.Count} citation(s) and prepared Web/Email/Teams payloads."));

        // Optional webhook callback for Email / Teams / Webhook integrations
        if (!string.IsNullOrWhiteSpace(run.ReplyChannel.CallbackWebhookUrl) &&
            Uri.TryCreate(run.ReplyChannel.CallbackWebhookUrl, UriKind.Absolute, out var callbackUri) &&
            (callbackUri.Scheme == Uri.UriSchemeHttps || callbackUri.Scheme == Uri.UriSchemeHttp))
        {
            await DispatchWebhookCallbackAsync(callbackUri, run, cancellationToken);
        }

        return run;
    }

    private static async Task<string> GenerateCosmosSqlForQuestionAsync(
        string systemPrompt,
        string tenantId,
        string question,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var sqlInstructions =
            $"[TASK:ASK_AI_COSMOS_SQL]\n{systemPrompt}\n" +
            "Translate the user's question into a single read-only Azure Cosmos DB SQL SELECT query against container `c`.\n" +
            $"Always filter by `c.tenantId = '{tenantId}' AND c.status = 'Completed'`.\n" +
            "Return JSON with property `cosmosSql` containing the SELECT statement.";

        var options = new ChatOptions
        {
            Instructions = sqlInstructions,
            ResponseFormat = ChatResponseFormat.ForJsonSchema<CosmosSqlGenerationDto>(
                    schemaName: "cosmos_sql_query",
                    schemaDescription: "Read-only Cosmos DB SQL SELECT query answering the user question")
        };

        var response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, $"User Question: {question}")],
            options,
            cancellationToken);

        var cleaned = ReasoningCleanupChatClient.CleanModelText(response.Text ?? string.Empty);
        var parsed = JsonSerializer.Deserialize<CosmosSqlGenerationDto>(
            cleaned,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (String.IsNullOrWhiteSpace(parsed?.CosmosSql) ||
            !parsed.CosmosSql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The model did not return a valid document query.");
        }

        return parsed.CosmosSql.Trim();
    }

    internal static string BuildDeterministicCosmosSql(string tenantId, string question)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "what", "all", "documents", "document", "have", "the", "for", "with", "from", "are", "who",
            "show", "find", "list", "which", "compare", "across", "summarize", "summary", "tell", "about",
            "and", "or", "in", "of", "to", "is", "how", "many", "between", "any", "that", "this", "their",
            "customers", "customer", "total", "deal", "value", "values", "billing", "currency", "currencies",
            "agreements", "agreement", "contracts", "contract", "numbers", "number", "mba", "mbsa"
        };

        var terms = question
            .Split([' ', ',', '.', '?', '"', '\'', ':', ';', '(', ')', '/', '&'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !stopWords.Contains(w))
            .Take(4)
            .ToList();

        if (terms.Count == 0)
        {
            return $"SELECT c.jobId, c.fileName, c.packetSummary, c.extractedEntities, c.promptColumnResults, c.segments FROM c WHERE c.tenantId = '{tenantId}' AND c.status = 'Completed'";
        }

        var termFilters = terms.Select(t =>
        {
            var safe = t.Replace("'", "");
            return $"(CONTAINS(c.fileName, '{safe}', true) OR CONTAINS(c.packetSummary, '{safe}', true) OR EXISTS(SELECT VALUE e FROM e IN c.extractedEntities WHERE CONTAINS(e.value, '{safe}', true)) OR EXISTS(SELECT VALUE p FROM p IN c.promptColumnResults WHERE CONTAINS(p.resultText, '{safe}', true)))";
        });

        return $"SELECT c.jobId, c.fileName, c.packetSummary, c.extractedEntities, c.promptColumnResults, c.segments FROM c WHERE c.tenantId = '{tenantId}' AND c.status = 'Completed' AND ({string.Join(" OR ", termFilters)})";
    }

    private static List<AskAiCitation> ResolveVerifiedCitations(
        string tenantId,
        string question,
        IReadOnlyList<DocumentJob> candidateJobs)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "what", "all", "documents", "document", "have", "the", "for", "with", "from", "are", "who",
            "show", "find", "list", "which", "compare", "across", "summarize", "summary", "tell", "about",
            "and", "or", "in", "of", "to", "is", "how", "many", "between", "any", "that", "this", "their"
        };

        var qTerms = question
            .Split([' ', ',', '.', '?', '"', '\'', ':', ';', '(', ')', '/', '&'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2 && !stopWords.Contains(w))
            .ToList();

        string GetEntity(DocumentJob job, string entityName) =>
            job.Segments.SelectMany(s => s.ExtractedEntities)
                .Concat(job.ExtractedEntities)
                .FirstOrDefault(e => string.Equals(e.Name, entityName, StringComparison.OrdinalIgnoreCase))?.Value ?? "—";

        var ranked = candidateJobs
            .Select(j =>
            {
                var blob = $"{j.FileName} {j.PacketSummary} " +
                           string.Join(" ", j.ExtractedEntities.Select(e => $"{e.Name} {e.Value}")) + " " +
                           string.Join(" ", j.PromptColumnResults.Where(p => p.FilterMatched).Select(p => $"{p.ColumnName} {p.ResultText}")) + " " +
                           string.Join(" ", j.Segments.Select(s => $"{s.IdentifiedDocumentName} {s.DetectedLanguage} {(s.WasTranslated ? "spanish translated" : "")} {(s.IsAmendment ? "amendment m130" : "")} {s.TranslatedText} {s.BodyText}"));

                var score = qTerms.Count(t => blob.Contains(t, StringComparison.OrdinalIgnoreCase));
                var bestSlice = j.Segments
                    .OrderByDescending(s => qTerms.Count(t => ($"{s.IdentifiedDocumentName} {s.InsightSummary} {s.TranslatedText} {s.BodyText}").Contains(t, StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault() ?? j.Segments.FirstOrDefault() ?? new DocumentSlice();

                return (Job: j, Score: score, Slice: bestSlice);
            })
            .ToList();

        var filtered = ranked.Any(r => r.Score > 0)
            ? ranked.Where(r => r.Score > 0).OrderByDescending(r => r.Score).ThenBy(r => r.Job.FileName).ToList()
            : ranked.OrderBy(r => r.Job.FileName).ToList();

        var citations = new List<AskAiCitation>();
        int idx = 1;
        foreach (var (job, _, slice) in filtered.Take(10))
        {
            var cleanName = (job.FileName ?? "Document").Replace(".pdf", "", StringComparison.OrdinalIgnoreCase);
            var docName = slice.IdentifiedDocumentName ?? slice.DocumentTypeName ?? "Document Slice";
            var pageRange = $"p.{slice.StartPage}–{slice.EndPage}";

            var promptSummary = job.PromptColumnResults
                .FirstOrDefault(p => p.SegmentIndex == slice.SegmentIndex && p.FilterMatched)?.ResultText;
            var excerpt = !string.IsNullOrWhiteSpace(slice.InsightSummary)
                ? slice.InsightSummary!
                : (!string.IsNullOrWhiteSpace(promptSummary) ? promptSummary! : slice.BodyText);

            citations.Add(new AskAiCitation(
                CitationNumber: idx++,
                JobId: job.JobId,
                FileName: job.FileName ?? "Document.pdf",
                CleanName: cleanName,
                SliceIndex: slice.SegmentIndex,
                DocumentName: docName,
                PageRange: pageRange,
                CustomerName: GetEntity(job, "Customer Name"),
                MbsaNumber: GetEntity(job, "MBA/MBSA Number"),
                ContractNumber: GetEntity(job, "Contract Number"),
                BillingCurrency: GetEntity(job, "Billing Currency"),
                TotalDealValue: GetEntity(job, "Total Deal Value"),
                Excerpt: excerpt,
                WorkspaceUrl: $"/?tenant={Uri.EscapeDataString(tenantId)}&job={Uri.EscapeDataString(job.JobId)}",
                PdfUrl: $"/api/harness/tenants/{Uri.EscapeDataString(tenantId)}/jobs/{Uri.EscapeDataString(job.JobId)}/slices/{slice.SegmentIndex}/pdf"));
        }

        return citations;
    }

    private static async Task<string> SynthesizeUserFriendlyCitedResponseAsync(
        string systemPrompt,
        string question,
        string executedSql,
        IReadOnlyList<string> jsonRows,
        IReadOnlyList<AskAiCitation> citations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var citationsContext = new StringBuilder();
        foreach (var c in citations)
        {
            citationsContext.AppendLine($"[{c.CitationNumber}] Document: {c.CleanName} ({c.DocumentName}, {c.PageRange}) | Customer: {c.CustomerName} | MBSA: {c.MbsaNumber} | Contract: {c.ContractNumber} | Deal Value: {c.TotalDealValue} {c.BillingCurrency} | Summary: {c.Excerpt}");
        }

        var synthInstructions =
            $"[TASK:ASK_AI_SYNTHESIZE]\n{systemPrompt}\n" +
            "Answer the user's question in clear, user-friendly language using the Cosmos DB query results and resolved citations.\n" +
            "Include inline citation markers like [1], [2] referencing the provided citation numbers. Do NOT expose raw SQL or JSON in your answer.";

        var userPrompt =
                $"User Question: {question}\n\n" +
                $"Executed Cosmos SQL: {executedSql}\n" +
                $"Matched Citations ({citations.Count}):\n{citationsContext}\n" +
                $"Cosmos DB Rows Sample:\n{string.Join("\n", jsonRows.Take(8))}";

        var resp = await chatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, synthInstructions),
                    new ChatMessage(ChatRole.User, userPrompt)
            ],
            cancellationToken: cancellationToken);

        var text = ReasoningCleanupChatClient.CleanModelText(resp.Text ?? string.Empty);
        if (String.IsNullOrWhiteSpace(text) ||
            text.TrimStart().StartsWith("{\"segments\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The model did not return an Ask AI answer.");
        }

        return text;
    }

    internal static string BuildDeterministicCitedNarrative(string question, IReadOnlyList<AskAiCitation> citations)
    {
        if (citations.Count == 0)
        {
            return "No matching documents were found for your question.";
        }

        var distinctCustomers = citations
            .Select(c => c.CustomerName)
            .Where(c => c != "—")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Found **{citations.Count}** matching document{(citations.Count == 1 ? "" : "s")}" +
                      (distinctCustomers.Count > 0 ? $" across **{distinctCustomers.Count}** customer{(distinctCustomers.Count == 1 ? "" : "s")} ({string.Join(", ", distinctCustomers.Take(5))}):" : ":"));
        sb.AppendLine();

        foreach (var c in citations)
        {
            var metaParts = new List<string>();
            if (c.CustomerName != "—") metaParts.Add(c.CustomerName);
            if (c.MbsaNumber != "—") metaParts.Add($"MBSA {c.MbsaNumber}");
            if (c.ContractNumber != "—") metaParts.Add($"Contract {c.ContractNumber}");
            if (c.TotalDealValue != "—") metaParts.Add($"{c.TotalDealValue} {(c.BillingCurrency != "—" ? c.BillingCurrency : "USD")}");
            else if (c.BillingCurrency != "—") metaParts.Add(c.BillingCurrency);

            var metaSuffix = metaParts.Count > 0 ? $" ({string.Join(" · ", metaParts)})" : string.Empty;
            sb.AppendLine($"• **[{c.CitationNumber}] {c.CleanName}** — *{c.DocumentName}, {c.PageRange}*{metaSuffix}: {c.Excerpt}");
        }

        return sb.ToString().Trim();
    }

    internal static string FormatEmailHtmlResponse(
        string tenantDisplayName,
        string question,
        string markdownAnswer,
        IReadOnlyList<AskAiCitation> citations)
    {
        var htmlAnswer = System.Net.WebUtility.HtmlEncode(markdownAnswer)
            .Replace("\n", "<br/>");

        var citesHtml = string.Join("", citations.Select(c =>
            $"<li style='margin-bottom:6px;'><strong>[{c.CitationNumber}] {System.Net.WebUtility.HtmlEncode(c.CleanName)}</strong> ({System.Net.WebUtility.HtmlEncode(c.DocumentName)}, {System.Net.WebUtility.HtmlEncode(c.PageRange)}) — <a href='{c.PdfUrl}'>Open PDF</a> | <a href='{c.WorkspaceUrl}'>Open in Workspace</a></li>"));

        return $"""
        <div style="font-family:'Segoe UI',sans-serif;color:#16161d;max-width:680px;line-height:1.6;">
          <div style="font-size:12px;color:#5d6074;text-transform:uppercase;letter-spacing:0.05em;">{System.Net.WebUtility.HtmlEncode(tenantDisplayName)} · DocIntel Ask AI</div>
          <h3 style="margin:6px 0 14px 0;font-size:17px;">Q: {System.Net.WebUtility.HtmlEncode(question)}</h3>
          <div style="background:#fafafc;border:1px solid #e3e1ec;border-radius:10px;padding:16px;margin-bottom:16px;">
            {htmlAnswer}
          </div>
          <div style="font-size:12px;font-weight:600;color:#5d6074;text-transform:uppercase;margin-bottom:8px;">Document Citations ({citations.Count})</div>
          <ul style="padding-left:18px;font-size:13px;margin:0;">
            {citesHtml}
          </ul>
        </div>
        """;
    }

    internal static string FormatTeamsAdaptiveCardJson(
        string tenantDisplayName,
        string question,
        string markdownAnswer,
        IReadOnlyList<AskAiCitation> citations)
    {
        var actions = citations.Take(5).Select(c => new
        {
            type = "Action.OpenUrl",
            title = $"[{c.CitationNumber}] {c.CleanName} ({c.PageRange})",
            url = c.WorkspaceUrl
        }).ToArray();

        var card = new
        {
            type = "AdaptiveCard",
            version = "1.5",
            body = new object[]
            {
                new { type = "TextBlock", text = $"{tenantDisplayName} · DocIntel Ask AI", weight = "Bolder", size = "Small", color = "Accent" },
                new { type = "TextBlock", text = question, weight = "Bolder", size = "Medium", wrap = true },
                new { type = "TextBlock", text = markdownAnswer, wrap = true, spacing = "Medium" }
            },
            actions
        };

        return JsonSerializer.Serialize(card, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task DispatchWebhookCallbackAsync(Uri callbackUri, AskAiRun run, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            await http.PostAsJsonAsync(callbackUri, new
            {
                run.RunId,
                run.TenantId,
                run.Question,
                status = run.Status.ToString(),
                run.Answer,
                run.Citations,
                run.EmailHtmlBody,
                run.TeamsAdaptiveCardJson,
                run.ReplyChannel
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook callback delivery failed for AskAiRun '{RunId}' to '{Uri}'.", run.RunId, callbackUri);
        }
    }

    private sealed class CosmosSqlGenerationDto
    {
        [JsonPropertyName("cosmosSql")]
        public string? CosmosSql { get; set; }
    }
}
