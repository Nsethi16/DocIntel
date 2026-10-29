using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DocIntel.Core.Features.AskAi;

/// <summary>Allowlisted provider diagnostics, without raw error messages, requests or document content.</summary>
public sealed record AskAiProviderFailure(
    [property: JsonPropertyName("httpStatus")] int? HttpStatus,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("parameter")] string? Parameter,
    [property: JsonPropertyName("schemaKeyword")] string? SchemaKeyword,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("hints")] IReadOnlyList<string> Hints)
{
    private static readonly string[] Codes =
    [
        "invalid_function_parameters", "invalid_json_schema", "string_above_max_length",
        "unsupported_parameter", "unsupported_value", "context_length_exceeded",
        "rate_limit_exceeded", "insufficient_quota", "quota_exceeded", "content_filter",
        "DeploymentNotFound", "invalid_request_error"
    ];

    /// <summary>Classifies SDK failures using a fixed allowlist; never returns the provider's raw message.</summary>
    public static AskAiProviderFailure? FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is not ClientResultException provider) continue;
            var message = provider.Message[..Math.Min(provider.Message.Length, 16_384)];
            try
            {
                var content = provider.GetRawResponse()?.Content;
                if (content is not null && content.ToMemory().Length <= 65_536)
                {
                    using var document = JsonDocument.Parse(content.ToMemory());
                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("error", out var error) &&
                        error.ValueKind == JsonValueKind.Object)
                        return Summarize(provider.Status, Text(error, "message") ?? message,
                            Text(error, "code") ?? Text(error, "type"), Text(error, "param"), "Response");
                }
            }
            catch (Exception problem) when (problem is JsonException or InvalidOperationException)
            {
                return Summarize(provider.Status, message, null, null, "MessageOnly");
            }
            return Summarize(provider.Status, message, null, null, "MessageOnly");
        }
        return null;
    }

    private static AskAiProviderFailure Summarize(
        int status, string message, string? candidate, string? candidateParameter, string origin)
    {
        message = message[..Math.Min(message.Length, 16_384)];
        var code = candidate is null ? Codes.FirstOrDefault(value =>
            message.Contains(value, StringComparison.OrdinalIgnoreCase)) :
            Codes.FirstOrDefault(value => String.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));
        var parameter = Regex.Match(candidateParameter ?? message,
                @"\b(?:tools\[\d{1,2}\]\.function\.(?:parameters|description|name)|" +
                @"response_format(?:\.json_schema(?:\.schema)?)?|messages|max_completion_tokens|model)\b",
                RegexOptions.CultureInvariant);
        var keyword = new[] { "additionalProperties", "required", "$defs", "$ref", "default", "format" }
            .FirstOrDefault(value => message.Contains(value, StringComparison.Ordinal));
        var terms = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tool_choice"] = "ToolChoice", ["tools"] = "Tools", ["function"] = "Functions",
            ["response_format"] = "ResponseFormat", ["json"] = "Json",
            ["api-version"] = "ApiVersion", ["API version"] = "ApiVersion",
            ["max_tokens"] = "MaxTokens", ["max_completion_tokens"] = "MaxCompletionTokens",
            ["token"] = "Tokens", ["reasoning"] = "Reasoning", ["operation"] = "Operation",
            ["not support"] = "Unsupported", ["not available"] = "Unavailable",
            ["not found"] = "NotFound", ["combination"] = "Combination", ["simultaneous"] = "Combination"
        };
        var hints = terms.Where(term => message.Contains(term.Key, StringComparison.OrdinalIgnoreCase))
            .Select(term => term.Value).Distinct(StringComparer.Ordinal).ToArray();
        return new(status is >= 100 and <= 599 ? status : null, code ?? "Unknown",
            parameter.Success ? parameter.Value : null, keyword, origin, hints);
    }

    private static string? Text(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Maps the safe provider response category to an actionable application error code.</summary>
    [JsonIgnore]
    public string FailureCode => Code switch
    {
        "context_length_exceeded" => "ProviderContextLimit",
        "insufficient_quota" or "quota_exceeded" => "ProviderQuotaExceeded",
        "invalid_function_parameters" or "invalid_json_schema" or "string_above_max_length" =>
            "ProviderSchemaRejected",
        "content_filter" => "ProviderContentFiltered",
        _ => HttpStatus switch
        {
            401 or 403 => "ProviderAccessDenied",
            404 => "ProviderDeploymentUnavailable",
            408 or 504 => "ProviderTimedOut",
            429 => "ProviderRateLimited",
            >= 500 => "ProviderUnavailable",
            _ => Parameter is not null && SchemaKeyword is not null &&
                (Parameter.StartsWith("tools[", StringComparison.Ordinal) ||
                 Parameter.StartsWith("response_format", StringComparison.Ordinal))
                    ? "ProviderSchemaRejected" : "ProviderRequestRejected"
        }
    };
}
