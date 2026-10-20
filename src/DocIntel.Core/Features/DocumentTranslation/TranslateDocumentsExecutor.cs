using System.Diagnostics;
using System.Text;
using Azure.AI.Translation.Document;
using Azure.Core;
using Azure.Identity;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Storage;
using Microsoft.Agents.AI.Workflows;

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
/// (<see cref="SingleDocumentTranslationClient"/> for synchronous PDF slice translation and
/// <see cref="DocumentTranslationClient"/> for batch Blob-to-Blob PDF translation),
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

    private readonly TokenCredential _credential;
    private readonly PdfPigLayoutExtractor _pdfExtractor = new();
    private readonly bool _allowDevelopmentSimulation;
    private readonly SingleDocumentTranslationClient? _client;

    public AzureDocumentTranslationService(
        TokenCredential? credential = null, bool allowDevelopmentSimulation = false,
        SingleDocumentTranslationClient? client = null)
    {
        var managedIdentityClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        _credential = credential ?? (!string.IsNullOrWhiteSpace(managedIdentityClientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId))
            : new DefaultAzureCredential());
        _allowDevelopmentSimulation = allowDevelopmentSimulation;
        _client = client;
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
                throw new InvalidOperationException("Document translation requires a configured HTTPS provider endpoint.");
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
            throw new InvalidOperationException("The document translation provider endpoint must use HTTPS.");
        }
        if (sourcePdfBytes is not { Length: > 0 })
        {
            throw new InvalidOperationException("PDF translation requires the actual source PDF bytes.");
        }
        var client = _client ?? new SingleDocumentTranslationClient(endpointUri, _credential);
        await using var pdfStream = new MemoryStream(sourcePdfBytes);
        var docFile = new MultipartFormFileData(sliceFileName, pdfStream, "application/pdf");
        var cleanTerms = doNotTranslateTerms.Where(t => !String.IsNullOrWhiteSpace(t)).Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (cleanTerms.Any(t => t.IndexOfAny(['\t', '\r', '\n']) >= 0))
        {
            throw new ArgumentException("Protected translation terms must not contain TSV control characters.");
        }
        await using var glossaryStream = new MemoryStream(Encoding.UTF8.GetBytes(
            String.Join("\n", cleanTerms.Select(t => $"{t}\t{t}"))));
        var content = cleanTerms.Count == 0 ? new DocumentTranslateContent(docFile) :
            new DocumentTranslateContent(docFile,
                [new MultipartFormFileData("donottranslate.tsv", glossaryStream, "text/tab-separated-values")]);
        var response = await client.TranslateAsync(
            targetLanguage, content, sourceLanguage: null, cancellationToken: cancellationToken);
        var bytes = response.Value.ToArray();
        if (bytes.Length == 0)
        {
            throw new InvalidDataException("The translation provider returned an empty PDF.");
        }
        var pages = await _pdfExtractor.ExtractPagesAsync(bytes, new SplittingConfig(), cancellationToken);
        return new DocumentSliceTranslationResult("und", true, String.Join("\n\n", pages.Select(p => p.BodyText)), bytes);
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

        byte[]? samplePdfBytes = null;
        if (!string.IsNullOrWhiteSpace(azureTranslatorEndpoint))
        {
            samplePdfBytes = PdfPigLayoutExtractor.CreateSamplePdf([(text, "Slice")]);
        }

        var result = await TranslatePdfSliceAsync(
            samplePdfBytes,
            "slice.pdf",
            text,
            [],
            targetLanguage,
            azureTranslatorEndpoint,
            doNotTranslateTerms,
            cancellationToken).ConfigureAwait(false);

        return (result.DetectedLanguage, result.WasTranslated, result.TranslatedText);
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
/// Feature 3 Executor: Translates non-target-language PDF slices into translated PDF documents
/// using <see cref="IDocumentTranslationService"/> (Azure AI Document Translation <see cref="SingleDocumentTranslationClient"/>).
/// </summary>
[SendsMessage(typeof(SplittingState))]
public sealed class TranslateDocumentsExecutor : Executor<SplittingState, SplittingState>
{
    private readonly IDocumentTranslationService _translationService;
    private readonly string? _defaultTranslatorEndpoint;
    private readonly IArtifactStore? _artifactStore;

    public TranslateDocumentsExecutor(
        IDocumentTranslationService translationService,
        string? defaultTranslatorEndpoint = null,
        IArtifactStore? artifactStore = null)
        : base(nameof(TranslateDocumentsExecutor))
    {
        _translationService = translationService ?? throw new ArgumentNullException(nameof(translationService));
        _defaultTranslatorEndpoint = defaultTranslatorEndpoint;
        _artifactStore = artifactStore;
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
        if (!String.IsNullOrWhiteSpace(config.AzureTranslatorEndpoint) &&
            (!Uri.TryCreate(config.AzureTranslatorEndpoint, UriKind.Absolute, out var configured) ||
             !Uri.TryCreate(_defaultTranslatorEndpoint, UriKind.Absolute, out var trusted) ||
             !String.IsNullOrEmpty(configured.UserInfo) || !configured.Equals(trusted)))
        {
            throw new InvalidOperationException("Tenant translation endpoints must match the server-configured provider.");
        }
        var targetLang = string.IsNullOrWhiteSpace(config.TargetLanguage) ? "en" : config.TargetLanguage.Trim();
        var endpoint = !string.IsNullOrWhiteSpace(config.AzureTranslatorEndpoint)
            ? config.AzureTranslatorEndpoint
            : _defaultTranslatorEndpoint;
        var translatedCount = 0;
        var simulationUsed = false;

        foreach (var slice in message.Slices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sliceFileName = $"slice-{slice.SegmentIndex + 1}-pages-{slice.StartPage}-{slice.EndPage}.pdf";
            var docResult = await _translationService.TranslatePdfSliceAsync(
                slice.SplitPdfBytes,
                sliceFileName,
                slice.BodyText,
                slice.Footers,
                targetLang,
                endpoint,
                config.DoNotTranslateTerms,
                cancellationToken);

            slice.DetectedLanguage = docResult.DetectedLanguage;
            simulationUsed |= docResult.IsDevelopmentSimulation;
            message.HasDevelopmentResults |= docResult.IsDevelopmentSimulation;
            slice.TargetLanguage = targetLang;
            slice.WasTranslated = docResult.WasTranslated;
            slice.TranslatedText = docResult.WasTranslated ? docResult.TranslatedText : slice.BodyText;

            if (docResult.WasTranslated && docResult.TranslatedPdfBytes is { Length: > 0 })
            {
                slice.TranslatedPdfBytes = docResult.TranslatedPdfBytes;
                slice.TranslatedPdfSizeBytes = docResult.TranslatedPdfBytes.Length;

                if (_artifactStore is not null)
                {
                    slice.TranslatedPdfBlobUri = await _artifactStore.SaveSlicePdfAsync(
                        message.Tenant.TenantId,
                        message.DocumentId,
                        slice.SegmentIndex,
                        docResult.TranslatedPdfBytes,
                        cancellationToken,
                        isTranslated: true);
                }

                translatedCount++;
            }
            else if (docResult.WasTranslated)
            {
                translatedCount++;
            }
        }

        sw.Stop();
        message.ExecutionTrace.Add(new WorkflowStepTrace(
            nameof(TranslateDocumentsExecutor),
            Executed: true,
            Succeeded: true,
            ModelIdUsed: simulationUsed ? "DevelopmentTranslationSimulation" : "AzureDocumentTranslation",
            DurationMs: sw.Elapsed.TotalMilliseconds,
            Notes: $"Evaluated {message.Slices.Count} PDF slice(s) for target language '{targetLang}'; translated {translatedCount} PDF document slice(s)."));

        return message;
    }
}
