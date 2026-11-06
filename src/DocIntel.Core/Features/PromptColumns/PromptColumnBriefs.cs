using System.Text;
using System.Text.RegularExpressions;

namespace DocIntel.Core.Features.PromptColumns;

/// <summary>Reusable, bounded prompt-column instructions and source-quote checks.</summary>
public static class PromptColumnBriefs
{
    public const string MissingEvidence = "Not stated in this document.";
    public const string Refinement =
        " Refinement: return only the three required Markdown bullets. Aim for 40 words including labels. " +
        "Answer the question directly in one short sentence. Copy a 3-12 word contiguous source excerpt exactly, " +
        "including punctuation, or use the unquoted missing-evidence marker. Give one short next action. " +
        "Do not repeat the question, add a heading, pad the answer or make unsupported claims.";

    private const string Format =
        " Use only this logical document; do not infer facts from another file or a referenced agreement. " +
        "Write plain English and at most 60 words in total, including labels. " +
        "Return exactly three Markdown bullets, one line per bullet: " +
        "**Answer:** one direct, specific answer, not an introduction or a clause inventory; " +
        "**Evidence:** one verbatim 3-18 word contiguous excerpt in ASCII double quotes, copied from the supplied source text; " +
        "**Next:** one practical recommended action and responsible role, or No action stated. " +
        "If the supporting fact is missing, write exactly Evidence: Not stated in this document. " +
        "without quotation marks, retaining the bold label. Missing is not zero or proof of a failure. " +
        "Preserve exact amounts, currencies, dates and approval/payment qualifiers. " +
        "Do not invent signatures, approvals, payments, page numbers or calculations. " +
        "Keep Answer below 24 words and Next below 16 words. No preamble, repeated question or conclusion.";

    public const string SummaryInstructions =
        "Summarize what this document means for the customer or business, focusing on its main purpose and " +
        "the single most important stated amount, obligation or decision. Omit secondary identifiers and background." + Format;
    public const string HighlightsInstructions =
        "Identify the two most decision-relevant commercial or operational facts, prioritizing value, timing " +
        "or an explicit delivery dependency. Do not list every term or repeat the document summary." + Format;
    public const string ContractInstructions =
        "State whether approval or execution is actually evidenced: Approval recorded, Signed in source, " +
        "Not evidenced or Not applicable, followed by the specific gap if any. " +
        "Recorded workflow approval is not proof of an executed or handwritten signature. " +
        "Do not claim verification, legal validity or compliance. A billing document is not execution evidence." + Format;
    public const string ClosingInstructions =
        "State Ready, Action needed, Not evidenced or Not applicable, and the single most important stated closing dependency. " +
        "Ready requires explicit completed approvals and no stated outstanding prerequisite. " +
        "Distinguish commercial approval, privacy clearance, purchase-order coverage and payment. " +
        "Use stated expiry dates, but do not claim something is overdue without a supplied reference date." + Format;
    public const string ExceptionsInstructions =
        "Name the single most material explicit commercial exception, condition or change and its stated business impact. " +
        "Prioritize an outstanding PO increase, fee change, liability qualifier or payment dependency. " +
        "Do not invent a company policy or call a term unusual/noncompliant without a supplied baseline. " +
        "If no exception is stated, say None stated, not No risk. Do not count invoices or copies as new sales." + Format;
    public const string LanguageInstructions =
        "State the source language or languages supported by the supplied text. " +
        "Do not claim a translation was performed, or that a document is monolingual, without evidence." + Format;

    /// <summary>Canonical templates used by workspace defaults and Admin quick-template buttons.</summary>
    public static IReadOnlyList<PromptColumnBriefTemplate> Templates { get; } = Array.AsReadOnly(
        new PromptColumnBriefTemplate[]
        {
            new("summary", "Summary", SummaryInstructions),
            new("highlights", "Highlights", HighlightsInstructions),
            new("contract", "Contract Check", ContractInstructions),
            new("closing", "Closing Readiness", ClosingInstructions),
            new("exceptions", "Commercial Exceptions", ExceptionsInstructions),
            new("language", "Language", LanguageInstructions)
        });

    /// <summary>Canonicalizes only an explicit missing-evidence marker, never a purported source quotation.</summary>
    public static string NormalizeMissingEvidence(string text)
    {
        if (!TryRead(text, out var fields) ||
            fields[1] != "\"" + MissingEvidence + "\"" && fields[1] != "\u201c" + MissingEvidence + "\u201d")
            return text;
        return $"- **Answer:** {fields[0]}\n- **Evidence:** {MissingEvidence}\n- **Next:** {fields[2]}";
    }

    /// <summary>Returns a fixed failure code without exposing generated or source text.</summary>
    public static string? GetError(string? text, string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!TryRead(text, out var fields) ||
            Regex.Matches(fields[0], @"\S+").Count > 24 ||
            Regex.Matches(fields[2], @"\S+").Count > 16)
            return "PromptNotConcise";
        if (fields[1] == MissingEvidence) return null;
        var quote = fields[1];
        if (quote.Length < 4 || quote[0] != '"' || quote[^1] != '"' ||
            Regex.Matches(quote[1..^1], @"\S+").Count is < 3 or > 18 ||
            !Normalize(source).Contains(Normalize(quote[1..^1]), StringComparison.Ordinal))
            return "PromptEvidenceMismatch";
        return null;
    }

    private static bool TryRead(string? text, out string[] fields)
    {
        fields = [];
        if (String.IsNullOrWhiteSpace(text) || Regex.Matches(text, @"\S+").Count > 60) return false;
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        string[] labels = ["Answer", "Evidence", "Next"];
        if (lines.Length != labels.Length) return false;
        var values = new List<string>();
        for (var index = 0; index < labels.Length; index++)
        {
            var prefix = "- **" + labels[index] + ":** ";
            if (!lines[index].StartsWith(prefix, StringComparison.Ordinal) || lines[index].Length <= prefix.Length)
                return false;
            values.Add(lines[index][prefix.Length..].Trim());
        }
        fields = values.ToArray();
        return true;
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"\s+", " ").Trim();
}

/// <summary>An immutable, reusable prompt preset; selecting it does not save workspace settings.</summary>
public sealed record PromptColumnBriefTemplate(string Key, string ColumnName, string Instructions);
