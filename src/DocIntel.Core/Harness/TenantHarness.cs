using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocIntel.Core.Harness;

/// <summary>
/// Strategy used by the Document Splitting feature to detect boundaries and group pages.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SplittingStrategy
{
    /// <summary>
    /// Combines deterministic footer/amendment boundary grouping with AI semantic classification.
    /// </summary>
    Hybrid,

    /// <summary>
    /// Uses only deterministic footer similarity clustering and amendment indicators (zero model calls).
    /// </summary>
    HeuristicFooter,

    /// <summary>
    /// Uses the configured AI model with structured outputs to detect page boundaries and document types.
    /// </summary>
    LlmSemantic
}

/// <summary>
/// Represents an expected document or form type in a tenant's catalog.
/// </summary>
public sealed class DocumentCatalogEntry
{
    public required string Name { get; set; }

    public string? IdentificationHint { get; set; }

    public string? SchemaId { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Tenant-configurable settings for the File Splitting & Grouping feature.
/// </summary>
public sealed class SplittingConfig
{
    public SplittingStrategy Strategy { get; set; } = SplittingStrategy.Hybrid;

    public bool EnableFooterNormalization { get; set; } = true;

    public double FooterNormalizationThreshold { get; set; } = 0.85;

    public double FooterBoundarySimilarityThreshold { get; set; } = 0.90;

    public bool InferNameWhenNotInCatalog { get; set; } = true;

    public bool ProduceSplitPdfBytes { get; set; } = true;

    public List<string> FootersToIgnore { get; set; } = ["Confidential", "Microsoft Confidential", "Page"];

    public List<string> AmendmentIndicators { get; set; } = ["amendment", "avenant", "enmienda", "nachtrag", "ctm"];

    public List<DocumentCatalogEntry> ExpectedDocumentTypes { get; set; } = [];
}

/// <summary>
/// Admin-defined rule mapping a canonical Document Name to a natural-language prompt/rule
/// describing how to identify that document (e.g. footer patterns, title keywords).
/// </summary>
public sealed class DocumentIdentificationRule
{
    public required string DocumentName { get; set; }

    public required string IdentificationPrompt { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Configuration for the 'Identify Documents' feature using a Microsoft Decision Model.
/// </summary>
public sealed class DocumentIdentificationConfig
{
    /// <summary>
    /// Microsoft Decision Model used to classify slices into defined document names or "Undefined".
    /// </summary>
    public string DecisionModelId { get; set; } = "decision-1";

    public List<DocumentIdentificationRule> Rules { get; set; } = [];
}

/// <summary>
/// Configuration for the 'Document Translation' feature using Azure Document Translation.
/// </summary>
public sealed class DocumentTranslationConfig
{
    public string TargetLanguage { get; set; } = "en";

    public string? AzureTranslatorEndpoint { get; set; }

    public List<string> DoNotTranslateTerms { get; set; } = ["Microsoft", "MBSA", "OXXO"];
}

/// <summary>
/// Configuration for the 'Document Insight (Summary)' feature with admin-customizable summary instructions.
/// </summary>
public sealed class DocumentInsightConfig
{
    public string SummaryInstructions { get; set; } =
        "Summarize the document concisely, highlighting the customer entity, agreement/quote identifiers, key commercial terms, and total financial value.";
}

/// <summary>
/// Configuration for the 'Entity Extraction' feature with admin-customizable entity extraction prompt.
/// </summary>
public sealed class EntityExtractionConfig
{
    public string ExtractionPrompt { get; set; } =
        "Extract Customer Name, MBA/MBSA Number, Contract/Agreement Number, Quote Number, Billing Currency, Tenant ID, and Total Deal Value.";
}

/// <summary>
/// Defines a single key-value field and its extraction instruction for a specific defined document type.
/// </summary>
public sealed class DataExtractionFieldSchema
{
    public required string FieldName { get; set; }

    public required string Instruction { get; set; }
}

/// <summary>
/// Defines a column and its extraction instruction within a table schema.
/// </summary>
public sealed class DataExtractionColumnSchema
{
    public required string ColumnName { get; set; }

    public required string Instruction { get; set; }
}

/// <summary>
/// Defines a table (with columns and table-level instruction) to extract from a specific defined document type.
/// </summary>
public sealed class DataExtractionTableSchema
{
    public required string TableName { get; set; }

    public required string Instruction { get; set; }

    public List<DataExtractionColumnSchema> Columns { get; set; } = [];
}

/// <summary>
/// Per-document-type Data Extraction schema containing key-value fields and tables.
/// </summary>
public sealed class DocumentDataExtractionProfile
{
    public required string DocumentName { get; set; }

    public List<DataExtractionFieldSchema> Fields { get; set; } = [];

    public List<DataExtractionTableSchema> Tables { get; set; } = [];
}

/// <summary>
/// Configuration for the 'Data Extraction' feature (per defined document key-value fields and tables).
/// </summary>
public sealed class DataExtractionConfig
{
    public List<DocumentDataExtractionProfile> Profiles { get; set; } = [];
}

/// <summary>
/// Defines a single entity filter condition that determines whether a Prompt Column should run on a document/slice.
/// </summary>
public sealed class PromptEntityFilterRule
{
    public required string EntityName { get; set; }

    /// <summary>
    /// Comparison operator: Exists, Equals, Contains, StartsWith, NotEquals.
    /// </summary>
    public string Operator { get; set; } = "Exists";

    public string? ExpectedValue { get; set; }
}

/// <summary>
/// Admin-defined custom Prompt Column that processes matching documents/slices as PDF file/base64.
/// </summary>
public sealed class PromptColumnDefinition
{
    public string ColumnId { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public required string ColumnName { get; set; }

    public required string PromptInstructions { get; set; }

    /// <summary>
    /// Filter evaluation mode: "All" (run on all documents/slices), "And" (all rules must match), or "Or" (any rule matches).
    /// </summary>
    public string FilterMode { get; set; } = "All";

    public List<PromptEntityFilterRule> EntityFilters { get; set; } = [];

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Configuration for the 'Prompt Columns' capability in the Agent Harness.
/// </summary>
public sealed class PromptColumnsConfig
{
    public List<PromptColumnDefinition> Columns { get; set; } = [];
}

/// <summary>
/// Per-feature harness configuration within a tenant profile.
/// </summary>
public sealed class FeatureHarness
{
    public const string SplitAndGroupKey = "SplitAndGroup";
    public const string IdentifyDocumentsKey = "IdentifyDocuments";
    public const string DocumentTranslationKey = "DocumentTranslation";
    public const string DocumentInsightKey = "DocumentInsight";
    public const string EntityExtractionKey = "EntityExtraction";
    public const string DataExtractionKey = "DataExtraction";
    public const string PromptColumnsKey = "PromptColumns";

    public bool Enabled { get; set; } = true;

    public string? ModelIdOverride { get; set; }

    public string? CustomSystemPrompt { get; set; }

    public SplittingConfig? SplitAndGroup { get; set; }

    public DocumentIdentificationConfig? IdentifyDocuments { get; set; }

    public DocumentTranslationConfig? DocumentTranslation { get; set; }

    public DocumentInsightConfig? DocumentInsight { get; set; }

    public EntityExtractionConfig? EntityExtraction { get; set; }

    public DataExtractionConfig? DataExtraction { get; set; }

    public PromptColumnsConfig? PromptColumns { get; set; }
}

/// <summary>
/// Persisted Agent Harness profile for an individual tenant.
/// </summary>
public sealed class TenantHarness
{
    /// <summary>
    /// Cosmos DB document id (mirrors <see cref="TenantId"/>).
    /// </summary>
    [JsonPropertyName("id")]
    public string Id
    {
        get => TenantId;
        set => TenantId = value;
    }

    [JsonPropertyName("tenantId")]
    public required string TenantId { get; set; }

    public required string DisplayName { get; set; }

    public int HarnessVersion { get; set; } = 1;

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Harness-level Decision Model (e.g., "decision-1") used for classification and routing tasks.
    /// </summary>
    public string DecisionModelId { get; set; } = "decision-1";

    /// <summary>
    /// Harness-level Language Model (e.g., "gpt-5-mini", "phi-4", "llama-3.3-70b", "deepseek-r1") used for insights and extraction tasks.
    /// </summary>
    public string DefaultModelId { get; set; } = "gpt-5-mini";

    public List<string> AllowedModels { get; set; } =
        ["decision-1", "phi-4", "mai-reasoning", "llama-3.3-70b", "deepseek-r1", "mistral-large", "gpt-5-mini", "gpt-4o"];

    public Dictionary<string, FeatureHarness> Capabilities { get; set; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [FeatureHarness.SplitAndGroupKey] = new FeatureHarness
            {
                Enabled = true,
                SplitAndGroup = new SplittingConfig()
            },
            [FeatureHarness.IdentifyDocumentsKey] = new FeatureHarness
            {
                Enabled = true,
                ModelIdOverride = "decision-1",
                IdentifyDocuments = new DocumentIdentificationConfig
                {
                    DecisionModelId = "decision-1",
                    Rules =
                    [
                        new DocumentIdentificationRule
                        {
                            DocumentName = "Program Signature Form",
                            IdentificationPrompt = "If document footer contains ProgramSignForm or DocumentX20-12883, or document title contains Formulario de Firma del Programa or Program Signature Form"
                        },
                        new DocumentIdentificationRule
                        {
                            DocumentName = "Customer Price Sheet",
                            IdentificationPrompt = "If document footer contains MSQuote or CTM, or document title contains Customer Price Sheet"
                        },
                        new DocumentIdentificationRule
                        {
                            DocumentName = "Master Business Service Agreement",
                            IdentificationPrompt = "If document footer contains Form-MBSA or document title contains Master Business Service Agreement"
                        },
                        new DocumentIdentificationRule
                        {
                            DocumentName = "Enterprise Enrollment",
                            IdentificationPrompt = "If document footer contains Form-EA or document title contains Enterprise Enrollment"
                        },
                        new DocumentIdentificationRule
                        {
                            DocumentName = "Pricing Amendment",
                            IdentificationPrompt = "If document footer or title contains Amendment ID or M130"
                        }
                    ]
                }
            },
            [FeatureHarness.DocumentTranslationKey] = new FeatureHarness
            {
                Enabled = true,
                DocumentTranslation = new DocumentTranslationConfig
                {
                    TargetLanguage = "en"
                }
            },
            [FeatureHarness.DocumentInsightKey] = new FeatureHarness
            {
                Enabled = true,
                ModelIdOverride = "gpt-5-mini",
                DocumentInsight = new DocumentInsightConfig()
            },
            [FeatureHarness.EntityExtractionKey] = new FeatureHarness
            {
                Enabled = true,
                ModelIdOverride = "gpt-5-mini",
                EntityExtraction = new EntityExtractionConfig()
            },
            [FeatureHarness.DataExtractionKey] = new FeatureHarness
            {
                Enabled = true,
                ModelIdOverride = "gpt-5-mini",
                DataExtraction = new DataExtractionConfig
                {
                    Profiles =
                    [
                        new DocumentDataExtractionProfile
                        {
                            DocumentName = "Program Signature Form",
                            Fields =
                            [
                                new DataExtractionFieldSchema
                                {
                                    FieldName = "CustomerEntityName",
                                    Instruction = "Extract the legal entity name under Customer / Nombre de la entidad"
                                },
                                new DataExtractionFieldSchema
                                {
                                    FieldName = "MbaMbsaNumber",
                                    Instruction = "Extract the MBA/MBSA agreement number in the top header"
                                },
                                new DataExtractionFieldSchema
                                {
                                    FieldName = "ContractNumber",
                                    Instruction = "Extract the Contract / Agreement number (Número de contrato)"
                                }
                            ],
                            Tables = []
                        },
                        new DocumentDataExtractionProfile
                        {
                            DocumentName = "Customer Price Sheet",
                            Fields =
                            [
                                new DataExtractionFieldSchema
                                {
                                    FieldName = "CustomerName",
                                    Instruction = "Extract the customer company name from the top of the price sheet"
                                },
                                new DataExtractionFieldSchema
                                {
                                    FieldName = "QuoteNumber",
                                    Instruction = "Extract the Quote Number (e.g. 3037534.004)"
                                },
                                new DataExtractionFieldSchema
                                {
                                    FieldName = "BillingCurrency",
                                    Instruction = "Extract the 3-letter Billing currency code (e.g. USD)"
                                },
                                new DataExtractionFieldSchema
                                {
                                    FieldName = "TotalDealValue",
                                    Instruction = "Extract the Total deal value from the Quote Summary"
                                }
                            ],
                            Tables =
                            [
                                new DataExtractionTableSchema
                                {
                                    TableName = "PurchaseOrderSummary",
                                    Instruction = "Extract rows from Purchase Order Information / Summary Totals table",
                                    Columns =
                                    [
                                        new DataExtractionColumnSchema { ColumnName = "Section", Instruction = "Section identifier (e.g. Section 1)" },
                                        new DataExtractionColumnSchema { ColumnName = "TenantId", Instruction = "Tenant ID name and number" },
                                        new DataExtractionColumnSchema { ColumnName = "Amount", Instruction = "Section total amount in USD" }
                                    ]
                                },
                                new DataExtractionTableSchema
                                {
                                    TableName = "SubscriptionPricing",
                                    Instruction = "Extract product pricing rows from Monthly Subscriptions / Future Pricing tables",
                                    Columns =
                                    [
                                        new DataExtractionColumnSchema { ColumnName = "PartNumber", Instruction = "SKU / Part Number (e.g. EP2-04960)" },
                                        new DataExtractionColumnSchema { ColumnName = "ProductName", Instruction = "Online service or product name" },
                                        new DataExtractionColumnSchema { ColumnName = "UnitPrice", Instruction = "Unit price value" }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            },
            [FeatureHarness.PromptColumnsKey] = new FeatureHarness
            {
                Enabled = true,
                ModelIdOverride = "gpt-5-mini",
                PromptColumns = new PromptColumnsConfig
                {
                    Columns =
                    [
                        new PromptColumnDefinition
                        {
                            ColumnId = "col-summary",
                            ColumnName = "Summary",
                            PromptInstructions = "Summarize the contents of {{Document.Content}} in a clear and concise manner. Focus on key points, main arguments, and important conclusions.",
                            FilterMode = "All",
                            EntityFilters = []
                        },
                        new PromptColumnDefinition
                        {
                            ColumnId = "col-highlights",
                            ColumnName = "Highlights",
                            PromptInstructions = "Extract the most important commercial, financial, and operational highlights from {{Document.Content}} as concise bullet points.",
                            FilterMode = "All",
                            EntityFilters = []
                        },
                        new PromptColumnDefinition
                        {
                            ColumnId = "col-contract-check",
                            ColumnName = "Contract Check",
                            PromptInstructions = "Verify contract identifiers, MBA/MBSA reference numbers, legal customer entity names, and formalization clauses in {{Document.Content}}.",
                            FilterMode = "Or",
                            EntityFilters =
                            [
                                new PromptEntityFilterRule
                                {
                                    EntityName = "Contract Number",
                                    Operator = "Exists"
                                },
                                new PromptEntityFilterRule
                                {
                                    EntityName = "MBA/MBSA Number",
                                    Operator = "Exists"
                                }
                            ]
                        }
                    ]
                }
            }
        };

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public static void EnsureDefaultCapabilities(TenantHarness tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var templateCapabilities = new TenantHarness { TenantId = "template", DisplayName = "Template" }.Capabilities;
        foreach (var (key, defaultCap) in templateCapabilities)
        {
            if (!tenant.Capabilities.TryGetValue(key, out var existingCap) || existingCap is null)
            {
                tenant.Capabilities[key] = defaultCap;
            }
            else if (string.Equals(key, FeatureHarness.PromptColumnsKey, StringComparison.OrdinalIgnoreCase))
            {
                if (existingCap.PromptColumns is null || existingCap.PromptColumns.Columns.Count == 0)
                {
                    existingCap.PromptColumns = defaultCap.PromptColumns;
                }
                else
                {
                    foreach (var col in existingCap.PromptColumns.Columns)
                    {
                        if (col.ColumnId == "col-summary" && col.ColumnName == "Summarize Document")
                        {
                            col.ColumnName = "Summary";
                        }
                        else if (col.ColumnId == "col-pricing-highlights" && col.ColumnName == "Amendment & Pricing Highlights")
                        {
                            col.ColumnId = "col-highlights";
                            col.ColumnName = "Highlights";
                            col.FilterMode = "All";
                            col.EntityFilters = [];
                        }
                        else if (col.ColumnId == "col-contract-check" && col.ColumnName == "Contract & Signature Verification")
                        {
                            col.ColumnName = "Contract Check";
                        }
                    }
                }
            }
        }
    }
}

/// <summary>
/// Persists and retrieves multi-tenant <see cref="TenantHarness"/> configurations.
/// </summary>
public interface IHarnessStore
{
    Task<TenantHarness?> GetTenantAsync(string tenantId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TenantHarness>> ListTenantsAsync(CancellationToken cancellationToken = default);

    Task<TenantHarness> UpsertTenantAsync(TenantHarness harness, CancellationToken cancellationToken = default);
}

/// <summary>
/// File-backed JSON store for tenant Agent Harness profiles.
/// </summary>
public sealed class FileHarnessStore : IHarnessStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, TenantHarness>? _cache;

    public FileHarnessStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public string StorageFilePath => _filePath;

    public async Task<TenantHarness?> GetTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var map = await EnsureLoadedAsync(cancellationToken);
            return map.TryGetValue(tenantId, out var found) ? Clone(found) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TenantHarness>> ListTenantsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var map = await EnsureLoadedAsync(cancellationToken);
            return map.Values
                .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TenantHarness> UpsertTenantAsync(TenantHarness harness, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentException.ThrowIfNullOrWhiteSpace(harness.TenantId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var map = await EnsureLoadedAsync(cancellationToken);
            if (map.TryGetValue(harness.TenantId, out var existing))
            {
                harness.HarnessVersion = existing.HarnessVersion + 1;
            }
            else if (harness.HarnessVersion < 1)
            {
                harness.HarnessVersion = 1;
            }

            harness.UpdatedAtUtc = DateTimeOffset.UtcNow;
            var saved = Clone(harness);
            map[harness.TenantId] = saved;

            await FlushAsync(map, cancellationToken);
            return Clone(saved);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, TenantHarness>> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        if (File.Exists(_filePath))
        {
            await using var stream = File.OpenRead(_filePath);
            var list = await JsonSerializer.DeserializeAsync<List<TenantHarness>>(stream, JsonOptions, cancellationToken) ?? [];
            var templateCapabilities = new TenantHarness { TenantId = "template", DisplayName = "Template" }.Capabilities;

            foreach (var tenant in list)
            {
                if (!tenant.AllowedModels.Contains("decision-1", StringComparer.OrdinalIgnoreCase))
                {
                    tenant.AllowedModels.Insert(0, "decision-1");
                }
                if (!tenant.AllowedModels.Contains("mai-reasoning", StringComparer.OrdinalIgnoreCase))
                {
                    tenant.AllowedModels.Insert(1, "mai-reasoning");
                }
                if (!tenant.AllowedModels.Contains("phi-4", StringComparer.OrdinalIgnoreCase))
                {
                    tenant.AllowedModels.Insert(2, "phi-4");
                }

                TenantHarness.EnsureDefaultCapabilities(tenant);

                if (string.IsNullOrWhiteSpace(tenant.DecisionModelId))
                {
                    tenant.DecisionModelId = "decision-1";
                }
                if (string.IsNullOrWhiteSpace(tenant.DefaultModelId) ||
                    string.Equals(tenant.DefaultModelId, "decision-1", StringComparison.OrdinalIgnoreCase))
                {
                    tenant.DefaultModelId = "phi-4";
                }

                if (tenant.Capabilities.TryGetValue(FeatureHarness.IdentifyDocumentsKey, out var idCap))
                {
                    idCap.ModelIdOverride = tenant.DecisionModelId;
                    if (idCap.IdentifyDocuments is not null)
                    {
                        idCap.IdentifyDocuments.DecisionModelId = tenant.DecisionModelId;
                    }
                }
            }

            _cache = list.ToDictionary(t => t.TenantId, t => t, StringComparer.OrdinalIgnoreCase);
            if (_cache.Count > 0)
            {
                return _cache;
            }
        }

        var defaults = CreateDefaultTenants();
        _cache = defaults.ToDictionary(t => t.TenantId, t => t, StringComparer.OrdinalIgnoreCase);
        await FlushAsync(_cache, cancellationToken);
        return _cache;
    }

    private async Task FlushAsync(Dictionary<string, TenantHarness> map, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var list = map.Values.OrderBy(t => t.TenantId, StringComparer.OrdinalIgnoreCase).ToList();
        var json = JsonSerializer.Serialize(list, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken);
    }

    private static TenantHarness Clone(TenantHarness source)
    {
        var json = JsonSerializer.Serialize(source, JsonOptions);
        return JsonSerializer.Deserialize<TenantHarness>(json, JsonOptions)!;
    }

    public static List<TenantHarness> CreateDefaultTenants() =>
    [
        new TenantHarness
        {
            TenantId = "contoso-legal",
            DisplayName = "Contoso Legal & Licensing",
            HarnessVersion = 1,
            DecisionModelId = "decision-1",
            DefaultModelId = "gpt-5-mini",
            AllowedModels = ["decision-1", "gpt-5-mini", "gpt-4o", "phi-4", "mai-reasoning", "llama-3.3-70b", "deepseek-r1", "mistral-large"]
        },
        new TenantHarness
        {
            TenantId = "fabrikam-procurement",
            DisplayName = "Fabrikam Procurement Ops",
            HarnessVersion = 1,
            DecisionModelId = "decision-1",
            DefaultModelId = "deepseek-r1",
            AllowedModels = ["decision-1", "phi-4", "mai-reasoning", "deepseek-r1", "mistral-large", "gpt-5-mini"]
        }
    ];
}
