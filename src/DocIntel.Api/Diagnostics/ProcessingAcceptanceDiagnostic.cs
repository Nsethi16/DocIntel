using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocIntel.Core;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.DocumentTranslation;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using DocIntel.Core.Ocr;
using DocIntel.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocIntel.Api.Diagnostics;

/// <summary>Explicit operator acceptance checks; generated/customer content is never printed.</summary>
public static class ProcessingAcceptanceDiagnostic
{
    public static async Task<int> ReprocessAsync(string tenantId, string jobId, bool live,
        IJobStore jobs, IJobQueue queue, ITenantAccessResolver access, IArtifactStore artifacts,
        TextWriter output, CancellationToken cancellationToken = default)
    {
        Validate(tenantId);
        if (!Guid.TryParseExact(jobId, "N", out _)) throw new ArgumentException("Choose an opaque document ID.");
        if (!live)
        {
            await DocumentJobDiagnostic.WriteAsync(output, "DocumentReprocess", "Refused", "LiveProbingDisabled");
            return 2;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(300));
        DocumentJob? source = null;
        var queued = false;
        try
        {
            source = await jobs.GetJobAsync(jobId, deadline.Token, tenantId);
            if (source is null || source.TenantId != tenantId || source.JobId != jobId ||
                source.Status != JobStatus.Failed || source.ProcessingHarness is null ||
                source.ProcessingHarness.TenantId != tenantId ||
                source.ProcessingHarness.HarnessVersion != source.AppliedHarnessVersion ||
                source.OriginalSha256 is not { Length: 64 } ||
                !source.OriginalSha256.All(Char.IsAsciiHexDigit) || source.SubmittedBy is not { } actor ||
                source.ExecutionAttempts >= 3)
            {
                await DocumentJobDiagnostic.WriteAsync(output, "DocumentReprocess", "Refused", "RetryNotEligible", source);
                return 2;
            }
            var scope = await access.ResolveAsync(actor, tenantId, deadline.Token);
            if (!scope.CanProcess || !scope.CanReadDocument(jobId)) throw new UnauthorizedAccessException();
            if (await DocumentPdfReader.ReadOriginalAsync(source, artifacts, deadline.Token) is null)
                throw new InvalidDataException("Verified source is unavailable.");
            var previousAttempt = source.ExecutionAttempts;
            var acceptedHash = source.OriginalSha256;
            scope = await access.ResolveAsync(actor, tenantId, deadline.Token);
            if (!scope.CanProcess || !scope.CanReadDocument(jobId)) throw new UnauthorizedAccessException();
            var retry = JsonSerializer.Deserialize<DocumentJob>(JsonSerializer.SerializeToUtf8Bytes(source))
                ?? throw new InvalidDataException("The selected document is unavailable.");
            retry.StoreVersion = source.StoreVersion;
            retry.Status = JobStatus.Queued;
            retry.ErrorMessage = null;
            retry.CompletedAtUtc = null;
            await jobs.UpsertJobAsync(retry, deadline.Token);
            queued = true;
            await queue.TryEnqueueAsync(new(jobId, tenantId), deadline.Token);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                source = await jobs.GetJobAsync(jobId, deadline.Token, tenantId)
                    ?? throw new InvalidDataException("The selected document is unavailable.");
                if (source.TenantId != tenantId || source.JobId != jobId || source.SubmittedBy != actor ||
                    source.OriginalSha256 != acceptedHash)
                    throw new InvalidDataException("The selected document changed unexpectedly.");
                scope = await access.ResolveAsync(actor, tenantId, deadline.Token);
                if (!scope.CanProcess || !scope.CanReadDocument(jobId)) throw new UnauthorizedAccessException();
                if (source.ExecutionAttempts > previousAttempt && source.Status is JobStatus.Completed or JobStatus.Failed)
                {
                    await DocumentJobDiagnostic.WriteAsync(output, "DocumentReprocess", source.Status.ToString(),
                        source.Status == JobStatus.Completed ? "None" : "ProcessingFailed", source);
                    return source.Status == JobStatus.Completed ? 0 : 1;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await DocumentJobDiagnostic.WriteAsync(output, "DocumentReprocess",
                queued ? "Pending" : "Refused", queued ? "VerificationPending" : "TimedOut", source);
            return 3;
        }
        catch (UnauthorizedAccessException)
        {
            await DocumentJobDiagnostic.WriteAsync(output, "DocumentReprocess", "Refused", "AccessRevoked", source);
            return 2;
        }
        catch (Exception)
        {
            await DocumentJobDiagnostic.WriteAsync(output, "DocumentReprocess",
                queued ? "Pending" : "Failed", queued ? "VerificationPending" : "DiagnosticFailed", source);
            return 1;
        }
    }

    public static async Task<int> TranslationAsync(string tenantId, bool live, IDocumentTranslationService translation,
        IDocumentLanguageDetector language, AzureIntegrationOptions azure, TextWriter output,
        CancellationToken cancellationToken = default)
    {
        Validate(tenantId);
        if (!live)
        {
            await DocumentJobDiagnostic.WriteAsync(output, "TranslationSynthetic", "Refused", "LiveProbingDisabled");
            return 2;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(240));
        try
        {
            var source = PdfPigLayoutExtractor.CreateSamplePdf(
            [("Devis de demonstration\nClient: NORTHWIND-DIAGNOSTIC\nNumero: SYN-FR-001\n" +
                "La livraison des services est prevue pour le mois prochain. Le montant total est de mille euros. " +
                "Ce document fictif sert uniquement a verifier la traduction et ne contient aucune donnee personnelle.",
                "Diagnostic-FR-001")]);
            var extractor = new PdfPigLayoutExtractor();
            var pages = await extractor.ExtractPagesAsync(source, new(), deadline.Token);
            var result = await translation.TranslatePdfSliceAsync(source, "synthetic.pdf",
                String.Join("\n", pages.Select(page => page.BodyText)), [], "en",
                azure.ResolveEffectiveTranslatorEndpoint(), ["NORTHWIND-DIAGNOSTIC", "SYN-FR-001"], deadline.Token);
            var translated = result.TranslatedPdfBytes;
            var valid = !result.IsDevelopmentSimulation && result.WasTranslated && result.DetectedLanguage == "fr" &&
                translated is { Length: > 5 } && translated.AsSpan().StartsWith("%PDF-"u8) &&
                !SHA256.HashData(source).SequenceEqual(SHA256.HashData(translated)) &&
                (await extractor.ExtractPagesAsync(translated, new(), deadline.Token)).Count == pages.Count &&
                result.TranslatedText.Contains("NORTHWIND-DIAGNOSTIC", StringComparison.Ordinal) &&
                result.TranslatedText.Contains("SYN-FR-001", StringComparison.Ordinal) &&
                (await language.DetectAsync(result.TranslatedText, deadline.Token)).Language == "en";
            await DocumentJobDiagnostic.WriteAsync(output, "TranslationSynthetic", valid ? "Completed" : "Failed",
                valid ? "None" : "SyntheticResultMismatch");
            return valid ? 0 : 1;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await DocumentJobDiagnostic.WriteAsync(output, "TranslationSynthetic", "Failed", "TimedOut");
            return 1;
        }
        catch (DocumentTranslationFailureException failure)
        {
            await DocumentJobDiagnostic.WriteAsync(output, "TranslationSynthetic", "Failed", failure.Code,
                httpStatus: failure.ProviderStatus);
            return 1;
        }
        catch (Exception)
        {
            await DocumentJobDiagnostic.WriteAsync(output, "TranslationSynthetic", "Failed", "DiagnosticFailed");
            return 1;
        }
    }

    public static async Task<int> PartialAsync(string tenantId, bool live, IHarnessStore harnesses,
        IModelCatalog models, TextWriter output, CancellationToken cancellationToken = default)
    {
        Validate(tenantId);
        if (!live)
        {
            await DocumentJobDiagnostic.WriteAsync(output, "ProcessingSynthetic", "Refused", "LiveProbingDisabled");
            return 2;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(300));
        try
        {
            var original = await harnesses.GetTenantAsync(tenantId, deadline.Token)
                ?? throw new InvalidDataException("Workspace unavailable.");
            if (!original.IsActive) throw new InvalidDataException("Workspace inactive.");
            var model = models.ResolveModelId(original);
            var tenant = new TenantHarness
            {
                TenantId = tenantId, DisplayName = "Synthetic processing", DefaultModelId = model,
                DecisionModelId = original.DecisionModelId, AllowedModels = [model, original.DecisionModelId],
                Ocr = new() { Mode = OcrMode.Never },
                Capabilities = new(StringComparer.OrdinalIgnoreCase)
                {
                    [FeatureHarness.SplitAndGroupKey] = new() { Enabled = true, SplitAndGroup = new() },
                    [FeatureHarness.IdentifyDocumentsKey] = new()
                    {
                        Enabled = true, IdentifyDocuments = new() { Rules =
                            [new() { DocumentName = "Quote", IdentificationPrompt = "Quote heading and quote number identify a sales quotation." }] }
                    },
                    [FeatureHarness.DocumentTranslationKey] = new() { Enabled = true, DocumentTranslation = new() },
                    [FeatureHarness.DocumentInsightKey] = new() { Enabled = true, ModelIdOverride = model, DocumentInsight = new() },
                    [FeatureHarness.EntityExtractionKey] = new() { Enabled = true, ModelIdOverride = model, EntityExtraction = new() },
                    [FeatureHarness.DataExtractionKey] = new()
                    {
                        Enabled = true, ModelIdOverride = model, DataExtraction = new() { Profiles =
                            [new() { DocumentName = "Quote", Fields =
                                [new() { FieldName = "QuoteNumber", Instruction = "Extract the source quote number." }] }] }
                    },
                    [FeatureHarness.PromptColumnsKey] = new()
                    {
                        Enabled = true, ModelIdOverride = model, PromptColumns = new() { Columns =
                            [new() { ColumnId = "synthetic-summary", ColumnName = "Summary",
                                PromptInstructions = "Summarize the source quote and cite its customer and total." }] }
                    }
                }
            };
            var extractor = new PdfPigLayoutExtractor();
            var source = PdfPigLayoutExtractor.CreateSamplePdf(
                [("Quote\nQuote Number: SYN-001\nCustomer Name: Northwind Example\nBilling Currency: USD\n" +
                    "Total deal value: 1000.00 USD\nThis authored synthetic record is not a customer transaction.", "Form-Quote-v1")]);
            var state = await new DocumentSplittingWorkflow(extractor, extractor, models,
                new InjectedTranslationFailure(), NullLogger<DocumentSplittingWorkflow>.Instance)
                .ExecuteAsync(new() { Tenant = tenant, FileName = "synthetic.pdf", SourcePdfBytes = source }, cancellationToken: deadline.Token);
            if (state.HasDevelopmentResults)
            {
                await DocumentJobDiagnostic.WriteAsync(output, "ProcessingSynthetic", "Failed", "SimulationNotPermitted");
                return 1;
            }
            var job = new DocumentJob
            {
                JobId = Guid.NewGuid().ToString("N"), TenantId = tenantId, TenantDisplayName = "Synthetic",
                AppliedHarnessVersion = 1, FileName = "synthetic.pdf", Status = JobStatus.Completed,
                TotalPages = state.Pages.Count, Segments = state.Slices, ExecutionTrace = state.ExecutionTrace,
                PacketSummary = state.PacketSummary, ExtractedEntities = state.ExtractedEntities,
                DataExtractions = state.DataExtractions, PromptColumnResults = state.PromptColumnResults
            };
            var valid = job.HasWarnings && job.Segments.Count == 1 &&
                job.ExecutionTrace.Single(step => step.ExecutorName == "TranslateDocumentsExecutor").Succeeded == false &&
                job.ExecutionTrace.Where(step => step.ExecutorName != "TranslateDocumentsExecutor").All(step => step.Succeeded) &&
                (!String.IsNullOrWhiteSpace(job.PacketSummary) || job.Segments.Any(slice => !String.IsNullOrWhiteSpace(slice.InsightSummary))) &&
                job.ExtractedEntities.Count > 0 && job.DataExtractions.Count > 0 && job.PromptColumnResults.Count > 0;
            await DocumentJobDiagnostic.WriteAsync(output, "ProcessingSynthetic", valid ? "Completed" : "Failed",
                valid ? "None" : "SyntheticResultMismatch", job);
            return valid ? 0 : 1;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await DocumentJobDiagnostic.WriteAsync(output, "ProcessingSynthetic", "Failed", "TimedOut");
            return 1;
        }
        catch (Exception)
        {
            await DocumentJobDiagnostic.WriteAsync(output, "ProcessingSynthetic", "Failed", "DiagnosticFailed");
            return 1;
        }
    }

    private static void Validate(string tenantId)
    {
        if (!Regex.IsMatch(tenantId, "^[a-z0-9][a-z0-9-]{0,127}$"))
            throw new ArgumentException("Choose a valid application workspace.");
    }

    private sealed class InjectedTranslationFailure : IDocumentTranslationService
    {
        public Task<DocumentSliceTranslationResult> TranslatePdfSliceAsync(byte[]? sourcePdfBytes, string sliceFileName,
            string fallbackBodyText, IReadOnlyList<string> footers, string targetLanguage, string? azureTranslatorEndpoint,
            IReadOnlyList<string> doNotTranslateTerms, CancellationToken cancellationToken = default) =>
            throw new DocumentTranslationFailureException("InjectedTranslationFailure",
                "Synthetic translation failure; original source remains available.", "en", 503);

        public Task<(string DetectedLanguage, bool WasTranslated, string TranslatedText)> TranslateSliceAsync(string text,
            string targetLanguage, string? azureTranslatorEndpoint, IReadOnlyList<string> doNotTranslateTerms,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
