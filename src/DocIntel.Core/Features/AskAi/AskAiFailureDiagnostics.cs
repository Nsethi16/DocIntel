namespace DocIntel.Core.Features.AskAi;

/// <summary>Maps known protocol failures to fixed diagnostic codes without exposing model or document content.</summary>
public static class AskAiFailureDiagnostics
{
    /// <summary>Classifies only server-owned failure messages; unknown errors remain unclassified.</summary>
    public static string GetCode(Exception exception) => exception.Message switch
    {
        "The model returned an invalid answer or unrecognized citation." => "InvalidModelAnswer",
        "Document answers require evidence; conversational answers do not." => "MissingEvidence",
        "The answer's citation markers do not match its evidence." => "CitationMismatch",
        "The Ask AI tool-call limit was reached. Narrow the question." => "ToolCallLimit",
        "The evidence limit was reached. Narrow the question." => "EvidenceLimit",
        "Document retrieval failed; no answer was produced."
            when exception.GetBaseException() is ArgumentException => "InvalidToolArguments",
        "Document retrieval failed; no answer was produced." => "RetrievalFailed",
        _ => "ExecutionFailed"
    };

    /// <summary>Returns a safe user-facing explanation, never a provider exception message.</summary>
    public static string GetMessage(string code) => code switch
    {
        "InvalidModelAnswer" => "The model returned an invalid answer or an unknown citation. No answer was returned.",
        "MissingEvidence" => "The model's answer did not supply the required document evidence. No answer was returned.",
        "CitationMismatch" => "The model's citation markers did not match its evidence. No answer was returned.",
        "ToolCallLimit" or "EvidenceLimit" => "This question exceeded its search or evidence limit. Narrow the question.",
        "InvalidToolArguments" => "The model generated invalid document-search or inspection arguments. No answer was returned.",
        "RetrievalFailed" => "Document retrieval failed. No answer was returned.",
        _ => "Ask AI could not complete this request. Check the run diagnostics or try again."
    };
}
