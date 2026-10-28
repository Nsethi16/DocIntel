using System.Text.Json.Serialization;

namespace DocIntel.Core.Features.AskAi;

/// <summary>A bounded, evidence-backed analytical dataset and its declarative presentation.</summary>
public sealed class AskAiAnalysis
{
    [JsonPropertyName("analysisId")]
    public string AnalysisId { get; init; } = String.Empty;
    [JsonPropertyName("title")]
    public string Title { get; init; } = String.Empty;
    [JsonPropertyName("operation")]
    public string Operation { get; init; } = "rows";
    [JsonPropertyName("visualization")]
    public string Visualization { get; set; } = "table";
    [JsonPropertyName("xColumn")]
    public string? XColumn { get; init; }
    [JsonPropertyName("yColumn")]
    public string? YColumn { get; init; }
    [JsonPropertyName("columns")]
    public List<AskAiAnalysisColumn> Columns { get; init; } = [];
    [JsonPropertyName("rows")]
    public List<AskAiAnalysisRow> Rows { get; init; } = [];
    [JsonPropertyName("evidenceIds")]
    public List<string> EvidenceIds { get; init; } = [];
    [JsonPropertyName("notes")]
    public List<string> Notes { get; init; } = [];
    [JsonPropertyName("coverage")]
    public AskAiAnalysisCoverage Coverage { get; init; } = new();
}

/// <summary>One allowlisted text, number or date column; values never contain executable markup.</summary>
public sealed record AskAiAnalysisColumn(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] string Kind);

/// <summary>A computed row with citations resolved by the server before publication.</summary>
public sealed class AskAiAnalysisRow
{
    [JsonPropertyName("cells")]
    public Dictionary<string, object?> Cells { get; init; } = new(StringComparer.Ordinal);
    [JsonPropertyName("evidenceIds")]
    public List<string> EvidenceIds { get; init; } = [];
    [JsonPropertyName("citationNumbers")]
    public List<int> CitationNumbers { get; set; } = [];
}

/// <summary>Explicit dataset coverage; inspected evidence is not asserted to be an exhaustive corpus.</summary>
public sealed class AskAiAnalysisCoverage
{
    [JsonPropertyName("scope")]
    public string Scope { get; init; } = "InspectedEvidence";
    [JsonPropertyName("recordsIncluded")]
    public int RecordsIncluded { get; init; }
    [JsonPropertyName("missingValues")]
    public int MissingValues { get; init; }
    [JsonPropertyName("duplicatesExcluded")]
    public int DuplicatesExcluded { get; init; }
    [JsonPropertyName("documentsMatched")]
    public int DocumentsMatched { get; set; }
    [JsonPropertyName("documentsInspected")]
    public int DocumentsInspected { get; set; }
    [JsonPropertyName("hasMoreDocuments")]
    public bool HasMoreDocuments { get; set; }
}

/// <summary>A question-specific analysis assembled from inspected original-source quotations.</summary>
public sealed class AskAiAnalysisRequest
{
    public string Title { get; init; } = String.Empty;
    public string Operation { get; init; } = "rows";
    public string Visualization { get; init; } = "table";
    public string? GroupBy { get; init; }
    public string? ValueColumn { get; init; }
    public string DateBucket { get; init; } = "none";
    public List<AskAiAnalysisInputColumn> Columns { get; init; } = [];
    public List<AskAiAnalysisInputRow> Rows { get; init; } = [];
}

/// <summary>Explicit parsing formats prevent locale-dependent date and number conversions.</summary>
public sealed class AskAiAnalysisInputColumn
{
    public string Key { get; init; } = String.Empty;
    public string Label { get; init; } = String.Empty;
    public string Kind { get; init; } = "text";
    public string NumberFormat { get; init; } = "dot-decimal";
    public string DateFormat { get; init; } = "yyyy-MM-dd";
}

/// <summary>Raw source values must occur within this exact inspected source quotation.</summary>
public sealed class AskAiAnalysisInputRow
{
    public string EvidenceId { get; init; } = String.Empty;
    public string SourceQuote { get; init; } = String.Empty;
    public string? Unit { get; init; }
    public Dictionary<string, string?> Cells { get; init; } = new(StringComparer.Ordinal);
}
