namespace DocIntel.Core.Features.AskAi;

/// <summary>Maps known protocol failures to fixed diagnostic codes without exposing model or document content.</summary>
public static class AskAiFailureDiagnostics
{
    /// <summary>Classifies only server-owned failure messages; unknown errors remain unclassified.</summary>
    public static string GetCode(Exception exception) =>
        AskAiProviderFailure.FromException(exception)?.FailureCode ?? (exception.Message switch
    {
        "The model returned an invalid answer or unrecognized citation." => "InvalidModelAnswer",
        "The model cited evidence that was not inspected." => "UnknownCitation",
        "Document answers require evidence; conversational answers do not." => "MissingEvidence",
        "The answer's citation markers do not match its evidence." => "CitationMismatch",
        "The Ask AI tool-call limit was reached. Narrow the question." => "ToolCallLimit",
        "The evidence limit was reached. Narrow the question." => "EvidenceLimit",
        "The Ask AI document scope limit was reached. Narrow the question." => "EvidenceLimit",
        "The Ask AI tool configuration exceeds the provider description limit." => "InvalidToolConfiguration",
        "A document used by this conversation changed; start a new question without prior context." => "SourceChanged",
        "Document retrieval failed; no answer was produced."
            when exception.GetBaseException() is ArgumentException => "InvalidToolArguments",
        "Document retrieval failed; no answer was produced." => "RetrievalFailed",
        _ => "ExecutionFailed"
    });

    /// <summary>Returns a safe user-facing explanation, never a provider exception message.</summary>
    public static string GetMessage(string code) => code switch
    {
        "InvalidModelAnswer" => "The model returned an invalid answer or an unknown citation. No answer was returned.",
        "UnknownCitation" => "The model cited evidence it had not inspected. No answer was returned.",
        "MissingEvidence" => "The model's answer did not supply the required document evidence. No answer was returned.",
        "CitationMismatch" => "The model's citation markers did not match its evidence. No answer was returned.",
        "ToolCallLimit" or "EvidenceLimit" => "This question exceeded its search or evidence limit. Narrow the question.",
        "InvalidToolArguments" => "The model generated invalid document-search or inspection arguments. No answer was returned.",
        "RetrievalFailed" => "Document retrieval failed. No answer was returned.",
        "InvalidToolConfiguration" => "Ask AI has an incompatible tool configuration. A workspace administrator must correct it.",
        "SourceChanged" => "A source document changed or prior evidence is no longer current. Start a new chat to search the current documents.",
        "ProviderContextLimit" => "The AI provider's context limit was reached. Ask a more focused question.",
        "ProviderQuotaExceeded" => "The configured AI provider has no available quota. A workspace administrator must review its quota.",
        "ProviderRateLimited" => "The AI provider's token or request rate limit was reached. Wait before retrying or ask a narrower question.",
        "ProviderSchemaRejected" => "The AI provider rejected a tool or response schema. Open run details for the safe diagnostic reference.",
        "ProviderAccessDenied" => "The AI provider denied the application's model access. A workspace administrator must check the model connection.",
        "ProviderDeploymentUnavailable" => "The configured model deployment is unavailable. A workspace administrator must check the deployment.",
        "ProviderTimedOut" => "The AI provider timed out. The question was not automatically resubmitted.",
        "ProviderUnavailable" => "The AI provider is temporarily unavailable. The question was not automatically resubmitted.",
        "ProviderContentFiltered" => "The AI provider declined this request under its content policy. No answer was returned.",
        "ProviderRequestRejected" => "The AI provider rejected the request. Open run details for its safe status and parameter reference.",
        _ => "Ask AI could not complete this request. Check the run diagnostics or try again."
    };
}
