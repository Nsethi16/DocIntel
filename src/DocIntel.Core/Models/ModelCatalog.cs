using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure.AI.Inference;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using DocIntel.Core.Harness;
using Microsoft.Extensions.AI;
using OpenAI;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace DocIntel.Core.Models;

/// <summary>
/// Identifies the backing AI provider family for a model in the catalog.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelProvider
{
    /// <summary>
    /// Microsoft Foundry Unified Inference API (supports Meta Llama, DeepSeek, Mistral, Microsoft Phi-4, MAI, Cohere).
    /// </summary>
    FoundryInference,

    /// <summary>
    /// Azure OpenAI / Foundry OpenAI deployments (gpt-5-mini, gpt-4o, o3-mini).
    /// </summary>
    AzureOpenAI,

    /// <summary>
    /// Any OpenAI-compatible HTTP endpoint (Foundry Local, Ollama, vLLM, Anthropic Claude Gateway).
    /// </summary>
    OpenAICompatible,

    /// <summary>
    /// Built-in deterministic simulator for offline development and unit testing.
    /// </summary>
    Simulated
}

/// <summary>
/// Metadata and connection settings for a selectable model in the Agent Harness catalog.
/// </summary>
public sealed record ModelDescriptor(
    string ModelId,
    string DisplayName,
    string Family,
    ModelProvider Provider,
    string DeploymentName,
    string? Endpoint = null,
    bool SupportsPdfInputs = false);

/// <summary>
/// Resolves provider-agnostic <see cref="IChatClient"/> instances for any model in the catalog.
/// </summary>
public interface IModelCatalog
{
    bool AllowsDevelopmentSimulation { get; }

    bool UsesDevelopmentSimulation(TenantHarness tenant, string? modelOverride = null);

    bool SupportsPdfInputs(TenantHarness tenant, string? modelOverride = null);

    IReadOnlyList<ModelDescriptor> ListModels();

    string ResolveModelId(TenantHarness tenant, string? modelOverride = null);

    IChatClient ResolveChatClient(TenantHarness tenant, string? modelOverride = null);

    /// <summary>
    /// Resolves a configured model without simulated or cross-provider fallback.
    /// </summary>
    IChatClient ResolveRequiredChatClient(TenantHarness tenant, string? modelOverride = null);
}

/// <summary>
/// Default implementation of <see cref="IModelCatalog"/> supporting Microsoft Foundry Inference (Llama, DeepSeek, Mistral, Phi-4),
/// Azure OpenAI, local OpenAI-compatible endpoints (Foundry Local / Ollama), and offline simulation.
/// </summary>
public sealed class ModelCatalog : IModelCatalog
{
    private readonly IReadOnlyList<ModelDescriptor> _descriptors;
    private readonly Dictionary<string, IChatClient> _customClients;
    private readonly TokenCredential _credential;

    public ModelCatalog(
        IEnumerable<ModelDescriptor>? descriptors = null,
        IDictionary<string, IChatClient>? customClients = null,
        TokenCredential? credential = null,
        bool allowDevelopmentSimulation = false)
    {
        _descriptors = (descriptors ?? CreateDefaultDescriptors()).ToList();
        AllowsDevelopmentSimulation = allowDevelopmentSimulation;
        _customClients = customClients is not null
            ? new Dictionary<string, IChatClient>(customClients, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, IChatClient>(StringComparer.OrdinalIgnoreCase);

        var managedIdentityClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        _credential = credential ?? (!string.IsNullOrWhiteSpace(managedIdentityClientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId))
            : new DefaultAzureCredential());
    }

    public IReadOnlyList<ModelDescriptor> ListModels() => _descriptors;

    public bool AllowsDevelopmentSimulation { get; }

    public bool UsesDevelopmentSimulation(TenantHarness tenant, string? modelOverride = null)
    {
        var id = ResolveModelId(tenant, modelOverride);
        if (_customClients.TryGetValue(id, out var client)) return client is SimulatedSliceChatClient;
        var descriptor = _descriptors.FirstOrDefault(d => String.Equals(d.ModelId, id, StringComparison.OrdinalIgnoreCase));
        return AllowsDevelopmentSimulation && (descriptor is null ||
            descriptor.Provider == ModelProvider.Simulated || String.IsNullOrWhiteSpace(descriptor.Endpoint));
    }

    public bool SupportsPdfInputs(TenantHarness tenant, string? modelOverride = null)
    {
        var id = ResolveModelId(tenant, modelOverride);
        return _descriptors.FirstOrDefault(d =>
            String.Equals(d.ModelId, id, StringComparison.OrdinalIgnoreCase))?.SupportsPdfInputs == true;
    }

    public string ResolveModelId(TenantHarness tenant, string? modelOverride = null)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var target = !string.IsNullOrWhiteSpace(modelOverride) ? modelOverride : tenant.DefaultModelId;

        if (tenant.AllowedModels.Count > 0 &&
            !string.Equals(target, tenant.DecisionModelId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(target, tenant.DefaultModelId, StringComparison.OrdinalIgnoreCase) &&
            !tenant.AllowedModels.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Model '{target}' is not enabled in the allowed model list for tenant '{tenant.TenantId}'.");
        }

        return target;
    }

    public IChatClient ResolveChatClient(TenantHarness tenant, string? modelOverride = null) =>
        ResolveClient(tenant, modelOverride, requireTools: false);

    /// <inheritdoc />
    public IChatClient ResolveRequiredChatClient(TenantHarness tenant, string? modelOverride = null) =>
        ResolveClient(tenant, modelOverride, requireTools: true);

    private IChatClient ResolveClient(TenantHarness tenant, string? modelOverride, bool requireTools)
    {
        var modelId = ResolveModelId(tenant, modelOverride);
        if (_customClients.TryGetValue(modelId, out var injectedClient))
        {
            if (injectedClient is SimulatedSliceChatClient && (requireTools || !AllowsDevelopmentSimulation))
            {
                throw new InvalidOperationException("This operation requires a configured, non-simulated model.");
            }

            return new ReasoningCleanupChatClient(injectedClient, ownsInnerClient: false);
        }

        var descriptor = _descriptors.FirstOrDefault(d =>
            String.Equals(d.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        if (!requireTools && AllowsDevelopmentSimulation && (descriptor is null ||
            descriptor.Provider == ModelProvider.Simulated || String.IsNullOrWhiteSpace(descriptor.Endpoint)))
        {
            return new SimulatedSliceChatClient(modelId);
        }
        if (descriptor is null || descriptor.Provider == ModelProvider.Simulated ||
            !Uri.TryCreate(descriptor.Endpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps &&
             (endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback)))
        {
            throw new InvalidOperationException(
                "Model execution requires a configured endpoint using HTTPS or loopback HTTP.");
        }

        IChatClient client = descriptor.Provider switch
        {
            ModelProvider.FoundryInference when requireTools => throw new NotSupportedException(
                "The legacy inference adapter does not support Ask AI tool calling. " +
                "Select a configured OpenAI-compatible tool-calling deployment."),
            ModelProvider.FoundryInference => CreateFoundryInferenceClient(endpoint.ToString(), descriptor.DeploymentName),
            ModelProvider.AzureOpenAI => new AzureOpenAIClient(endpoint, _credential)
                .GetChatClient(descriptor.DeploymentName).AsIChatClient(),
            ModelProvider.OpenAICompatible => new OpenAIClient(
                    new System.ClientModel.ApiKeyCredential("local"),
                    new OpenAIClientOptions { Endpoint = endpoint })
                .GetChatClient(descriptor.DeploymentName).AsIChatClient(),
            _ => throw new InvalidOperationException("The selected model provider is not supported by Ask AI.")
        };

        return new ReasoningCleanupChatClient(client);
    }

    private FoundryInferenceChatClient CreateFoundryInferenceClient(string endpoint, string deploymentName)
    {
        var options = new AzureAIInferenceClientOptions();
        options.AddPolicy(
            new BearerTokenAuthenticationPolicy(_credential, ["https://cognitiveservices.azure.com/.default"]),
            HttpPipelinePosition.PerRetry);

        return new FoundryInferenceChatClient(new ChatCompletionsClient(new Uri(endpoint), _credential, options), deploymentName);
    }

    public static IReadOnlyList<ModelDescriptor> CreateDefaultDescriptors(
        string? foundryInferenceEndpoint = null,
        string? azureOpenAiEndpoint = null,
        string? localOpenAiEndpoint = null) =>
    [
        new("decision-1", "Microsoft Decision 1", "Microsoft", ModelProvider.AzureOpenAI, "gpt-5-mini", azureOpenAiEndpoint ?? foundryInferenceEndpoint, true),
        new("phi-4", "Microsoft Phi-4 (Foundry)", "Microsoft", ModelProvider.FoundryInference, "Phi-4", foundryInferenceEndpoint),
        new("mai-reasoning", "Microsoft MAI-Reasoning (Foundry)", "Microsoft", ModelProvider.FoundryInference, "MAI-Reasoning", foundryInferenceEndpoint),
        new("llama-3.3-70b", "Meta Llama 3.3 70B Instruct (Foundry)", "Meta", ModelProvider.FoundryInference, "Llama-3.3-70B-Instruct", foundryInferenceEndpoint),
        new("deepseek-r1", "DeepSeek R1 Reasoning (Foundry)", "DeepSeek", ModelProvider.FoundryInference, "DeepSeek-R1", foundryInferenceEndpoint),
        new("mistral-large", "Mistral Large 2411 (Foundry)", "Mistral", ModelProvider.FoundryInference, "Mistral-Large-2411", foundryInferenceEndpoint),
        new("gpt-5-mini", "GPT-5 Mini (Foundry / Azure OpenAI)", "OpenAI", ModelProvider.AzureOpenAI, "gpt-5-mini", azureOpenAiEndpoint ?? foundryInferenceEndpoint, true),
        new("gpt-4o", "GPT-4o (Foundry / Azure OpenAI)", "OpenAI", ModelProvider.AzureOpenAI, "gpt-4o", azureOpenAiEndpoint ?? foundryInferenceEndpoint, true),
        new("foundry-local-phi4", "Phi-4 (On-Device Foundry Local / Ollama)", "Local", ModelProvider.OpenAICompatible, "phi-4", localOpenAiEndpoint)
    ];
}

/// <summary>
/// Adapts <see cref="Azure.AI.Inference.ChatCompletionsClient"/> (used by non-OpenAI models in Microsoft Foundry
/// such as Meta Llama, DeepSeek, Mistral, Phi-4, and Cohere) to <see cref="Microsoft.Extensions.AI.IChatClient"/>.
/// </summary>
public sealed class FoundryInferenceChatClient : IChatClient
{
    private readonly ChatCompletionsClient _client;
    private readonly string _modelName;

    public FoundryInferenceChatClient(ChatCompletionsClient client, string modelName)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        _modelName = modelName;
        Metadata = new ChatClientMetadata("Azure.AI.Inference", null, modelName);
    }

    public ChatClientMetadata Metadata { get; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var requestOptions = new ChatCompletionsOptions
        {
            Model = _modelName,
            Temperature = options?.Temperature ?? 0.1f
        };

        var messageList = messages.ToList();
        if (!string.IsNullOrWhiteSpace(options?.Instructions) &&
            !messageList.Any(m => m.Role == ChatRole.System))
        {
            requestOptions.Messages.Add(new ChatRequestSystemMessage(options.Instructions));
        }

        foreach (var msg in messageList)
        {
            var text = msg.Text ?? string.Empty;
            if (msg.Role == ChatRole.System)
            {
                requestOptions.Messages.Add(new ChatRequestSystemMessage(text));
            }
            else if (msg.Role == ChatRole.Assistant)
            {
                requestOptions.Messages.Add(new ChatRequestAssistantMessage(text));
            }
            else
            {
                requestOptions.Messages.Add(new ChatRequestUserMessage(text));
            }
        }

        var response = await _client.CompleteAsync(requestOptions, cancellationToken).ConfigureAwait(false);
        var content = response.Value.Content ?? string.Empty;

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, content))
        {
            ModelId = _modelName
        };
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Use GetResponseAsync for structured document analysis.");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}

/// <summary>
/// Normalizes responses from reasoning models (e.g. DeepSeek-R1, Phi-4-reasoning) by stripping
/// <c>&lt;think&gt;...&lt;/think&gt;</c> chains and markdown code fences around structured JSON outputs.
/// </summary>
public sealed class ReasoningCleanupChatClient : DelegatingChatClient
{
    private static readonly Regex ThinkBlockRegex = new(
        @"<think>[\s\S]*?</think>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MarkdownJsonFenceRegex = new(
        @"^```(?:json)?\s*([\s\S]*?)\s*```$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly bool _ownsInnerClient;

    public ReasoningCleanupChatClient(
        IChatClient innerClient,
        bool ownsInnerClient = true)
        : base(innerClient)
    {
        _ownsInnerClient = ownsInnerClient;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents.OfType<TextContent>())
            {
                content.Text = CleanModelText(content.Text);
            }
        }

        return response;
    }

    protected override void Dispose(bool disposing)
    {
        if (_ownsInnerClient)
        {
            base.Dispose(disposing);
        }
    }

    public static string CleanModelText(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var withoutThink = ThinkBlockRegex.Replace(input, string.Empty).Trim();
        var fenceMatch = MarkdownJsonFenceRegex.Match(withoutThink);
        return fenceMatch.Success ? fenceMatch.Groups[1].Value.Trim() : withoutThink;
    }
}

/// <summary>
/// Deterministic offline <see cref="IChatClient"/> that simulates structured document slice detection
/// for local runs and automated tests without requiring live cloud endpoints.
/// </summary>
public sealed class SimulatedSliceChatClient : IChatClient
{
    private static readonly Regex PageMarkerRegex = new(
        @"<!--\s*Page\s+(\d+)\s*-->\s*([\s\S]*?)(?=<!--\s*PageBreak\s*-->|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FooterCommentRegex = new(
        @"<!--\s*PageFooter=""([^""]+)""\s*-->",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CatalogLineRegex = new(
        @"^-\s+([^:\r\n]+)(?::\s*""([^""]+)"")?",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly string _modelId;

    public SimulatedSliceChatClient(string modelId)
    {
        _modelId = modelId;
        Metadata = new ChatClientMetadata("DocIntel.Simulated", null, modelId);
    }

    public ChatClientMetadata Metadata { get; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var systemPrompt = options?.Instructions
            ?? list.FirstOrDefault(m => m.Role == ChatRole.System)?.Text
            ?? string.Empty;
        var userText = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;

        if (systemPrompt.Contains("[TASK:IDENTIFY_DOCUMENT]", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(SimulateDocumentIdentification(systemPrompt, userText));
        }

        if (systemPrompt.Contains("[TASK:DOCUMENT_INSIGHT]", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(SimulateDocumentInsight(systemPrompt, userText));
        }

        if (systemPrompt.Contains("[TASK:ENTITY_EXTRACTION]", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(SimulateEntityExtraction(systemPrompt, userText));
        }

        if (systemPrompt.Contains("[TASK:DATA_EXTRACTION]", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(SimulateDataExtraction(systemPrompt, userText));
        }

        if (systemPrompt.Contains("[TASK:PROMPT_COLUMN]", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(SimulatePromptColumn(systemPrompt, userText));
        }

        var catalog = new List<(string Name, string Hint)>();
        foreach (Match m in CatalogLineRegex.Matches(systemPrompt))
        {
            catalog.Add((m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()));
        }

        var pages = new List<(int PageNumber, string Content, string Footer)>();
        foreach (Match pm in PageMarkerRegex.Matches(userText))
        {
            var pageNum = int.Parse(pm.Groups[1].Value);
            var content = pm.Groups[2].Value.Trim();
            var footerMatch = FooterCommentRegex.Match(content);
            var footer = footerMatch.Success ? footerMatch.Groups[1].Value.Trim() : $"Page-{pageNum}";
            pages.Add((pageNum, content, footer));
        }

        var groups = new List<(int Start, int End, string Footer, string CombinedContent)>();
        foreach (var page in pages.OrderBy(p => p.PageNumber))
        {
            if (groups.Count > 0 && string.Equals(groups[^1].Footer, page.Footer, StringComparison.OrdinalIgnoreCase))
            {
                var prev = groups[^1];
                groups[^1] = (prev.Start, page.PageNumber, prev.Footer, prev.CombinedContent + "\n" + page.Content);
            }
            else
            {
                groups.Add((page.PageNumber, page.PageNumber, page.Footer, page.Content));
            }
        }

        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "form", "footer", "contains", "title", "states", "look", "body", "references", "agreement", "amendment"
        };

        var segments = groups.Select(g =>
        {
            var topLines = g.CombinedContent
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !l.StartsWith("<!--", StringComparison.Ordinal))
                .Take(4)
                .ToList();

            string[] docKeywords =
            [
                "Price Sheet", "Signature Form", "Formulario de Firma", "Agreement",
                "Enrollment", "Amendment", "Addendum", "Statement of Work", "Invoice"
            ];

            var bestTitleLine = topLines.FirstOrDefault(l =>
                    docKeywords.Any(k => l.Contains(k, StringComparison.OrdinalIgnoreCase)))
                ?? topLines.FirstOrDefault()
                ?? string.Empty;

            var titleLine = bestTitleLine.Contains(" - ", StringComparison.Ordinal)
                ? string.Join(" - ", bestTitleLine.Split(" - ", StringSplitOptions.TrimEntries).Skip(1))
                : bestTitleLine.Split('-')[0].Trim();

            string? matchedName = null;
            foreach (var (name, hint) in catalog)
            {
                var tokens = (name + " " + hint)
                    .Split([' ', ',', '"', '\'', ':'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t.Length >= 4 && !stopWords.Contains(t))
                    .ToList();

                if (titleLine.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                    tokens.Any(t => g.Footer.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                    titleLine.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    matchedName = name;
                    break;
                }
            }

            var resolvedName = matchedName ?? (!string.IsNullOrWhiteSpace(titleLine) ? titleLine : g.Footer);

            return new
            {
                footers = new[] { g.Footer },
                startPage = g.Start,
                endPage = g.End,
                documentTypeName = resolvedName,
                isInferred = matchedName is null
            };
        }).ToArray();

        var json = JsonSerializer.Serialize(new { segments });
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))
        {
            ModelId = _modelId
        });
    }

    private ChatResponse SimulateDocumentIdentification(string systemPrompt, string userText)
    {
        var ruleLines = systemPrompt
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.TrimStart().StartsWith("RULE|", StringComparison.Ordinal))
            .ToList();

        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "if", "document", "footer", "contains", "title", "or", "and", "the", "has", "states", "with", "when", "this", "that", "body", "references", "form", "customer", "master", "agreement"
        };

        string matchedDocName = "Undefined";
        string? matchedRulePrompt = null;
        double confidence = 0.50;

        // Pass 1: Prefer exact DocumentName title match in the document text
        foreach (var line in ruleLines)
        {
            var parts = line.Split('|', 3);
            if (parts.Length < 3) continue;
            var docName = parts[1].Trim();
            var prompt = parts[2].Trim();

            if (userText.Contains(docName, StringComparison.OrdinalIgnoreCase))
            {
                matchedDocName = docName;
                matchedRulePrompt = prompt;
                confidence = 0.98;
                break;
            }
        }

        // Pass 2: Check identification rule tokens (footer codes, bilingual titles, etc.)
        if (matchedDocName == "Undefined")
        {
            foreach (var line in ruleLines)
            {
                var parts = line.Split('|', 3);
                if (parts.Length < 3) continue;

                var docName = parts[1].Trim();
                var prompt = parts[2].Trim();

                var tokens = prompt
                    .Split([',', ';', '.', '"', '\'', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
                    .SelectMany(chunk => chunk.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Where(w => w.Length >= 3 && !stopWords.Contains(w))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (tokens.Any(t => userText.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    matchedDocName = docName;
                    matchedRulePrompt = prompt;
                    confidence = 0.96;
                    break;
                }
            }
        }

        var json = JsonSerializer.Serialize(new
        {
            identifiedDocumentName = matchedDocName,
            confidence,
            matchedRule = matchedRulePrompt
        });

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, json))
        {
            ModelId = _modelId
        };
    }

    private ChatResponse SimulateDocumentInsight(string systemPrompt, string userText)
    {
        var lines = userText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 3 && !l.StartsWith("<!--", StringComparison.Ordinal))
            .ToList();

        var instructionLine = systemPrompt
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(l => !l.StartsWith("[TASK:", StringComparison.Ordinal) &&
                                !l.StartsWith("You are an enterprise", StringComparison.OrdinalIgnoreCase))
            ?.Trim() ?? string.Empty;

        var docHeading = lines.FirstOrDefault() ?? "Commercial Document";
        var customer = ResolveFieldFromText("customer", "customer entity", userText);
        var mbsa = ResolveFieldFromText("mbsa", "mba/mbsa", userText);
        var contract = ResolveFieldFromText("contract", "contract number", userText);
        var quote = ResolveFieldFromText("quote", "quote number", userText);
        var currency = ResolveFieldFromText("currency", "billing currency", userText);
        var dealVal = ResolveFieldFromText("total", "total deal value", userText);

        var insightParts = new List<string>();
        var lowerInstr = instructionLine.ToLowerInvariant();

        // 1. Core document & counterparty identity
        if (!string.IsNullOrWhiteSpace(customer))
        {
            insightParts.Add($"{docHeading} executed for {customer}.");
        }
        else
        {
            insightParts.Add($"{docHeading} governing enterprise commercial obligations.");
        }

        // 2. Agreement / Quote / Contract identifiers
        var idTokens = new List<string>();
        if (!string.IsNullOrWhiteSpace(mbsa)) idTokens.Add($"MBA/MBSA {mbsa}");
        if (!string.IsNullOrWhiteSpace(contract)) idTokens.Add($"Contract {contract}");
        if (!string.IsNullOrWhiteSpace(quote)) idTokens.Add($"Quote {quote}");
        if (idTokens.Count > 0)
        {
            insightParts.Add($"Key Identifiers: {string.Join(", ", idTokens)}.");
        }

        // 3. Financial & commercial terms
        if (!string.IsNullOrWhiteSpace(dealVal))
        {
            var currLabel = !string.IsNullOrWhiteSpace(currency) ? $" {currency}" : " USD";
            insightParts.Add($"Total Financial Value: {dealVal}{currLabel}.");
        }
        else if (!string.IsNullOrWhiteSpace(currency))
        {
            insightParts.Add($"Billing Currency: {currency}.");
        }

        // 4. Substantive commercial terms from body lines (excluding raw IDs already summarized)
        var bodyClauseLines = lines.Skip(1)
            .Where(l => !l.StartsWith("Customer Name:", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("Nombre de la entidad:", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("MBA/MBSA", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("Número de MBA", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("Contract Number:", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("Número de contrato:", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("Quote Number:", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("Billing currency:", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("Total deal value", StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        if (bodyClauseLines.Count > 0)
        {
            insightParts.Add($"Commercial Terms: {string.Join(" ", bodyClauseLines)}");
        }

        // 5. Honor any custom focus instruction configured by Admin in Admin Studio
        var defaultInstrPrefix = "Summarize the document concisely, highlighting the customer entity";
        if (!string.IsNullOrWhiteSpace(instructionLine) &&
            !instructionLine.StartsWith(defaultInstrPrefix, StringComparison.OrdinalIgnoreCase))
        {
            insightParts.Add($"Admin Focus ({instructionLine.TrimEnd('.')}): Verified against slice clauses ({string.Join("; ", lines.Take(2))}).");
        }

        var summary = string.Join(" ", insightParts);
        var json = JsonSerializer.Serialize(new { summary });
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, json))
        {
            ModelId = _modelId
        };
    }

    private ChatResponse SimulateEntityExtraction(string systemPrompt, string userText)
    {
        var entities = new List<object>();

        void TryMatch(string pattern, string name, string category)
        {
            var m = Regex.Match(userText, pattern, RegexOptions.IgnoreCase);
            if (m.Success && m.Groups.Count > 1)
            {
                var val = m.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    entities.Add(new { name, value = val, category, confidence = 0.97 });
                }
            }
        }

        TryMatch(@"(?:MBA/MBSA(?:\s+Number)?|MBSA)(?::\s*|\s+)(?=[A-Z0-9\-]*\d)([A-Z0-9\-]{5,})", "MBA/MBSA Number", "Identifier");
        TryMatch(@"(?:N[uú]mero\s+de\s+contrato|Contract\s+Number|Agreement\s+Number|contrato|Contract|Agreement)(?::\s*|\s+)(?=[A-Z0-9\-]*\d)([A-Z0-9\-]{5,})", "Contract Number", "Identifier");
        TryMatch(@"Quote\s+Number:\s*([0-9\.]+)", "Quote Number", "Identifier");
        TryMatch(@"Billing\s+currency:\s*([A-Z]{3})", "Billing Currency", "Financial");
        TryMatch(@"Total\s+deal\s+value(?::\s*|\s+)([0-9,\.]+)", "Total Deal Value", "Financial");
        TryMatch(@"Tenant\s+ID:\s*([^\r\n\{]+)", "Tenant ID", "Customer");
        TryMatch(@"(?:Entity\s+Name|Nombre\s+de\s+la\s+entidad|Customer\s+Name):\s*([^\r\n]+)", "Customer Name", "Party");

        if (!entities.Any(e => ((dynamic)e).name == "Customer Name") &&
            userText.Contains("Cadena Comercial OXXO", StringComparison.OrdinalIgnoreCase))
        {
            entities.Add(new { name = "Customer Name", value = "Cadena Comercial OXXO SA de CV", category = "Party", confidence = 0.99 });
        }

        var firstLine = userText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(firstLine))
        {
            entities.Add(new { name = "Document Heading", value = firstLine, category = "Metadata", confidence = 0.92 });
        }

        var json = JsonSerializer.Serialize(new { entities });
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, json))
        {
            ModelId = _modelId
        };
    }

    private ChatResponse SimulateDataExtraction(string systemPrompt, string userText)
    {
        var lines = systemPrompt.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var fields = new List<object>();

        foreach (var line in lines.Where(l => l.StartsWith("FIELD|", StringComparison.Ordinal)))
        {
            var parts = line.Split('|', 3);
            if (parts.Length < 3) continue;

            var fieldName = parts[1].Trim();
            var instruction = parts[2].Trim();
            var val = ResolveFieldFromText(fieldName, instruction, userText);
            fields.Add(new { fieldName, fieldValue = val, confidenceValue = string.IsNullOrEmpty(val) ? 50.0 : 98.5 });
        }

        var tables = new List<object>();
        var tableLines = lines.Where(l => l.StartsWith("TABLE|", StringComparison.Ordinal)).ToList();
        var colLines = lines.Where(l => l.StartsWith("COLUMN|", StringComparison.Ordinal)).ToList();

        foreach (var tLine in tableLines)
        {
            var tParts = tLine.Split('|', 3);
            if (tParts.Length < 3) continue;
            var tableName = tParts[1].Trim();

            var cols = colLines
                .Select(c => c.Split('|', 4))
                .Where(c => c.Length >= 4 && string.Equals(c[1].Trim(), tableName, StringComparison.OrdinalIgnoreCase))
                .Select(c => (ColName: c[2].Trim(), ColInstruction: c[3].Trim()))
                .ToList();

            var rows = ExtractTableRowsFromText(tableName, cols, userText);
            tables.Add(new { tableName, rows });
        }

        var json = JsonSerializer.Serialize(new { fields, tables });
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, json))
        {
            ModelId = _modelId
        };
    }

    private ChatResponse SimulatePromptColumn(string systemPrompt, string userText)
    {
        var lines = systemPrompt.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var colName = lines.FirstOrDefault(l => l.StartsWith("COLUMN_NAME|", StringComparison.Ordinal))?.Split('|', 2).ElementAtOrDefault(1) ?? "Custom Prompt";
        var promptTemplate = lines.FirstOrDefault(l => l.StartsWith("PROMPT_TEMPLATE|", StringComparison.Ordinal))?.Split('|', 2).ElementAtOrDefault(1) ?? string.Empty;

        var rawDocContent = userText;
        var docMarkerIdx = userText.IndexOf("[DOCUMENT_CONTENT_START]", StringComparison.Ordinal);
        if (docMarkerIdx >= 0)
        {
            rawDocContent = userText[(docMarkerIdx + "[DOCUMENT_CONTENT_START]".Length)..];
        }

        var bodyLines = rawDocContent
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith("[PDF_FILE_ATTACHMENT:", StringComparison.Ordinal) &&
                        !l.StartsWith("[DOCUMENT_METADATA:", StringComparison.Ordinal) &&
                        !l.StartsWith("Document Content:", StringComparison.Ordinal))
            .Select(l => l.Trim())
            .Where(l => l.Length > 2)
            .ToList();

        var preview = string.Join(" — ", bodyLines.Take(3));
        var lower = (colName + " " + promptTemplate).ToLowerInvariant();

        string result;
        if (lower.Contains("language") || lower.Contains("classify"))
        {
            result = rawDocContent.Contains("Formulario", StringComparison.OrdinalIgnoreCase)
                ? "Spanish (ES) source document translated to English (EN)."
                : "English (EN) commercial agreement document.";
        }
        else if (lower.Contains("highlight") || lower.Contains("pricing") || lower.Contains("amendment"))
        {
            result = bodyLines.Count > 0
                ? string.Join("\n", bodyLines.Take(4).Select(l => $"• {l}"))
                : preview;
        }
        else if (lower.Contains("contract") || lower.Contains("verify") || lower.Contains("signature"))
        {
            result = $"Confirmed contract identifiers and formalization clauses: {preview}.";
        }
        else
        {
            result = $"{preview}.";
        }

        var json = JsonSerializer.Serialize(new { result });
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, json))
        {
            ModelId = _modelId
        };
    }

    private static string ResolveFieldFromText(string fieldName, string instruction, string text)
    {
        var key = (fieldName + " " + instruction).ToLowerInvariant();

        if (key.Contains("mba") || key.Contains("mbsa"))
        {
            var m = Regex.Match(text, @"(?:MBA/MBSA(?:\s+Number)?|MBSA)(?::\s*|\s+)(?=[A-Z0-9\-]*\d)([A-Z0-9\-]{5,})", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
        }

        if (key.Contains("contract") || key.Contains("contrato") || key.Contains("agreement"))
        {
            var m = Regex.Match(text, @"(?:N[uú]mero\s+de\s+contrato|Contract\s+Number|Agreement\s+Number|contrato|Contract|Agreement)(?::\s*|\s+)(?=[A-Z0-9\-]*\d)([A-Z0-9\-]{5,})", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
        }

        if (key.Contains("quote"))
        {
            var m = Regex.Match(text, @"Quote\s+Number:\s*([0-9\.]+)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
        }

        if (key.Contains("currency"))
        {
            var m = Regex.Match(text, @"Billing\s+currency:\s*([A-Z]{3})", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
        }

        if (key.Contains("total") || key.Contains("deal") || key.Contains("value"))
        {
            var m = Regex.Match(text, @"Total\s+deal\s+value\s+([0-9,\.]+)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
        }

        if (key.Contains("customer") || key.Contains("entity") || key.Contains("cliente") || key.Contains("entidad"))
        {
            var m = Regex.Match(text, @"(?:Entity\s+Name|Nombre\s+de\s+la\s+entidad|Customer\s+Name):\s*([^\r\n]+)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            if (text.Contains("Cadena Comercial OXXO", StringComparison.OrdinalIgnoreCase))
            {
                return "Cadena Comercial OXXO SA de CV";
            }
        }

        return string.Empty;
    }

    private static List<object> ExtractTableRowsFromText(
        string tableName,
        List<(string ColName, string ColInstruction)> cols,
        string text)
    {
        var rows = new List<object>();
        if (cols.Count == 0) return rows;

        if (tableName.Contains("PurchaseOrder", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("FOOD SERVICE - 6137346", StringComparison.OrdinalIgnoreCase))
        {
            var rowFields = cols.Select(c =>
            {
                var cn = c.ColName.ToLowerInvariant();
                var val = cn.Contains("section") ? "Section 1"
                    : cn.Contains("tenant") ? "FOOD SERVICE - 6137346"
                    : cn.Contains("amount") || cn.Contains("total") ? "282,600.00"
                    : string.Empty;
                return (object)new { fieldName = c.ColName, fieldValue = val, confidenceValue = 97.0 };
            }).ToList();

            rows.Add(new { fields = rowFields });
        }
        else if (tableName.Contains("Subscription", StringComparison.OrdinalIgnoreCase) ||
                 tableName.Contains("Pricing", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match m in Regex.Matches(text, @"([A-Z0-9]{3}-[0-9]{5})\s+([0-9]+\.[0-9]{2})", RegexOptions.IgnoreCase))
            {
                var sku = m.Groups[1].Value;
                var price = m.Groups[2].Value;
                var rowFields = cols.Select(c =>
                {
                    var cn = c.ColName.ToLowerInvariant();
                    var val = cn.Contains("part") || cn.Contains("sku") ? sku
                        : cn.Contains("product") || cn.Contains("name") ? "Entra Suite Sub Per User"
                        : cn.Contains("price") || cn.Contains("amount") ? price
                        : string.Empty;
                    return (object)new { fieldName = c.ColName, fieldValue = val, confidenceValue = 96.5 };
                }).ToList();

                rows.Add(new { fields = rowFields });
            }
        }

        return rows;
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
