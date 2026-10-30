using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;

namespace DocIntel.Core.Features.DocumentTranslation;

/// <summary>Creates the named, redirect-disabled HTTP client used by the language detection API.</summary>
public delegate HttpClient DocumentLanguageHttpClientFactory();

/// <summary>A reliable source language, or an explicitly mixed-language source.</summary>
public sealed record DocumentLanguageResult(string Language);

/// <summary>Detects language without translating document text or generating a PDF.</summary>
public interface IDocumentLanguageDetector
{
    Task<DocumentLanguageResult> DetectAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>A safe optional-feature failure; raw provider messages are not exposed to users.</summary>
public sealed class DocumentTranslationFailureException(
    string code, string message, string? language = null, int? providerStatus = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public string? Language { get; } = language;
    public int? ProviderStatus { get; } = providerStatus;
}

/// <summary>Uses the real Translator Detect API; the pinned text SDK does not expose this operation.</summary>
public sealed class AzureDocumentLanguageDetector(
    HttpClient httpClient, TokenCredential credential, Uri endpoint) : IDocumentLanguageDetector, IDisposable
{
    private const int MaximumResponseBytes = 65_536;
    private const int MaximumSourceCharacters = 1_000_000;
    private readonly Uri _uri = DetectionUri(endpoint);

    public async Task<DocumentLanguageResult> DetectAsync(string text, CancellationToken cancellationToken = default)
    {
        var samples = Samples(text);
        if (samples.Count == 0) throw UnknownLanguage();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in samples.Chunk(30))
            languages.UnionWith(await DetectBatchAsync(batch, deadline.Token));
        return new(languages.Count == 1 ? languages.Single() : "mul");
    }

    private async Task<HashSet<string>> DetectBatchAsync(IReadOnlyList<string> samples, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _uri)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
                samples.Select(sample => new { Text = sample })))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var token = await credential.GetTokenAsync(new TokenRequestContext(
            ["https://cognitiveservices.azure.com/.default"]), cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Document language detection was rejected.", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (body.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("Language detection response exceeds its supported size.");
            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        using var document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, checked((int)body.Length)));
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() != samples.Count)
            throw new InvalidDataException("Language detection returned an invalid result count.");
        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("language", out var value) || value.ValueKind != JsonValueKind.String ||
                value.GetString() is not { } language ||
                !Regex.IsMatch(language, "^[a-zA-Z]{2,3}(?:-[a-zA-Z]{2,4})?$") ||
                language.Equals("und", StringComparison.OrdinalIgnoreCase) ||
                !item.TryGetProperty("score", out var score) || score.ValueKind != JsonValueKind.Number ||
                !score.TryGetDouble(out var confidence) ||
                !Double.IsFinite(confidence) || confidence is < 0.8 or > 1 ||
                !item.TryGetProperty("isTranslationSupported", out var supported) ||
                supported.ValueKind != JsonValueKind.True)
                throw UnknownLanguage();
            languages.Add(language);
        }
        return languages;
    }

    public void Dispose() => httpClient.Dispose();

    internal static IReadOnlyList<string> Samples(string text)
    {
        const int length = 1500;
        if (text.Length > MaximumSourceCharacters) throw UnknownLanguage();
        var result = new List<string>();
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset < text.Length;)
        {
            var remaining = text.Length - offset;
            var count = remaining > length && remaining < length + 300
                ? remaining / 2 : Math.Min(length, remaining);
            if (count > 0 && offset + count < text.Length && Char.IsHighSurrogate(text[offset + count - 1])) count--;
            if (count < remaining)
            {
                for (var end = offset + count; end > offset + count - 128 && end > offset; end--)
                {
                    if (!Char.IsWhiteSpace(text[end - 1])) continue;
                    count = end - offset;
                    break;
                }
            }
            var sample = text.Substring(offset, count);
            if (sample.Any(Char.IsLetter) && distinct.Add(sample))
                result.Add(sample);
            offset += count;
        }
        return result;
    }

    private static Uri DetectionUri(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps &&
            (endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback) ||
            endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 ||
            endpoint.AbsolutePath != "/")
            throw new ArgumentException("Language detection requires a trusted HTTPS resource root.");
        return new(endpoint, "translator/text/v3.0/detect?api-version=3.0");
    }

    private static DocumentTranslationFailureException UnknownLanguage() => new(
        "LanguageUndetermined",
        "Source language could not be established reliably across the source text; translation was not attempted.", "und");
}

internal sealed class UnconfiguredDocumentLanguageDetector : IDocumentLanguageDetector
{
    public Task<DocumentLanguageResult> DetectAsync(string text, CancellationToken cancellationToken = default) =>
        throw new DocumentTranslationFailureException("TranslationNotConfigured",
            "The language detection provider is not configured; original source was retained.");
}
