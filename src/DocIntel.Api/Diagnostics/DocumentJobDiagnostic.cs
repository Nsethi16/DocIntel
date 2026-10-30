using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using DocIntel.Core;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.DocumentTranslation;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Storage;

namespace DocIntel.Api.Diagnostics;

/// <summary>Inspects opaque document references or explicitly probes one failed translation without saving results.</summary>
public static class DocumentJobDiagnostic
{
    private static readonly HashSet<string> Stages =
    [
        "ExtractPageLayoutsExecutor", "DetectSliceBoundariesExecutor", "WritePdfSlicesExecutor",
        "IdentifyDocumentsExecutor", "TranslateDocumentsExecutor", "GenerateDocumentInsightsExecutor",
        "ExtractEntitiesExecutor", "ExtractDocumentDataExecutor", "EvaluatePromptColumnsExecutor",
        "GroundExtractedValuesExecutor"
    ];

    /// <summary>No document text, filenames, original errors, identities or translated output are printed.</summary>
    public static async Task<int> ExecuteAsync(
        string tenantId, string reference, bool probe, bool liveProbingEnabled, IJobStore jobs,
        ITenantAccessResolver access, IArtifactStore artifacts, IPdfSlicer slicer,
        IDocumentTranslationService translation, AzureIntegrationOptions azure, TextWriter output,
        CancellationToken cancellationToken = default)
    {
        var exactId = Guid.TryParseExact(reference, "N", out _);
        if (String.IsNullOrEmpty(tenantId) || !Regex.IsMatch(tenantId, "^[a-z0-9][a-z0-9-]{0,127}$") ||
            !exactId && !Regex.IsMatch(reference, "^[a-fA-F0-9]{64}$"))
            throw new ArgumentException("Choose a workspace and an opaque document ID or exact-filename SHA-256.");
        var phase = probe ? "TranslationProbe" : "Document";
        if (probe && (!exactId || !liveProbingEnabled))
        {
            await WriteAsync(output, phase, "Refused", "ExplicitDocumentAndLiveProbeRequired");
            return 2;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var stage = "Lookup";
        DocumentJob? selected = null;
        int? sliceIndex = null;
        try
        {
            IReadOnlyList<DocumentJob> matches;
            if (exactId)
            {
                var job = await jobs.GetJobAsync(reference, deadline.Token, tenantId);
                matches = job is null ? [] : [job];
            }
            else
            {
                matches = (await jobs.ListJobsByTenantAsync(tenantId, deadline.Token))
                    .Where(job => String.Equals(reference,
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job.FileName))),
                        StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(job => job.CreatedAtUtc).Take(5).ToArray();
            }
            if (matches.Count == 0)
            {
                await WriteAsync(output, phase, "NotFound", "DocumentUnavailable");
                return 2;
            }
            if (matches.Any(job => job.TenantId != tenantId || !Guid.TryParseExact(job.JobId, "N", out _)))
                throw new InvalidDataException("A diagnostic document does not match the selected workspace.");
            foreach (var job in matches) await WriteAsync(output, "Document", job.Status.ToString(), "Stored", job);
            if (!probe) return 0;
            selected = matches[0];
            stage = "Access";
            if (selected.SubmittedBy is not { } actor) throw new UnauthorizedAccessException();
            var scope = await access.ResolveAsync(actor, tenantId, deadline.Token);
            if (!scope.CanProcess || !scope.CanReadDocument(selected.JobId))
                throw new UnauthorizedAccessException();
            var harness = selected.ProcessingHarness;
            if (selected.Status != JobStatus.Failed || harness is null || harness.TenantId != tenantId ||
                harness.HarnessVersion != selected.AppliedHarnessVersion ||
                !selected.ExecutionTrace.Any(step => step.ExecutorName == "IdentifyDocumentsExecutor" && step.Succeeded) ||
                selected.ExecutionTrace.Any(step => step.ExecutorName == "TranslateDocumentsExecutor" && step.Succeeded) ||
                !harness.Capabilities.TryGetValue(FeatureHarness.DocumentTranslationKey, out var feature) || !feature.Enabled)
            {
                await WriteAsync(output, phase, "Refused", "TranslationStageNotEligible", selected);
                return 2;
            }
            var config = feature.DocumentTranslation ?? new();
            var endpoint = azure.ResolveEffectiveTranslatorEndpoint();
            if (!String.IsNullOrWhiteSpace(config.AzureTranslatorEndpoint) &&
                (!Uri.TryCreate(config.AzureTranslatorEndpoint, UriKind.Absolute, out var configured) ||
                 !Uri.TryCreate(endpoint, UriKind.Absolute, out var trusted) || !configured.Equals(trusted)))
                throw new InvalidOperationException("The accepted translation endpoint differs from the configured provider.");
            foreach (var slice in selected.Segments.OrderBy(slice => slice.SegmentIndex).Take(48))
            {
                sliceIndex = slice.SegmentIndex;
                stage = "ReadSource";
                if (await artifacts.GetSlicePdfAsync(tenantId, selected.ArtifactDocumentId ?? selected.JobId,
                    slice.SegmentIndex, deadline.Token, isTranslated: true) is { Length: > 0 }) continue;
                var bytes = await DocumentPdfReader.ReadSliceAsync(
                    selected, slice.SegmentIndex, false, artifacts, slicer, deadline.Token)
                    ?? throw new InvalidDataException("The active source slice is unavailable.");
                stage = "Translate";
                var result = await translation.TranslatePdfSliceAsync(bytes,
                    $"slice-{slice.SegmentIndex + 1}-pages-{slice.StartPage}-{slice.EndPage}.pdf",
                    slice.BodyText, slice.Footers, String.IsNullOrWhiteSpace(config.TargetLanguage) ? "en" : config.TargetLanguage.Trim(),
                    endpoint, config.DoNotTranslateTerms, deadline.Token);
                if (result.IsDevelopmentSimulation)
                    throw new InvalidOperationException("A simulated translation cannot verify the production provider.");
                await WriteAsync(output, phase, "Completed", "None", selected, stage, sliceIndex);
                return 0;
            }
            await WriteAsync(output, phase, "Refused", "NoUntranslatedSliceWithinProbeLimit", selected, stage);
            return 2;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await WriteAsync(output, phase, "Failed", "TimedOut", selected, stage, sliceIndex);
            return 1;
        }
        catch (UnauthorizedAccessException)
        {
            await WriteAsync(output, phase, "Refused", "AccessRevoked", selected, stage, sliceIndex);
            return 2;
        }
        catch (Exception exception)
        {
            var failure = exception as RequestFailedException;
            var code = failure?.ErrorCode;
            var safeCode = code is not null && (Regex.IsMatch(code, "^[0-9]{6}$") ||
                code is "InvalidRequest" or "InvalidArgument" or "UnsupportedLanguage" or "UnsupportedSourceLanguage" or
                "UnsupportedTargetLanguage" or "UnsupportedDocumentFormat" or "InvalidDocumentFormat" or "InvalidGlossary" or
                "InvalidRequestContent" or "DocumentTooLarge" or "AuthenticationFailed" or "Unauthorized" or "Forbidden" or
                "AccessDenied" or "QuotaExceeded" or "TooManyRequests" or "ResourceNotFound" or "InternalServerError" or
                "AuthorizationPermissionMismatch" or "AuthenticationTypeDisabled")
                ? code : failure is null ? null : "OtherProviderCode";
            var message = failure is null ? String.Empty : failure.Message[..Math.Min(failure.Message.Length, 16_384)];
            var terms = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["glossary"] = "Glossary", ["source language"] = "SourceLanguage",
                ["target language"] = "TargetLanguage", ["same language"] = "SameLanguage",
                ["unsupported"] = "Unsupported", ["format"] = "Format", ["size"] = "Size",
                ["password"] = "Password", ["authorization"] = "Access", ["rate limit"] = "RateLimit",
                ["contains no text"] = "NoText"
            };
            var hints = terms.Where(term => message.Contains(term.Key, StringComparison.OrdinalIgnoreCase))
                .Select(term => term.Value).ToArray();
            await WriteAsync(output, phase, "Failed", failure is null ? "DiagnosticFailed" : "ProviderRequestFailed",
                selected, stage, sliceIndex, failure?.Status, safeCode, hints);
            return 1;
        }
    }

    private static Task WriteAsync(TextWriter output, string phase, string status, string failureCode,
        DocumentJob? job = null, string? stage = null, int? sliceIndex = null, int? httpStatus = null,
        string? providerCode = null, IReadOnlyList<string>? providerHints = null) =>
        output.WriteLineAsync("DOCINTEL_DIAGNOSTIC " + JsonSerializer.Serialize(new
        {
            phase, status, failureCode, stage, sliceIndex, httpStatus, providerCode, providerHints,
            jobId = job?.JobId, createdAtUtc = job?.CreatedAtUtc, completedAtUtc = job?.CompletedAtUtc,
            pages = job?.TotalPages, slices = job?.Segments.Count,
            lastStep = job?.ExecutionTrace.LastOrDefault()?.ExecutorName is { } name && Stages.Contains(name) ? name : null,
            decisionCompleted = job?.ExecutionTrace.Any(step => step.ExecutorName == "IdentifyDocumentsExecutor" &&
                step.Executed && step.Succeeded && step.ModelIdUsed == "decision-1"),
            identifiedSlices = job?.Segments.Count(slice => !String.IsNullOrWhiteSpace(slice.IdentifiedDocumentName)),
            translatedSlices = job?.Segments.Count(slice => slice.WasTranslated)
        }));
}
