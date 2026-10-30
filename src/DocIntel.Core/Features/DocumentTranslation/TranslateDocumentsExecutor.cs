using System.Diagnostics;
using System.Text.Json;
using Azure;
using Azure.Identity;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Storage;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using UglyToad.PdfPig.Core;

namespace DocIntel.Core.Features.DocumentTranslation;

public sealed record DocumentSliceTranslationResult(
    string DetectedLanguage,
    bool WasTranslated,
    string TranslatedText,
    byte[]? TranslatedPdfBytes = null,
    bool IsDevelopmentSimulation = false);

public interface IDocumentTranslationService
{
    Task<(string DetectedLanguage, bool WasTranslated, string TranslatedText)> TranslateSliceAsync(
        string text,
        string targetLanguage,
        string? azureTranslatorEndpoint,
        IReadOnlyList<string> doNotTranslateTerms,
        CancellationToken cancellationToken = default);

    Task<DocumentSliceTranslationResult> TranslatePdfSliceAsync(
        byte[]? sourcePdfBytes,
        string sliceFileName,
        string fallbackBodyText,
        IReadOnlyList<string> footers,
        string targetLanguage,
        string? azureTranslatorEndpoint,
        IReadOnlyList<string> doNotTranslateTerms,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Translates PDF document slices using Azure AI Document Translation
/// using real language detection and the PDF-capable asynchronous Blob-to-Blob translation API,
/// preserving original PDF layout and formatting while applying custom TSV glossaries for protected terms.
/// </summary>
public sealed class AzureDocumentTranslationService : IDocumentTranslationService
{
    private static readonly string[] SpanishIndicators =
    [
        "Formulario", "Firma", "Programa", "Número", "Numero", "contrato", "Cliente",
        "entidad", "dirección", "direccion", "copia", "confirmación", "confirmacion", "términos", "terminos", "documentos"
    ];

    private static readonly Dictionary<string, string> LocalSpanishToEnglishPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Formulario de Firma del Programa"] = "Program Signature Form",
        ["Número de MBA/MBSA"] = "MBA/MBSA Number",
        ["Numero de MBA/MBSA"] = "MBA/MBSA Number",
        ["Número de contrato"] = "Contract Number",
        ["Numero de contrato"] = "Contract Number",
        ["Nombre de la entidad"] = "Entity Name",
        ["Cliente"] = "Customer",
        ["campo obligatorio"] = "required field",
        ["Cuando Microsoft haya formalizado debidamente el formulario de firma"] = "When Microsoft has duly executed the signature form",
        ["recibirá una copia de confirmación"] = "will receive a confirmation copy",
        ["recibira una copia de confirmacion"] = "will receive a confirmation copy"
    };

    private readonly PdfPigLayoutExtractor _pdfExtractor = new();
    private readonly bool _allowDevelopmentSimulation;
    private readonly IDocumentLanguageDetector _languageDetector;
    private readonly IPdfBatchTranslationClient _pdfTranslation;

    public AzureDocumentTranslationService(
        IDocumentLanguageDetector languageDetector, IPdfBatchTranslationClient pdfTranslation,
        bool allowDevelopmentSimulation = false)
    {
        _languageDetector = languageDetector ?? throw new ArgumentNullException(nameof(languageDetector));
        _pdfTranslation = pdfTranslation ?? throw new ArgumentNullException(nameof(pdfTranslation));
        _allowDevelopmentSimulation = allowDevelopmentSimulation;
    }

    public async Task<DocumentSliceTranslationResult> TranslatePdfSliceAsync(
        byte[]? sourcePdfBytes,
        string sliceFileName,
        string fallbackBodyText,
        IReadOnlyList<string> footers,
        string targetLanguage,
        string? azureTranslatorEndpoint,
        IReadOnlyList<string> doNotTranslateTerms,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        if (String.IsNullOrWhiteSpace(azureTranslatorEndpoint))
        {
            if (!_allowDevelopmentSimulation)
            {
                throw new DocumentTranslationFailureException("TranslationNotConfigured",
                    "Document translation requires a configured provider; original source was retained.");
            }
            var detectedLang = DetectLanguageLocally(fallbackBodyText);
            if (String.Equals(detectedLang, targetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return new DocumentSliceTranslationResult(
                    detectedLang, false, fallbackBodyText, null, IsDevelopmentSimulation: true);
            }
            var simulatedText = TranslateBodyTextLocally(
                fallbackBodyText, detectedLang, targetLanguage, doNotTranslateTerms);
            var footerLabel = footers.FirstOrDefault() ?? $"Development-{targetLanguage}";
            return new DocumentSliceTranslationResult(detectedLang, true, simulatedText,
                PdfPigLayoutExtractor.CreateSamplePdf([(simulatedText, footerLabel)]), IsDevelopmentSimulation: true);
        }
        if (!Uri.TryCreate(azureTranslatorEndpoint, UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme != Uri.UriSchemeHttps || !String.IsNullOrEmpty(endpointUri.UserInfo))
        {
            throw new DocumentTranslationFailureException("TranslationConfigurationError",
                "The translation provider endpoint must use HTTPS; no provider was contacted.");
        }
        if (sourcePdfBytes is not null &&
            (sourcePdfBytes.Length is < 5 or > 25 * 1024 * 1024 || !sourcePdfBytes.AsSpan().StartsWith("%PDF-"u8)))
            throw new InvalidDataException("Translation requires a genuine source PDF of at most 25 MiB.");
        if (doNotTranslateTerms is null)
            throw new DocumentTranslationFailureException("TranslationConfigurationError",
                "Protected translation terms are invalid; original source was retained.");
        var cleanTerms = doNotTranslateTerms.Where(t => !String.IsNullOrWhiteSpace(t)).Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (cleanTerms.Any(t => t.IndexOfAny(['\t', '\r', '\n']) >= 0))
        {
            throw new DocumentTranslationFailureException("TranslationConfigurationError",
                "Protected translation terms must not contain TSV control characters.");
        }
        var sourceLanguage = "und";
        try
        {
            var detection = await _languageDetector.DetectAsync(fallbackBodyText, cancellationToken);
            sourceLanguage = detection.Language;
            if (String.IsNullOrWhiteSpace(sourceLanguage) ||
                sourceLanguage.Equals("und", StringComparison.OrdinalIgnoreCase))
                throw new DocumentTranslationFailureException("LanguageUndetermined",
                    "Source language is uncertain; translation was not attempted.", "und");
            if (sourceLanguage.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase))
                return new(sourceLanguage, false, fallbackBodyText);
            if (sourcePdfBytes is null)
                throw new DocumentTranslationFailureException("TranslationSourceUnavailable",
                    "Translation requires the actual source PDF slice; original text remains available.", sourceLanguage);
            var bytes = await _pdfTranslation.TranslateAsync(
                sourcePdfBytes, sourceLanguage, targetLanguage, cleanTerms, cancellationToken);
            if (bytes.Length < 5 || !bytes.AsSpan().StartsWith("%PDF-"u8))
                throw new InvalidDataException("The translation provider returned an invalid PDF.");
            var pages = await _pdfExtractor.ExtractPagesAsync(bytes, new SplittingConfig(), cancellationToken);
            return new(sourceLanguage, true, String.Join("\n\n", pages.Select(page => page.BodyText)), bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DocumentTranslationFailureException failure)
        {
            throw new DocumentTranslationFailureException(failure.Code, failure.Message,
                failure.Language ?? sourceLanguage, failure.ProviderStatus, failure);
        }
        catch (Exception exception) when (exception is RequestFailedException or HttpRequestException or
            AuthenticationFailedException or JsonException or InvalidDataException or IOException or FormatException or
            PdfDocumentFormatException or
            TimeoutException or OperationCanceledException)
        {
            var status = exception is RequestFailedException azureFailure ? azureFailure.Status :
                exception is HttpRequestException httpFailure && httpFailure.StatusCode is { } code ? (int?)code : null;
            var error = status is 401 or 403 ? "TranslationAccessDenied" :
                exception is TimeoutException or OperationCanceledException ? "TranslationTimedOut" :
                exception is JsonException or InvalidDataException or FormatException or PdfDocumentFormatException
                    ? "TranslationInvalidResponse" : "TranslationProviderFailed";
            throw new DocumentTranslationFailureException(error,
                "Translation could not complete; original source was retained and independent features can continue.",
                sourceLanguage, status, exception);
        }
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

        if (!String.IsNullOrWhiteSpace(azureTranslatorEndpoint) || !_allowDevelopmentSimulation)
            throw new NotSupportedException("Production PDF translation requires the actual accepted source PDF.");
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        var detected = DetectLanguageLocally(text);
        var translated = !detected.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase);
        return (detected, translated, translated
            ? TranslateBodyTextLocally(text, detected, targetLanguage, doNotTranslateTerms) : text);
    }

    private static string TranslateBodyTextLocally(
        string text,
        string detectedLang,
        string targetLanguage,
        IReadOnlyList<string> doNotTranslateTerms)
    {
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
            output = $"[Document Translated {detectedLang} -> {targetLanguage}]\n{text}";
        }

        return output;
    }

    private static string DetectLanguageLocally(string text)
    {
        var spanishMatches = SpanishIndicators.Count(ind => text.Contains(ind, StringComparison.OrdinalIgnoreCase));
        return spanishMatches >= 2 ? "es" : "en";
    }
}

/// <summary>
/// Feature 3 Executor: Translates non-target-language PDF slices into translated PDF documents
/// using <see cref="IDocumentTranslationService"/> with explicit optional-feature failures.
/// </summary>
[SendsMessage(typeof(SplittingState))]
public sealed class TranslateDocumentsExecutor : Executor<SplittingState, SplittingState>
{
    private readonly IDocumentTranslationService _translationService;
    private readonly string? _defaultTranslatorEndpoint;
    private readonly IArtifactStore? _artifactStore;
    private readonly ILogger _logger;

    public TranslateDocumentsExecutor(
        IDocumentTranslationService translationService,
        string? defaultTranslatorEndpoint = null,
        IArtifactStore? artifactStore = null, ILogger? logger = null)
        : base(nameof(TranslateDocumentsExecutor))
    {
        _translationService = translationService ?? throw new ArgumentNullException(nameof(translationService));
        _defaultTranslatorEndpoint = defaultTranslatorEndpoint;
        _artifactStore = artifactStore;
        _logger = logger ?? NullLogger.Instance;
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
                Notes: "Skipped (Document Translation is disabled for this workspace)."));
            return message;
        }

        var sw = Stopwatch.StartNew();
        var config = feature.DocumentTranslation ?? new DocumentTranslationConfig();
        var targetLang = string.IsNullOrWhiteSpace(config.TargetLanguage) ? "en" : config.TargetLanguage.Trim();
        if (!String.IsNullOrWhiteSpace(config.AzureTranslatorEndpoint) &&
            (!Uri.TryCreate(config.AzureTranslatorEndpoint, UriKind.Absolute, out var configured) ||
             !Uri.TryCreate(_defaultTranslatorEndpoint, UriKind.Absolute, out var trusted) ||
             !String.IsNullOrEmpty(configured.UserInfo) || !configured.Equals(trusted)))
        {
            foreach (var slice in message.Slices)
            {
                slice.TargetLanguage = targetLang;
                FailSlice(slice, "TranslationConfigurationError",
                    "The workspace translation endpoint differs from the server-configured provider.", "und");
            }
            message.ExecutionTrace.Add(new(nameof(TranslateDocumentsExecutor), true, false, null, sw.Elapsed.TotalMilliseconds,
                "Translation configuration was rejected. Original source was retained; independent features continue."));
            _logger.LogWarning("Translation endpoint configuration was rejected without contacting the provider.");
            return message;
        }
        var endpoint = !string.IsNullOrWhiteSpace(config.AzureTranslatorEndpoint)
            ? config.AzureTranslatorEndpoint
            : _defaultTranslatorEndpoint;
        var translatedCount = 0;
        var simulationUsed = false;
        var failures = 0;

        foreach (var slice in message.Slices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            slice.TargetLanguage = targetLang;

            var sliceFileName = $"slice-{slice.SegmentIndex + 1}-pages-{slice.StartPage}-{slice.EndPage}.pdf";
            DocumentSliceTranslationResult docResult;
            try
            {
                docResult = await _translationService.TranslatePdfSliceAsync(
                    slice.SplitPdfBytes, sliceFileName, slice.BodyText, slice.Footers, targetLang, endpoint,
                    config.DoNotTranslateTerms, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (DocumentTranslationFailureException failure)
            {
                cancellationToken.ThrowIfCancellationRequested();
                failures++;
                FailSlice(slice, failure.Code, failure.Message, failure.Language);
                _logger.LogWarning("Translation failed for slice {SliceIndex}; code {Code}; HTTP {Status}. Original source retained.",
                    slice.SegmentIndex, failure.Code, failure.ProviderStatus);
                continue;
            }

            string? translatedLocation = null;
            if (docResult.WasTranslated)
            {
                if (docResult.TranslatedPdfBytes is not { Length: > 0 } translated ||
                    !translated.AsSpan().StartsWith("%PDF-"u8))
                {
                    failures++;
                    FailSlice(slice, "TranslationInvalidResponse",
                        "A translated PDF was not returned; original source was retained.", docResult.DetectedLanguage);
                    continue;
                }
                try
                {
                    if (_artifactStore is not null)
                        translatedLocation = await _artifactStore.SaveSlicePdfAsync(message.Tenant.TenantId,
                            message.DocumentId, slice.SegmentIndex, translated, cancellationToken, isTranslated: true);
                }
                catch (Exception exception) when (exception is RequestFailedException or IOException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    failures++;
                    FailSlice(slice, "TranslationStorageFailed",
                        "The translated PDF could not be stored; original source was retained.", docResult.DetectedLanguage);
                    _logger.LogWarning("A translated artifact could not be stored for slice {SliceIndex}; error type {ErrorType}.",
                        slice.SegmentIndex, exception.GetType().Name);
                    continue;
                }
                translatedCount++;
            }
            slice.TranslationErrorCode = null;
            slice.TranslationErrorMessage = null;
            slice.DetectedLanguage = docResult.DetectedLanguage;
            simulationUsed |= docResult.IsDevelopmentSimulation;
            message.HasDevelopmentResults |= docResult.IsDevelopmentSimulation;
            slice.WasTranslated = docResult.WasTranslated;
            slice.TranslatedText = docResult.WasTranslated ? docResult.TranslatedText : slice.BodyText;
            slice.TranslatedPdfBytes = docResult.WasTranslated ? docResult.TranslatedPdfBytes : null;
            slice.TranslatedPdfSizeBytes = slice.TranslatedPdfBytes?.Length ?? 0;
            slice.TranslatedPdfBlobUri = docResult.WasTranslated ? translatedLocation : null;
        }

        sw.Stop();
        message.ExecutionTrace.Add(new WorkflowStepTrace(
            nameof(TranslateDocumentsExecutor),
            Executed: true,
            Succeeded: failures == 0,
            ModelIdUsed: simulationUsed ? "DevelopmentTranslationSimulation" :
                translatedCount > 0 ? "AzureDocumentTranslation" : "AzureTranslatorLanguageDetection",
            DurationMs: sw.Elapsed.TotalMilliseconds,
            Notes: failures > 0
                ? $"Translation failed for {failures} of {message.Slices.Count} document(s). Original source was retained; independent features continue."
                : $"Detected source languages for {message.Slices.Count} document(s); translated {translatedCount}; same-language sources were retained."));

        return message;
    }

    private static void FailSlice(DocumentSlice slice, string code, string message, string? language)
    {
        slice.DetectedLanguage = language ?? "und";
        slice.WasTranslated = false;
        slice.TranslatedText = null;
        slice.TranslatedPdfBytes = null;
        slice.TranslatedPdfBlobUri = null;
        slice.TranslatedPdfSizeBytes = 0;
        slice.TranslationErrorCode = code;
        slice.TranslationErrorMessage = message;
    }
}
