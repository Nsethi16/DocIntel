using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;

namespace DocIntel.Core.Models;

/// <summary>
/// Makes bounded, typed choices using a configured Microsoft Decision deployment.
/// </summary>
public interface IMicrosoftDecisionClient : IDisposable
{
    /// <summary>
    /// Selects one supplied label and returns its provider-reported probability.
    /// </summary>
    /// <param name="state">The document text or JSON to classify.</param>
    /// <param name="instructions">Instructions for evaluating the document.</param>
    /// <param name="criteria">Two to 255 case-sensitive labels and their nonempty descriptions.</param>
    /// <param name="cancellationToken">Cancels authentication, inference, and response reading.</param>
    /// <returns>The selected label, its probability, and the configured deployment name.</returns>
    Task<MicrosoftDecisionResult> ChooseAsync(
        string state,
        string instructions,
        IReadOnlyDictionary<string, string> criteria,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A validated native Microsoft Decision choice, without generated reasoning or inferred scores.
/// </summary>
/// <param name="Choice">The exact selected criterion label.</param>
/// <param name="Probability">The selected label's probability, not the separate assessment confidence.</param>
/// <param name="DeploymentName">The case-sensitive deployment used for the request.</param>
public sealed record MicrosoftDecisionResult(string Choice, double Probability, string DeploymentName);

/// <summary>
/// Calls the native Microsoft provider systemone endpoint using Microsoft Entra authentication.
/// </summary>
/// <remarks>
/// Owns the supplied HTTP client, but not the credential. The client's handler must disable automatic redirects.
/// Each operation has a 60-second deadline, including authentication and response reading.
/// Responses are limited to 256 KiB. Probabilities must sum to 1 within an absolute tolerance of 0.001
/// for provider rounding; probabilities are never normalized or substituted.
/// </remarks>
public sealed class MicrosoftDecisionClient : IMicrosoftDecisionClient
{
    /// <summary>
    /// The named HTTP client whose handler disables automatic redirects.
    /// </summary>
    public const string HttpClientName = "MicrosoftDecision";

    private const string TokenScope = "https://cognitiveservices.azure.com/.default";
    private const string QuestionName = "document_type";
    private const int MaximumResponseBytes = 256 * 1024;
    private const int MaximumCriteriaCount = 255;
    private const double ProbabilitySumTolerance = 0.001;
    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;
    private readonly Uri _requestUri;
    private readonly string _deploymentName;
    private int _disposed;

    /// <summary>
    /// Creates a native Decision client for a resource root and an exact deployment name.
    /// </summary>
    /// <param name="httpClient">An owned HTTP client with automatic redirects disabled.</param>
    /// <param name="credential">The caller-owned Microsoft Entra credential.</param>
    /// <param name="endpoint">An HTTPS resource root, or an HTTP loopback root for local protocol tests.</param>
    /// <param name="deploymentName">The configured, case-sensitive deployment name, not a model alias.</param>
    public MicrosoftDecisionClient(
        HttpClient httpClient,
        TokenCredential credential,
        Uri endpoint,
        string deploymentName)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        if (!endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != Uri.UriSchemeHttps &&
             (endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback)) ||
            !String.IsNullOrEmpty(endpoint.UserInfo) ||
            !String.IsNullOrEmpty(endpoint.Query) ||
            !String.IsNullOrEmpty(endpoint.Fragment) ||
            endpoint.AbsolutePath != "/")
        {
            throw new ArgumentException(
                "Microsoft Decision requires an HTTPS resource root or an HTTP loopback resource root.",
                nameof(endpoint));
        }

        _httpClient = httpClient;
        _credential = credential;
        _requestUri = new Uri(endpoint, "providers/microsoft/v1/systemone");
        _deploymentName = deploymentName;
    }

    /// <inheritdoc />
    public async Task<MicrosoftDecisionResult> ChooseAsync(
        string state,
        string instructions,
        IReadOnlyDictionary<string, string> criteria,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        ArgumentNullException.ThrowIfNull(criteria);
        var choices = SnapshotCriteria(criteria);
        cancellationToken.ThrowIfCancellationRequested();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            var token = await GetTokenAsync(deadline.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, _requestUri)
            {
                Content = JsonContent.Create(new
                {
                    model = _deploymentName,
                    state,
                    questions = new
                    {
                        document_type = new { type = "choice", instructions, criteria = choices }
                    }
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Microsoft Decision returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            }

            using var body = await ReadBoundedBodyAsync(response.Content, deadline.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(
                body.GetBuffer().AsMemory(0, checked((int)body.Length)),
                new JsonDocumentOptions { MaxDepth = 16 });
            deadline.Token.ThrowIfCancellationRequested();
            return ReadChoice(document.RootElement, choices);
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException(
                "Microsoft Decision was canceled or exceeded its time limit.",
                cancellationToken.IsCancellationRequested ? cancellationToken : deadline.Token);
        }
        catch (HttpRequestException exception)
        {
            var message = exception.StatusCode is { } status
                ? $"Microsoft Decision returned HTTP {(int)status}."
                : "Microsoft Decision request failed.";
            throw new HttpRequestException(message, null, exception.StatusCode);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Microsoft Decision returned invalid JSON.");
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("Microsoft Decision returned an invalid choice response.");
        }
        catch (IOException)
        {
            throw new InvalidDataException("Microsoft Decision returned an unreadable or invalid choice response.");
        }
        catch (FormatException)
        {
            throw new InvalidDataException("Microsoft Decision received invalid provider protocol data.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _httpClient.Dispose();
        }
    }

    private async ValueTask<AccessToken> GetTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _credential.GetTokenAsync(
                new TokenRequestContext([TokenScope]), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Credential providers can include sensitive provider payloads in their exception chains.
            throw new InvalidOperationException("Microsoft Decision authentication failed.");
        }
    }

    private static Dictionary<string, string> SnapshotCriteria(IReadOnlyDictionary<string, string> criteria)
    {
        if (criteria.Count is < 2 or > MaximumCriteriaCount)
        {
            throw new ArgumentException("Microsoft Decision requires 2 to 255 distinct criteria.", nameof(criteria));
        }

        var choices = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var criterion in criteria)
        {
            if (String.IsNullOrWhiteSpace(criterion.Key) || String.IsNullOrWhiteSpace(criterion.Value) ||
                !choices.TryAdd(criterion.Key, criterion.Value) || choices.Count > MaximumCriteriaCount)
            {
                throw new ArgumentException(
                    "Microsoft Decision criteria require distinct nonempty labels and descriptions.",
                    nameof(criteria));
            }
        }

        if (choices.Count < 2)
        {
            throw new ArgumentException(
                "Microsoft Decision requires at least two distinct criteria.", nameof(criteria));
        }

        return choices;
    }

    private static async Task<MemoryStream> ReadBoundedBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidDataException("Microsoft Decision response exceeded its size limit.");
        }

        using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var body = new MemoryStream();
        try
        {
            var buffer = new byte[8192];
            while (true)
            {
                var remaining = MaximumResponseBytes - checked((int)body.Length);
                var count = await source.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)),
                    cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    return body;
                }

                if (count > remaining)
                {
                    throw new InvalidDataException("Microsoft Decision response exceeded its size limit.");
                }

                body.Write(buffer, 0, count);
            }
        }
        catch
        {
            body.Dispose();
            throw;
        }
    }

    private MicrosoftDecisionResult ReadChoice(JsonElement root, Dictionary<string, string> criteria)
    {
        ValidateUniqueProperties(root);
        if (root.ValueKind != JsonValueKind.Object || HasFailure(root) ||
            !root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object ||
            answers.EnumerateObject().Count() != 1 ||
            !answers.TryGetProperty(QuestionName, out var answer) || answer.ValueKind != JsonValueKind.Object ||
            HasFailure(answer) ||
            !answer.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            type.GetString() != "choice" ||
            !answer.TryGetProperty("choice", out var selected) || selected.ValueKind != JsonValueKind.String ||
            !criteria.ContainsKey(selected.GetString()!) ||
            !answer.TryGetProperty("probabilities", out var probabilities) ||
            probabilities.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Microsoft Decision returned an invalid choice response.");
        }

        var choice = selected.GetString()!;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        double sum = 0;
        double maximum = 0;
        double selectedProbability = 0;
        foreach (var probability in probabilities.EnumerateObject())
        {
            if (!criteria.ContainsKey(probability.Name) || !seen.Add(probability.Name) ||
                probability.Value.ValueKind != JsonValueKind.Number ||
                !probability.Value.TryGetDouble(out var value) || !Double.IsFinite(value) || value is < 0 or > 1)
            {
                throw new InvalidDataException("Microsoft Decision returned invalid class probabilities.");
            }

            sum += value;
            maximum = Math.Max(maximum, value);
            if (probability.Name == choice)
            {
                selectedProbability = value;
            }
        }

        if (seen.Count != criteria.Count ||
            sum < 1 - ProbabilitySumTolerance || sum > 1 + ProbabilitySumTolerance ||
            selectedProbability < maximum)
        {
            throw new InvalidDataException("Microsoft Decision returned inconsistent class probabilities.");
        }

        return new MicrosoftDecisionResult(choice, selectedProbability, _deploymentName);
    }

    private static bool HasFailure(JsonElement value) =>
        (value.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null) ||
        (value.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) ||
        (value.TryGetProperty("error_message", out var message) && message.ValueKind != JsonValueKind.Null);

    private static void ValidateUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("Microsoft Decision returned duplicate JSON properties.");
                }

                ValidateUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateUniqueProperties(item);
            }
        }
    }
}
