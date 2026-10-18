using System.Text;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.PromptColumns;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using DocIntel.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;

namespace DocIntel.Api.Endpoints;

public static class HarnessEndpoints
{
    public static IEndpointRouteBuilder MapHarnessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/harness");

        group.MapGet("/catalog", (IModelCatalog modelCatalog, IHarnessStore store) =>
        {
            var storageLocation = store switch
            {
                CosmosHarnessStore cosmos => cosmos.StorageDescription,
                FileHarnessStore file => file.StorageFilePath,
                _ => "In-Memory"
            };
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
                    },
                    new
                    {
                        name = FeatureHarness.PromptColumnsKey,
                        description = "Define custom prompt columns with entity filters and process matching documents as PDF/Base64."
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
            IJobStore jobStore,
            IModelCatalog modelCatalog,
            IArtifactStore? artifactStore,
            CancellationToken ct) =>
        {
            updated.TenantId = tenantId;
            TenantHarness.EnsureDefaultCapabilities(updated);
            var saved = await store.UpsertTenantAsync(updated, ct);

            // Re-evaluate prompt columns on the tenant's recent completed jobs so new/updated prompts appear immediately
            var jobs = await jobStore.ListJobsByTenantAsync(tenantId, ct);
            foreach (var job in jobs.Where(j => j.Status == JobStatus.Completed).Take(10))
            {
                await EvaluatePromptColumnsExecutor.EvaluateJobPromptsAsync(job, saved, modelCatalog, artifactStore, ct);
                await jobStore.UpsertJobAsync(job, ct);
            }

            return TypedResults.Ok(saved);
        });

        // List all jobs for a tenant (newest first)
        group.MapGet("/tenants/{tenantId}/jobs", async (
            string tenantId,
            IHarnessStore harnessStore,
            IJobStore jobStore,
            IModelCatalog modelCatalog,
            IArtifactStore? artifactStore,
            CancellationToken ct) =>
        {
            var jobs = await jobStore.ListJobsByTenantAsync(tenantId, ct);
            var tenant = await harnessStore.GetTenantAsync(tenantId, ct);

            if (tenant is not null &&
                tenant.Capabilities.TryGetValue(FeatureHarness.PromptColumnsKey, out var pcCap) &&
                pcCap.Enabled &&
                (pcCap.PromptColumns?.Columns.Count ?? 0) > 0)
            {
                foreach (var job in jobs.Where(j => j.Status == JobStatus.Completed && j.PromptColumnResults.Count == 0).Take(10))
                {
                    await EvaluatePromptColumnsExecutor.EvaluateJobPromptsAsync(job, tenant, modelCatalog, artifactStore, ct);
                    await jobStore.UpsertJobAsync(job, ct);
                }
            }

            return Results.Ok(jobs.Select(FormatJobResponse));
        });

        // Explicitly evaluate or re-evaluate Prompt Columns on a specific job
        group.MapPost("/tenants/{tenantId}/jobs/{jobId}/evaluate-prompts", async (
            string tenantId,
            string jobId,
            IHarnessStore harnessStore,
            IJobStore jobStore,
            IModelCatalog modelCatalog,
            IArtifactStore? artifactStore,
            CancellationToken ct) =>
        {
            var tenant = await harnessStore.GetTenantAsync(tenantId, ct);
            if (tenant is null)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not found." });
            }

            var job = await jobStore.GetJobAsync(jobId, ct);
            if (job is null)
            {
                return Results.NotFound(new { error = $"Job '{jobId}' not found." });
            }

            await EvaluatePromptColumnsExecutor.EvaluateJobPromptsAsync(job, tenant, modelCatalog, artifactStore, ct);
            await jobStore.UpsertJobAsync(job, ct);

            return Results.Ok(FormatJobResponse(job));
        });

        // Stream or download a split PDF slice (or its translated PDF document when ?translated=true)
        group.MapGet("/tenants/{tenantId}/jobs/{jobId}/slices/{sliceIndex:int}/pdf", async (
            string tenantId,
            string jobId,
            int sliceIndex,
            bool? translated,
            IJobStore jobStore,
            IArtifactStore? artifactStore,
            CancellationToken ct) =>
        {
            var wantTranslated = translated == true;
            byte[]? pdfBytes = null;
            if (artifactStore is not null)
            {
                pdfBytes = await artifactStore.GetSlicePdfAsync(tenantId, jobId, sliceIndex, ct, isTranslated: wantTranslated);
            }

            if (pdfBytes is null)
            {
                var job = await jobStore.GetJobAsync(jobId, ct);
                var seg = job?.Segments.FirstOrDefault(s => s.SegmentIndex == sliceIndex);
                if (wantTranslated)
                {
                    if (seg?.TranslatedPdfBytes is { Length: > 0 })
                    {
                        pdfBytes = seg.TranslatedPdfBytes;
                    }
                    else if (!string.IsNullOrWhiteSpace(seg?.TranslatedText))
                    {
                        var footer = seg.Footers.FirstOrDefault() ?? $"Translated-{(seg.TargetLanguage ?? "EN").ToUpperInvariant()}";
                        pdfBytes = PdfPigLayoutExtractor.CreateSamplePdf([(seg.TranslatedText, footer)]);
                    }
                }

                if (pdfBytes is null)
                {
                    if (artifactStore is not null && wantTranslated)
                    {
                        pdfBytes = await artifactStore.GetSlicePdfAsync(tenantId, jobId, sliceIndex, ct, isTranslated: false);
                    }

                    pdfBytes ??= seg?.SplitPdfBytes;
                }
            }

            if (pdfBytes is null || pdfBytes.Length == 0)
            {
                return Results.NotFound(new { error = $"PDF slice {sliceIndex} for job '{jobId}' was not found." });
            }

            return Results.File(pdfBytes, "application/pdf", enableRangeProcessing: true);
        });

        // Ask AI across a tenant's processed documents
        group.MapPost("/tenants/{tenantId}/ask", async (
            string tenantId,
            [FromBody] AskTenantDocumentsRequest req,
            IHarnessStore harnessStore,
            IJobStore jobStore,
            IModelCatalog modelCatalog,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req?.Question))
            {
                return Results.BadRequest(new { error = "Question is required." });
            }

            var tenant = await harnessStore.GetTenantAsync(tenantId, ct);
            if (tenant is null)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not found." });
            }

            var jobs = (await jobStore.ListJobsByTenantAsync(tenantId, ct))
                .Where(j => j.Status == JobStatus.Completed)
                .Take(15)
                .ToList();

            if (jobs.Count == 0)
            {
                return Results.Ok(new
                {
                    answer = "No processed documents were found for this tenant yet. Upload a document using the 'Upload File' button to start asking questions.",
                    matchedDocuments = Array.Empty<string>(),
                    modelIdUsed = tenant.DefaultModelId
                });
            }

            var contextBuilder = new StringBuilder();
            foreach (var j in jobs)
            {
                contextBuilder.AppendLine($"=== Document: {j.FileName} (JobId: {j.JobId}, Date: {j.CreatedAtUtc:yyyy-MM-dd}) ===");
                if (!string.IsNullOrWhiteSpace(j.PacketSummary))
                {
                    contextBuilder.AppendLine($"Summary: {j.PacketSummary}");
                }
                if (j.ExtractedEntities.Count > 0)
                {
                    contextBuilder.AppendLine("Entities: " + string.Join("; ", j.ExtractedEntities.Select(e => $"{e.Name}={e.Value}")));
                }
                foreach (var de in j.DataExtractions)
                {
                    contextBuilder.AppendLine($"Structured Data ({de.DocumentName}, Pages {de.PageRange}): " +
                        string.Join("; ", de.Fields.Select(f => $"{f.FieldName}={f.FieldValue}")));
                }
                foreach (var s in j.Segments)
                {
                    var preview = s.BodyText.Length > 500 ? s.BodyText[..500] : s.BodyText;
                    contextBuilder.AppendLine($"Slice #{s.SegmentIndex + 1} ({s.IdentifiedDocumentName ?? s.DocumentTypeName}, Pages {s.StartPage}-{s.EndPage}): {preview}");
                }
                contextBuilder.AppendLine();
            }

            var modelId = modelCatalog.ResolveModelId(tenant, tenant.DefaultModelId);
            var chatClient = modelCatalog.ResolveChatClient(tenant, tenant.DefaultModelId);

            string answer;
            try
            {
                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, "You are the Document Intelligence Ask AI assistant. Answer the user's question concisely and accurately using ONLY the provided tenant documents context. Cite matching document file names and slice page ranges when relevant. If no document matches the request, state 'No document found matching the request.'"),
                    new(ChatRole.User, $"Tenant Documents Context:\n{contextBuilder}\n\nUser Question: {req.Question}")
                };

                var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
                answer = response.Text ?? string.Empty;

                // If running against SimulatedSliceChatClient (which returns JSON segments), synthesize a direct answer from context
                if (answer.TrimStart().StartsWith("{\"segments\"", StringComparison.Ordinal))
                {
                    answer = SynthesizeLocalAnswer(req.Question, jobs);
                }
            }
            catch
            {
                answer = SynthesizeLocalAnswer(req.Question, jobs);
            }

            return Results.Ok(new
            {
                answer,
                modelIdUsed = modelId,
                documentsEvaluated = jobs.Count
            });
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
                promptColumnResults = completed.PromptColumnResults,
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
            ("Formulario de Firma del Programa\nNúmero de MBA/MBSA: U5389226323\nNúmero de contrato: 69943885\nNombre de la entidad: Contoso Legal\nCuando Microsoft haya formalizado debidamente el formulario de firma, el Cliente recibirá una copia de confirmación.", "X20-12661-01-SigForm-ES"),
            ("Master Business Service Agreement\nThis Master Agreement governs all enterprise purchases.\nSection 1: Definitions and Scope.", "Form-MBSA-v1"),
            ("Master Business Service Agreement\nSection 2: Confidentiality and Liability Limits.\nSigned by Authorized Representative.", "Form-MBSA-v1"),
            ("Enterprise Enrollment\nEnrollment under the Master Business Service Agreement.\nIncludes 2,500 Enterprise Seats.", "Form-EA-v2"),
            ("Amendment ID M130\nCustom Pricing and Volume Discount Schedule.\nApplies to Enterprise Enrollment.", "VLAmendments,M130")
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
        job.PromptColumnResults,
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
        s.PromptColumnResults,
        s.BodyText,
        s.Markdown,
        s.SplitPdfBlobUri,
        s.SplitPdfSizeBytes,
        s.TranslatedPdfBlobUri,
        s.TranslatedPdfSizeBytes,
        splitPdfBase64 = s.SplitPdfBytes is not null ? Convert.ToBase64String(s.SplitPdfBytes) : null,
        translatedPdfBase64 = s.TranslatedPdfBytes is not null ? Convert.ToBase64String(s.TranslatedPdfBytes) : null
    };

    private static string SynthesizeLocalAnswer(string question, IReadOnlyList<DocumentJob> jobs)
    {
        var qTerms = question
            .Split([' ', ',', '.', '?', '"', '\'', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !new[] { "what", "all", "documents", "have", "the", "for", "with", "from", "are", "who", "show", "find", "list" }.Contains(w.ToLowerInvariant()))
            .ToList();

        var matches = new List<string>();
        foreach (var j in jobs)
        {
            var blob = $"{j.FileName} {j.PacketSummary} " +
                       string.Join(" ", j.ExtractedEntities.Select(e => $"{e.Name}:{e.Value}")) + " " +
                       string.Join(" ", j.DataExtractions.SelectMany(d => d.Fields).Select(f => $"{f.FieldName}:{f.FieldValue}")) + " " +
                       string.Join(" ", j.Segments.Select(s => $"{s.IdentifiedDocumentName} {s.BodyText}"));

            if (qTerms.Count == 0 || qTerms.Any(t => blob.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                var entitiesSummary = j.ExtractedEntities.Count > 0
                    ? string.Join(", ", j.ExtractedEntities.Take(4).Select(e => $"{e.Name}: {e.Value}"))
                    : "No entities extracted";
                matches.Add($"• **{j.FileName}** ({j.Segments.Count} slice(s), {j.TotalPages} pages) — {entitiesSummary}");
            }
        }

        return matches.Count > 0
            ? $"Found {matches.Count} matching document(s):\n" + string.Join("\n", matches)
            : "No document found matching the request.";
    }

    public sealed record AskTenantDocumentsRequest(string Question, string? JobId = null);
}
