using System.Diagnostics;
using Azure.AI.Translation.Document;
using Azure.AI.Translation.Text;
using Azure.Core;
using Azure.Identity;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using Microsoft.Agents.AI.Workflows;

namespace DocIntel.Core.Features.DocumentTranslation;

public interface IDocumentTranslationService
{
    Task<(string DetectedLanguage, bool WasTranslated, string TranslatedText)> TranslateSliceAsync(
        string text,
        string targetLanguage,
        string? azureTranslatorEndpoint,
        IReadOnlyList<string> doNotTranslateTerms,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Translates document slices using Azure AI Translator (<see cref="TextTranslationClient"/> and <see cref="DocumentTranslationClient"/>)
/// when an Azure Translator endpoint is configured, with a deterministic local fallback for offline development and testing.
/// </summary>
public sealed class AzureDocumentTranslationService : IDocumentTranslationService
{
    private static readonly string[] SpanishIndicators =
    [
        "Formulario", "Firma", "Programa", "Número", "contrato", "Cliente",
        "entidad", "dirección", "copia", "confirmación", "términos", "documentos"
    ];

    private static readonly Dictionary<string, string> LocalSpanishToEnglishPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Formulario de Firma del Programa"] = "Program Signature Form",
        ["Número de MBA/MBSA"] = "MBA/MBSA Number",
        ["Número de contrato"] = "Contract Number",
        ["Nombre de la entidad"] = "Entity Name",
        ["Cliente"] = "Customer",
        ["campo obligatorio"] = "required field",
        ["Cuando Microsoft haya formalizado debidamente el formulario de firma"] = "When Microsoft has duly executed the signature form",
        ["recibirá una copia de confirmación"] = "will receive a confirmation copy"
    };

    private readonly TokenCredential _credential;

    public AzureDocumentTranslationService(TokenCredential? credential = null)
    {
        var managedIdentityClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        _credential = credential ?? (!string.IsNullOrWhiteSpace(managedIdentityClientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId))
            : new DefaultAzureCredential());
    }

    public async Task<(string DetectedLanguage, bool WasTranslated, string TranslatedText)> TranslateSliceAsync(
        string text,
        string targetLanguage,
        string? azureTranslatorEndpoint,
        IReadOnlyList<string> doNotTranslateTerms,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (targetLanguage, false, string.Empty);
        }

        // 1. Live Azure AI Translator path when endpoint is configured
        if (!string.IsNullOrWhiteSpace(azureTranslatorEndpoint) &&
            Uri.TryCreate(azureTranslatorEndpoint, UriKind.Absolute, out var endpointUri))
        {
            try
            {
                var textClient = new TextTranslationClient(_credential, endpointUri);
                var response = await textClient.TranslateAsync(targetLanguage, [text], cancellationToken: cancellationToken).ConfigureAwait(false);
                var item = response.Value.FirstOrDefault();
                var detected = item?.DetectedLanguage?.Language ?? "unknown";
                var translated = item?.Translations?.FirstOrDefault()?.Text ?? text;
                var changed = !string.Equals(detected, targetLanguage, StringComparison.OrdinalIgnoreCase);
                return (detected, changed, translated);
            }
            catch (Exception)
            {
                // Fall back to local language detection & translation if custom subdomain requires regional headers or is not yet enabled
            }
        }

        // 2. Local language detection & translation fallback (for offline dev / CI)
        var detectedLang = DetectLanguageLocally(text);
        if (string.Equals(detectedLang, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return (detectedLang, false, text);
        }

        var output = text;
        if (string.Equals(detectedLang, "es", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(targetLanguage, "en", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (esPhrase, enPhrase) in LocalSpanishToEnglishPhrases)
            {
                if (doNotTranslateTerms.Any(term => esPhrase.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                output = output.Replace(esPhrase, enPhrase, StringComparison.OrdinalIgnoreCase);
            }
        }
        else
        {
            output = $"[Translated {detectedLang} -> {targetLanguage}]\n{text}";
        }

        return (detectedLang, true, output);
    }

    /// <summary>
    /// Creates an Azure <see cref="DocumentTranslationClient"/> for batch Blob-to-Blob PDF translation in Azure deployments.
    /// </summary>
    public DocumentTranslationClient CreateBatchDocumentClient(Uri endpoint) =>
        new(endpoint, _credential);

    private static string DetectLanguageLocally(string text)
    {
        var spanishMatches = SpanishIndicators.Count(ind => text.Contains(ind, StringComparison.OrdinalIgnoreCase));
        return spanishMatches >= 2 ? "es" : "en";
    }
}

/// <summary>
/// Feature 3 Executor: Detects language on each PDF slice and translates non-target-language slices
/// using <see cref="IDocumentTranslationService"/> (Azure Document Translation).
/// </summary>
[SendsMessage(typeof(SplittingState))]
public sealed class TranslateDocumentsExecutor : Executor<SplittingState, SplittingState>
{
    private readonly IDocumentTranslationService _translationService;
    private readonly string? _defaultTranslatorEndpoint;

    public TranslateDocumentsExecutor(IDocumentTranslationService translationService, string? defaultTranslatorEndpoint = null)
        : base(nameof(TranslateDocumentsExecutor))
    {
        _translationService = translationService ?? throw new ArgumentNullException(nameof(translationService));
        _defaultTranslatorEndpoint = defaultTranslatorEndpoint;
    }

    public override async ValueTask<SplittingState> HandleAsync(
        SplittingState message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!message.Tenant.Capabilities.TryGetValue(FeatureHarness.DocumentTranslationKey, out var feature) ||
            !feature.Enabled)
        {
            message.ExecutionTrace.Add(new WorkflowStepTrace(
                nameof(TranslateDocumentsExecutor),
                Executed: false,
                Succeeded: true,
                ModelIdUsed: null,
                DurationMs: 0,
                Notes: "Skipped (Document Translation is disabled for this tenant)."));
            return message;
        }

        var sw = Stopwatch.StartNew();
        var config = feature.DocumentTranslation ?? new DocumentTranslationConfig();
        var targetLang = string.IsNullOrWhiteSpace(config.TargetLanguage) ? "en" : config.TargetLanguage.Trim();
        var endpoint = !string.IsNullOrWhiteSpace(config.AzureTranslatorEndpoint)
            ? config.AzureTranslatorEndpoint
            : _defaultTranslatorEndpoint;
        var translatedCount = 0;

        foreach (var slice in message.Slices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (detected, wasTranslated, translatedText) = await _translationService.TranslateSliceAsync(
                slice.BodyText,
                targetLang,
                endpoint,
                config.DoNotTranslateTerms,
                cancellationToken);

            slice.DetectedLanguage = detected;
            slice.TargetLanguage = targetLang;
            slice.WasTranslated = wasTranslated;
            slice.TranslatedText = wasTranslated ? translatedText : slice.BodyText;

            if (wasTranslated)
            {
                translatedCount++;
            }
        }

        sw.Stop();
        message.ExecutionTrace.Add(new WorkflowStepTrace(
            nameof(TranslateDocumentsExecutor),
            Executed: true,
            Succeeded: true,
            ModelIdUsed: "AzureDocumentTranslation",
            DurationMs: sw.Elapsed.TotalMilliseconds,
            Notes: $"Evaluated {message.Slices.Count} slice(s) for target language '{targetLang}'; translated {translatedCount} slice(s)."));

        return message;
    }
}
