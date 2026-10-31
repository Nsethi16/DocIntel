using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;

namespace DocIntel.Core.Features.DocumentTranslation;

/// <summary>Creates the named, redirect-disabled HTTP client used by the language detection API.</summary>
public delegate HttpClient DocumentLanguageHttpClientFactory();

/// <summary>A bounded language check without retaining the checked source text.</summary>
public sealed record DocumentLanguageSample(
    int SampleIndex, int CharacterCount, string Language, double Score, bool IsTranslationSupported)
{
    public bool IsUsable => IsTranslationSupported &&
        !Language.Equals("und", StringComparison.OrdinalIgnoreCase) &&
        Score >= DocumentLanguageResult.MinimumUsableScore;
}

/// <summary>Source-language evidence for one logical document, including uncertainty and batch routing.</summary>
public sealed record DocumentLanguageResult(string Language)
{
    public const double MinimumUsableScore = 0.5;
    public const double SameLanguageSkipScore = 0.8;

    /// <summary>Mixed or partly uncertain evidence cannot safely select one source language or skip translation.</summary>
    public bool UseAutomaticSourceLanguage { get; init; }

    public IReadOnlyList<DocumentLanguageSample> Samples { get; init; } = [];
    public int UsableSampleCount => Samples.Count(sample => sample.IsUsable);
}

/// <summary>Detects language without translating document text or generating a PDF.</summary>
public interface IDocumentLanguageDetector
{
    Task<DocumentLanguageResult> DetectAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>A safe optional-feature failure; raw provider messages are not exposed to users.</summary>
public sealed class DocumentTranslationFailureException(
    string code, string message, string? language = null, int? providerStatus = null,
    Exception? innerException = null, DocumentLanguageResult? languageDetection = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public string? Language { get; } = language;
    public int? ProviderStatus { get; } = providerStatus;
    public DocumentLanguageResult? LanguageDetection { get; } = languageDetection;
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
        var checks = new List<DocumentLanguageSample>();
        foreach (var batch in samples.Chunk(30))
            checks.AddRange(await DetectBatchAsync(batch, checks.Count, deadline.Token));
        var languages = checks.Where(sample => sample.IsUsable).Select(sample => sample.Language)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var detection = new DocumentLanguageResult(languages.Length switch
        {
            0 => "und",
            1 => languages[0],
            _ => "mul"
        })
        {
            Samples = checks,
            UseAutomaticSourceLanguage = languages.Length != 1 ||
                languages.Any(language => language.Equals("mul", StringComparison.OrdinalIgnoreCase)) ||
                checks.Any(sample => !sample.IsUsable || sample.Score < DocumentLanguageResult.SameLanguageSkipScore)
        };
        if (languages.Length == 0) throw UnknownLanguage(detection);
        return detection;
    }

    private async Task<IReadOnlyList<DocumentLanguageSample>> DetectBatchAsync(
        IReadOnlyList<string> samples, int firstSampleIndex, CancellationToken cancellationToken)
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
        var checks = new List<DocumentLanguageSample>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("language", out var value) || value.ValueKind != JsonValueKind.String ||
                value.GetString() is not { } language ||
                !Regex.IsMatch(language, "^[a-zA-Z]{2,3}(?:-[a-zA-Z]{2,4})?$") ||
                !item.TryGetProperty("score", out var score) || score.ValueKind != JsonValueKind.Number ||
                !score.TryGetDouble(out var confidence) ||
                !Double.IsFinite(confidence) || confidence is < 0 or > 1 ||
                !item.TryGetProperty("isTranslationSupported", out var supported) ||
                supported.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Language detection returned an invalid sample result.");
            checks.Add(new(firstSampleIndex + checks.Count, samples[checks.Count].Length,
                language, confidence, supported.GetBoolean()));
        }
        return checks;
    }

    public void Dispose() => httpClient.Dispose();

    internal static IReadOnlyList<string> Samples(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
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

    private static DocumentTranslationFailureException UnknownLanguage(DocumentLanguageResult? detection = null) => new(
        "LanguageUndetermined",
        "No supported source-language chunk met the minimum detection score of 0.5, or source text was unavailable " +
        "within the detection limit; translation was not attempted.", "und", languageDetection: detection);
}

internal sealed class UnconfiguredDocumentLanguageDetector : IDocumentLanguageDetector
{
    public Task<DocumentLanguageResult> DetectAsync(string text, CancellationToken cancellationToken = default) =>
        throw new DocumentTranslationFailureException("TranslationNotConfigured",
            "The language detection provider is not configured; original source was retained.");
}
