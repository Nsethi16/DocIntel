using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace DocIntel.Api.Endpoints;

public static class HarnessEndpoints
{
    public static IEndpointRouteBuilder MapHarnessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/harness");

        group.MapGet("/catalog", (IModelCatalog modelCatalog, IHarnessStore store) =>
        {
            var storageLocation = (store as FileHarnessStore)?.StorageFilePath ?? "In-Memory";
            return TypedResults.Ok(new
            {
                models = modelCatalog.ListModels().Select(m => m.ModelId).ToArray(),
                modelDescriptors = modelCatalog.ListModels(),
                storageLocation,
                capabilities = new object[]
                {
                    new
                    {
                        name = FeatureHarness.SplitAndGroupKey,
                        description = "Identify and break each document in a multi-document file deterministically.",
                        defaultSystemPrompt = DocumentSplittingWorkflow.DefaultSystemPrompt
                    },
                    new
                    {
                        name = FeatureHarness.IdentifyDocumentsKey,
                        description = "Classify PDF slices into admin-defined document names or Undefined using a Microsoft Decision Model."
                    },
                    new
                    {
                        name = FeatureHarness.DocumentTranslationKey,
                        description = "Translate non-target language document slices using Azure Document Translation."
                    },
                    new
                    {
                        name = FeatureHarness.DocumentInsightKey,
                        description = "Generate executive summaries guided by custom admin summary instructions."
                    },
                    new
                    {
                        name = FeatureHarness.EntityExtractionKey,
                        description = "Extract structured entities guided by custom admin extraction prompts."
                    },
                    new
                    {
                        name = FeatureHarness.DataExtractionKey,
                        description = "Extract structured key-value fields and tables with columns at the defined document level."
                    }
                }
            });
        });

        group.MapGet("/tenants", async (IHarnessStore store, CancellationToken ct) =>
        {
            var tenants = await store.ListTenantsAsync(ct);
            return TypedResults.Ok(tenants);
        });

        group.MapGet("/tenants/{tenantId}", async (string tenantId, IHarnessStore store, CancellationToken ct) =>
        {
            var tenant = await store.GetTenantAsync(tenantId, ct);
            return tenant is not null
                ? Results.Ok(tenant)
                : Results.NotFound(new { error = $"Tenant '{tenantId}' not found." });
        });

        group.MapPut("/tenants/{tenantId}", async (
            string tenantId,
            [FromBody] TenantHarness updated,
            IHarnessStore store,
            CancellationToken ct) =>
        {
            updated.TenantId = tenantId;
            var saved = await store.UpsertTenantAsync(updated, ct);
            return TypedResults.Ok(saved);
        });

        // Async Job Submission (202 Accepted)
        group.MapPost("/tenants/{tenantId}/jobs", async (
            string tenantId,
            HttpRequest request,
            IHarnessStore harnessStore,
            IJobStore jobStore,
            IJobQueue jobQueue,
            CancellationToken ct) =>
        {
            var tenant = await harnessStore.GetTenantAsync(tenantId, ct);
            if (tenant is null)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not found." });
            }

            var (fileName, pdfBytes) = await ResolvePdfBytesAsync(request, ct);
            var jobId = Guid.NewGuid().ToString("N");

            var job = new DocumentJob
            {
                JobId = jobId,
                TenantId = tenant.TenantId,
                TenantDisplayName = tenant.DisplayName,
                AppliedHarnessVersion = tenant.HarnessVersion,
                FileName = fileName,
                Status = JobStatus.Queued
            };

            await jobStore.UpsertJobAsync(job, ct);
            await jobQueue.EnqueueAsync(new JobQueueTicket(jobId, tenant.TenantId, fileName, pdfBytes), ct);

            return Results.Accepted($"/api/harness/jobs/{jobId}", new
            {
                jobId,
                tenantId = tenant.TenantId,
                appliedHarnessVersion = tenant.HarnessVersion,
                status = job.Status.ToString(),
                statusUrl = $"/api/harness/jobs/{jobId}"
            });
        });

        // Poll Async Job Status
        group.MapGet("/jobs/{jobId}", async (string jobId, IJobStore jobStore, CancellationToken ct) =>
        {
            var job = await jobStore.GetJobAsync(jobId, ct);
            if (job is null)
            {
                return Results.NotFound(new { error = $"Job '{jobId}' not found." });
            }

            return Results.Ok(FormatJobResponse(job));
        });

        // Inline / Sync Execution
        group.MapPost("/tenants/{tenantId}/run-split", async (
            string tenantId,
            HttpRequest request,
            IHarnessStore harnessStore,
            DocumentSplittingWorkflow workflow,
            CancellationToken ct) =>
        {
            var tenant = await harnessStore.GetTenantAsync(tenantId, ct);
            if (tenant is null)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not found." });
            }

            var (fileName, pdfBytes) = await ResolvePdfBytesAsync(request, ct);

            var state = new SplittingState
            {
                Tenant = tenant,
                FileName = fileName,
                SourcePdfBytes = pdfBytes
            };

            var completed = await workflow.ExecuteAsync(state, cancellationToken: ct);

            return Results.Ok(new
            {
                tenantId = tenant.TenantId,
                tenantDisplayName = tenant.DisplayName,
                appliedHarnessVersion = tenant.HarnessVersion,
                documentId = completed.DocumentId,
                correlationId = completed.CorrelationId,
                fileName = completed.FileName,
                totalPages = completed.Pages.Count,
                strategyUsed = completed.StrategyUsed,
                modelIdUsed = completed.ModelIdUsed,
                packetSummary = completed.PacketSummary,
                extractedEntities = completed.ExtractedEntities,
                dataExtractions = completed.DataExtractions,
                executionTrace = completed.ExecutionTrace,
                pages = completed.Pages,
                segments = completed.Slices.Select(FormatSlice)
            });
        });

        return app;
    }

    private static async Task<(string FileName, byte[] PdfBytes)> ResolvePdfBytesAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.HasFormContentType && request.Form.Files.Count > 0)
        {
            var file = request.Form.Files[0];
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            return (file.FileName, ms.ToArray());
        }

        var sampleBytes = PdfPigLayoutExtractor.CreateSamplePdf(
        [
            ("Master Business Service Agreement\nThis Master Agreement governs all enterprise purchases.\nSection 1: Definitions and Scope.", "Form-MBSA-v1"),
            ("Master Business Service Agreement\nSection 2: Confidentiality and Liability Limits.\nSigned by Authorized Representative.", "Form-MBSA-v1"),
            ("Enterprise Enrollment\nEnrollment under the Master Business Service Agreement.\nIncludes 2,500 Enterprise Seats.", "Form-EA-v2"),
            ("Amendment ID M130\nCustom Pricing and Volume Discount Schedule.\nApplies to Enterprise Enrollment.", "VLAmendments,M130"),
            ("Partner Cloud Addendum\nSpecial Terms for Multi-Cloud Hybrid Workloads.\nEffective Date: October 2026.", "Addendum-Cloud-99")
        ]);

        return ("Enterprise-Contract-Packet-Sample.pdf", sampleBytes);
    }

    private static object FormatJobResponse(DocumentJob job) => new
    {
        job.JobId,
        job.TenantId,
        job.TenantDisplayName,
        job.AppliedHarnessVersion,
        job.FileName,
        status = job.Status.ToString(),
        job.ErrorMessage,
        job.TotalPages,
        job.StrategyUsed,
        job.ModelIdUsed,
        job.PacketSummary,
        job.ExtractedEntities,
        job.DataExtractions,
        job.CreatedAtUtc,
        job.CompletedAtUtc,
        job.ExecutionTrace,
        segments = job.Segments.Select(FormatSlice)
    };

    private static object FormatSlice(DocumentSlice s) => new
    {
        s.SegmentIndex,
        s.DocumentTypeName,
        s.IdentifiedDocumentName,
        s.IdentificationConfidence,
        s.MatchedIdentificationRule,
        s.IsInferred,
        s.StartPage,
        s.EndPage,
        s.Footers,
        s.IsAmendment,
        s.AmendmentId,
        s.SchemaId,
        s.DetectedLanguage,
        s.TargetLanguage,
        s.WasTranslated,
        s.TranslatedText,
        s.InsightSummary,
        s.ExtractedEntities,
        s.DataExtraction,
        s.BodyText,
        s.Markdown,
        s.SplitPdfSizeBytes,
        splitPdfBase64 = s.SplitPdfBytes is not null ? Convert.ToBase64String(s.SplitPdfBytes) : null
    };
}
