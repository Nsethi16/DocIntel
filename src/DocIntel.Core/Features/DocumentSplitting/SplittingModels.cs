using System.Text.Json.Serialization;
using DocIntel.Core.Harness;
using DocIntel.Core.Evidence;
using DocIntel.Core.Features.DocumentTranslation;

namespace DocIntel.Core.Features.DocumentSplitting;

/// <summary>
/// Represents the extracted layout and text of a single 1-based page.
/// </summary>
public sealed class PageLayout
{
    public required int PageNumber { get; init; }

    public double PageWidth { get; init; }

    public double PageHeight { get; init; }

    public double ImageAreaRatio { get; init; }

    public string TextSource { get; set; } = "Unknown";

    public string? TextProvider { get; set; }

    public bool OcrCacheHit { get; set; }

    public IReadOnlyList<PageWord> Words { get; set; } = [];

    public int? SubDocPageNumber { get; set; }

    public int? SubDocTotalPages { get; set; }

    public string BodyText { get; set; } = string.Empty;

    public string Markdown { get; set; } = string.Empty;
    public string? NativeLayoutJson { get; set; }

    public string FooterText { get; set; } = string.Empty;

    public bool IsAmendment { get; set; }

    public string? AmendmentId { get; set; }
}

/// <summary>
/// Represents a single extracted key-value field with confidence score (0-100) for Data Extraction.
/// </summary>
public sealed record ExtractedFieldValue(
    [property: JsonPropertyName("fieldName")] string FieldName,
    [property: JsonPropertyName("fieldValue")] string FieldValue,
    [property: JsonPropertyName("confidenceValue")] double ConfidenceValue = 0,
    [property: JsonPropertyName("provenance")] ValueProvenance? Provenance = null,
    [property: JsonPropertyName("confidenceSignals")] ConfidenceSignals? ConfidenceSignals = null);

/// <summary>
/// Represents a single extracted table row containing column values.
/// </summary>
public sealed record ExtractedTableRow(
    [property: JsonPropertyName("fields")] IReadOnlyList<ExtractedFieldValue> Fields);

/// <summary>
/// Represents a structured table extracted from a document slice.
/// </summary>
public sealed record ExtractedTableValue(
    [property: JsonPropertyName("tableName")] string TableName,
    [property: JsonPropertyName("rows")] IReadOnlyList<ExtractedTableRow> Rows);

/// <summary>
/// Document-level structured Data Extraction output (key-value fields + tables) for a single PDF slice.
/// </summary>
public sealed record DocumentDataExtractionResult(
    [property: JsonPropertyName("documentName")] string DocumentName,
    [property: JsonPropertyName("footerId")] string FooterId,
    [property: JsonPropertyName("pageRange")] string PageRange,
    [property: JsonPropertyName("fields")] IReadOnlyList<ExtractedFieldValue> Fields,
    [property: JsonPropertyName("tables")] IReadOnlyList<ExtractedTableValue> Tables);

/// <summary>
/// Result of evaluating an admin-defined Prompt Column against a PDF slice/document (processed as PDF file/base64).
/// </summary>
public sealed record PromptColumnResult(
    [property: JsonPropertyName("columnId")] string ColumnId,
    [property: JsonPropertyName("columnName")] string ColumnName,
    [property: JsonPropertyName("promptInstructions")] string PromptInstructions,
    [property: JsonPropertyName("filterSummary")] string FilterSummary,
    [property: JsonPropertyName("filterMatched")] bool FilterMatched,
    [property: JsonPropertyName("matchedEntities")] IReadOnlyList<string> MatchedEntities,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("resultText")] string ResultText,
    [property: JsonPropertyName("processedAsFileBase64")] bool ProcessedAsFileBase64,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("fileSizeBytes")] int FileSizeBytes,
    [property: JsonPropertyName("base64Preview")] string Base64Preview,
    [property: JsonPropertyName("modelIdUsed")] string? ModelIdUsed,
    [property: JsonPropertyName("segmentIndex")] int SegmentIndex,
    [property: JsonPropertyName("documentName")] string DocumentName,
    [property: JsonPropertyName("pageRange")] string PageRange,
    [property: JsonPropertyName("evaluatedAtUtc")] DateTimeOffset EvaluatedAtUtc,
    [property: JsonPropertyName("inputFingerprint")] string? InputFingerprint = null);

public sealed record ExtractedEntity(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("confidence")] double Confidence = 0,
    [property: JsonPropertyName("sourceSliceIndex")] int? SourceSliceIndex = null,
    [property: JsonPropertyName("provenance")] ValueProvenance? Provenance = null,
    [property: JsonPropertyName("confidenceSignals")] ConfidenceSignals? ConfidenceSignals = null);

/// <summary>
/// Represents a single logical sub-document identified and sliced from a multi-document file.
/// </summary>
public sealed class DocumentSlice
{
    [JsonPropertyName("segmentIndex")]
    public int SegmentIndex { get; set; }

    [JsonPropertyName("documentTypeName")]
    public string? DocumentTypeName { get; set; }

    [JsonPropertyName("identifiedDocumentName")]
    public string? IdentifiedDocumentName { get; set; }

    /// <summary>Selected-class probability for native Microsoft Decision, or the configured chat model's confidence.</summary>
    [JsonPropertyName("identificationConfidence")]
    public double IdentificationConfidence { get; set; }

    [JsonPropertyName("matchedIdentificationRule")]
    public string? MatchedIdentificationRule { get; set; }

    [JsonPropertyName("isInferred")]
    public bool IsInferred { get; set; }

    [JsonPropertyName("startPage")]
    public int StartPage { get; set; }

    [JsonPropertyName("endPage")]
    public int EndPage { get; set; }

    [JsonPropertyName("footers")]
    public IReadOnlyList<string> Footers { get; set; } = [];

    [JsonPropertyName("isAmendment")]
    public bool IsAmendment { get; set; }

    [JsonPropertyName("amendmentId")]
    public string? AmendmentId { get; set; }

    [JsonPropertyName("schemaId")]
    public string? SchemaId { get; set; }

    [JsonPropertyName("detectedLanguage")]
    public string? DetectedLanguage { get; set; }

    /// <summary>Per-logical-document checks and uncertainty; historical snapshots keep their original serialization.</summary>
    [JsonPropertyName("languageDetection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentLanguageResult? LanguageDetection { get; set; }

    [JsonPropertyName("targetLanguage")]
    public string? TargetLanguage { get; set; }

    [JsonPropertyName("wasTranslated")]
    public bool WasTranslated { get; set; }

    [JsonPropertyName("translationErrorCode")]
    public string? TranslationErrorCode { get; set; }

    [JsonPropertyName("translationErrorMessage")]
    public string? TranslationErrorMessage { get; set; }

    [JsonPropertyName("translatedText")]
    public string? TranslatedText { get; set; }

    [JsonPropertyName("insightSummary")]
    public string? InsightSummary { get; set; }

    [JsonPropertyName("extractedEntities")]
    public List<ExtractedEntity> ExtractedEntities { get; set; } = [];

    [JsonPropertyName("dataExtraction")]
    public DocumentDataExtractionResult? DataExtraction { get; set; }

    [JsonPropertyName("promptColumnResults")]
    public List<PromptColumnResult> PromptColumnResults { get; set; } = [];

    [JsonPropertyName("markdown")]
    public string Markdown { get; set; } = string.Empty;

    [JsonPropertyName("bodyText")]
    public string BodyText { get; set; } = string.Empty;

    [JsonPropertyName("splitPdfBlobUri")]
    public string? SplitPdfBlobUri { get; set; }

    [JsonIgnore]
    public byte[]? SplitPdfBytes { get; set; }

    [JsonPropertyName("splitPdfSizeBytes")]
    public int SplitPdfSizeBytes { get; set; }

    [JsonPropertyName("translatedPdfBlobUri")]
    public string? TranslatedPdfBlobUri { get; set; }

    [JsonIgnore]
    public byte[]? TranslatedPdfBytes { get; set; }

    [JsonPropertyName("translatedPdfSizeBytes")]
    public int TranslatedPdfSizeBytes { get; set; }
}

/// <summary>
/// Telemetry trace entry recorded by each executor in the Microsoft Agent Framework workflow.
/// </summary>
public sealed record WorkflowStepTrace(
    string ExecutorName,
    bool Executed,
    bool Succeeded,
    string? ModelIdUsed,
    double DurationMs,
    string? Notes);

/// <summary>
/// State message flowing through the Microsoft Agent Framework <c>DocumentSplittingWorkflow</c>.
/// </summary>
public sealed class SplittingState
{
    internal SemaphoreSlim ExecutionGate { get; } = new(1, 1);

    public bool HasDevelopmentResults { get; set; }

    public required TenantHarness Tenant { get; init; }

    public required string FileName { get; init; }

    public string DocumentId { get; init; } = Guid.NewGuid().ToString("N");

    public Guid CorrelationId { get; init; } = Guid.NewGuid();

    [JsonIgnore]
    public byte[]? SourcePdfBytes { get; init; }

    public List<PageLayout> Pages { get; set; } = [];

    public string? LayoutSha256 { get; set; }

    public List<DocumentSlice> Slices { get; set; } = [];

    public string StrategyUsed { get; set; } = "Skipped";

    public string? ModelIdUsed { get; set; }

    public string? PacketSummary { get; set; }

    public List<ExtractedEntity> ExtractedEntities { get; set; } = [];

    public List<DocumentDataExtractionResult> DataExtractions { get; set; } = [];

    public List<PromptColumnResult> PromptColumnResults { get; set; } = [];

    public List<WorkflowStepTrace> ExecutionTrace { get; set; } = [];
}
