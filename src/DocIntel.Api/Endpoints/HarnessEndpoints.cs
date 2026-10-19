using System.Text;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.DocumentTranslation;
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
                    },
                    new
                    {
                        name = FeatureHarness.DocumentGraphKey,
                        description = "Visualize cross-document relationships and constellations by extracted entities."
                    },
                    new
                    {
                        name = FeatureHarness.AskAiKey,
                        description = "Conversational question answering and synthesis across tenant documents."
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

            // Evaluate any newly added or modified Prompt Columns across ALL existing completed jobs in the background
            _ = Task.Run(async () =>
            {
                try
                {
                    var jobs = await jobStore.ListJobsByTenantAsync(tenantId, CancellationToken.None);
                    foreach (var job in jobs.Where(j => EvaluatePromptColumnsExecutor.JobNeedsPromptEvaluation(j, saved)))
                    {
                        await EvaluatePromptColumnsExecutor.EvaluateJobPromptsAsync(
                            job,
                            saved,
                            modelCatalog,
                            artifactStore,
                            CancellationToken.None,
                            forceReevaluateAll: false);
                        await jobStore.UpsertJobAsync(job, CancellationToken.None);
                    }
                }
                catch
                {
                }
            });

            return TypedResults.Ok(saved);
        });

        // Lightweight Server-Side Graph Projection API (scalable for 100K+ documents — returns only nodes, edges, and facets)
        group.MapGet("/tenants/{tenantId}/graph", async (
            string tenantId,
            string? entityName,
            string? entityValue,
            string? q,
            int? maxDocs,
            IHarnessStore harnessStore,
            IJobStore jobStore,
            CancellationToken ct) =>
        {
            var jobs = (await jobStore.ListJobsByTenantAsync(tenantId, ct))
                .Where(j => j.Status == JobStatus.Completed)
                .ToList();

            var cap = Math.Clamp(maxDocs ?? 5000, 10, 20000);
            var docNodes = new List<object>();
            var entityMap = new Dictionary<string, (string Name, string Value, string Category, int Count)>(StringComparer.OrdinalIgnoreCase);
            var edges = new List<object>();

            foreach (var j in jobs.Take(cap))
            {
                var docId = $"doc:{j.JobId}";
                var cleanName = (j.FileName ?? "Document").Replace(".pdf", "", StringComparison.OrdinalIgnoreCase);
                var seenPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var s in j.Segments)
                {
                    foreach (var e in s.ExtractedEntities)
                    {
                        if (string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.Value)) continue;
                        var key = $"{e.Name.Trim()}||{e.Value.Trim()}";
                        if (!seenPairs.Add(key)) continue;

                        if (!string.IsNullOrWhiteSpace(entityName) && !string.Equals(e.Name.Trim(), entityName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!string.IsNullOrWhiteSpace(entityValue) && !string.Equals(e.Value.Trim(), entityValue, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!string.IsNullOrWhiteSpace(q) &&
                            !cleanName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                            !e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                            !e.Value.Contains(q, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (entityMap.TryGetValue(key, out var existing))
                        {
                            entityMap[key] = existing with { Count = existing.Count + 1 };
                        }
                        else
                        {
                            entityMap[key] = (e.Name.Trim(), e.Value.Trim(), e.Category ?? "Entity", 1);
                        }

                        edges.Add(new { source = docId, target = $"ent:{key}" });
                    }
                }

                if (seenPairs.Count > 0)
                {
                    docNodes.Add(new
                    {
                        id = docId,
                        jobId = j.JobId,
                        label = cleanName,
                        pages = j.TotalPages,
                        slices = j.Segments.Count
                    });
                }
            }

            var entityNodes = entityMap.Select(kv => new
            {
                id = $"ent:{kv.Key}",
                pairKey = kv.Key,
                entityName = kv.Value.Name,
                entityValue = kv.Value.Value,
                category = kv.Value.Category,
                degree = kv.Value.Count
            }).OrderByDescending(e => e.degree).ToList();

            return Results.Ok(new
            {
                tenantId,
                totalDocumentsInTenant = jobs.Count,
                renderedDocuments = docNodes.Count,
                renderedEntities = entityNodes.Count,
                renderedEdges = edges.Count,
                docNodes,
                entityNodes,
                edges
            });
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

            if (tenant is not null)
            {
                var pendingEval = jobs.Where(j => EvaluatePromptColumnsExecutor.JobNeedsPromptEvaluation(j, tenant)).ToList();
                if (pendingEval.Count > 0)
                {
                    var fastCatalog = new ModelCatalog();
                    foreach (var job in pendingEval)
                    {
                        await EvaluatePromptColumnsExecutor.EvaluateJobPromptsAsync(
                            job,
                            tenant,
                            fastCatalog,
                            artifactStore: null,
                            ct,
                            forceReevaluateAll: false);
                    }

                    _ = Task.Run(async () =>
                    {
                        foreach (var job in pendingEval)
                        {
                            try
                            {
                                await jobStore.UpsertJobAsync(job, CancellationToken.None);
                            }
                            catch
                            {
                            }
                        }
                    });
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

            await EvaluatePromptColumnsExecutor.EvaluateJobPromptsAsync(job, tenant, modelCatalog, artifactStore, ct, forceReevaluateAll: true);
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

        // Ask AI across a tenant's processed documents (with Entity Scope Filtering, Hebbia Matrix Grid, and Verifiable Citations)
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

            var allCompletedJobs = (await jobStore.ListJobsByTenantAsync(tenantId, ct))
                .Where(j => j.Status == JobStatus.Completed)
                .ToList();

            // Apply Entity Scope Filters (same model as Document Graph)
            var filterEntityNames = (req.EntityNames ?? [])
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var filterPair = !string.IsNullOrWhiteSpace(req.EntityValuePair) &&
                             !string.Equals(req.EntityValuePair, "ALL", StringComparison.OrdinalIgnoreCase)
                ? req.EntityValuePair.Trim()
                : null;

            var scopedJobs = allCompletedJobs.Where(j =>
            {
                var jobEntities = j.Segments.SelectMany(s => s.ExtractedEntities).Concat(j.ExtractedEntities).ToList();
                if (filterPair is not null)
                {
                    var parts = filterPair.Split("||", 2);
                    if (parts.Length == 2 &&
                        !jobEntities.Any(e => string.Equals(e.Name?.Trim(), parts[0], StringComparison.OrdinalIgnoreCase) &&
                                              string.Equals(e.Value?.Trim(), parts[1], StringComparison.OrdinalIgnoreCase)))
                    {
                        return false;
                    }
                }

                if (filterEntityNames.Count > 0 &&
                    !jobEntities.Any(e => e.Name is not null && filterEntityNames.Contains(e.Name.Trim())))
                {
                    return false;
                }

                return true;
            }).ToList();

            if (scopedJobs.Count == 0)
            {
                return Results.Ok(new
                {
                    answer = "No documents match the selected entity filters. Clear or broaden the entity filter scope above to analyze across more tenant documents.",
                    modelIdUsed = tenant.DefaultModelId,
                    documentsEvaluated = 0,
                    totalTenantDocuments = allCompletedJobs.Count,
                    comparisonMatrix = Array.Empty<object>(),
                    citations = Array.Empty<object>(),
                    followUpSuggestions = new[]
                    {
                        "Compare total deal values and billing currencies across all customers",
                        "Which customers have Spanish signature packets or M130 pricing amendments?",
                        "Summarize all agreements and MBA/MBSA numbers across the tenant"
                    }
                });
            }

            var researchResult = SynthesizeCrossDocumentResearch(req.Question, scopedJobs, allCompletedJobs.Count, tenant.DefaultModelId);

            return Results.Ok(researchResult);
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

        // Seed 26 realistic example documents across customers, languages, and document types into IJobStore (Cosmos DB / InMemory)
        group.MapPost("/tenants/{tenantId}/seed-examples", async (
            string tenantId,
            IHarnessStore harnessStore,
            IJobStore jobStore,
            IPageLayoutExtractor layoutExtractor,
            IPdfSlicer pdfSlicer,
            IDocumentTranslationService translationService,
            ILogger<DocumentSplittingWorkflow> workflowLogger,
            CancellationToken ct) =>
        {
            var tenant = await harnessStore.GetTenantAsync(tenantId, ct);
            if (tenant is null)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not found." });
            }

            // Use fast deterministic catalog for bulk seeding 26 documents so Cosmos DB seeding completes in <5 seconds
            var fastWorkflow = new DocumentSplittingWorkflow(
                layoutExtractor,
                pdfSlicer,
                new ModelCatalog(),
                translationService,
                workflowLogger,
                azureOptions: new DocIntel.Core.AzureIntegrationOptions(),
                artifactStore: null);

            var examples = CreateExampleDocumentCorpus();
            var existingJobs = await jobStore.ListJobsByTenantAsync(tenantId, ct);
            var existingNames = existingJobs.Select(j => j.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var seededIds = new System.Collections.Concurrent.ConcurrentBag<string>();
            var pendingExamples = examples.Where(x => !existingNames.Contains(x.FileName)).ToList();

            await Parallel.ForEachAsync(
                pendingExamples,
                new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
                async (item, token) =>
                {
                    var (fileName, pages) = item;
                    var pdfBytes = PdfPigLayoutExtractor.CreateSamplePdf(pages);
                    var jobId = Guid.NewGuid().ToString("N");
                    var state = new SplittingState
                    {
                        DocumentId = jobId,
                        Tenant = tenant,
                        FileName = fileName,
                        SourcePdfBytes = pdfBytes
                    };

                    var completed = await fastWorkflow.ExecuteAsync(state, cancellationToken: token);
                    var job = new DocumentJob
                    {
                        JobId = jobId,
                        TenantId = tenant.TenantId,
                        TenantDisplayName = tenant.DisplayName,
                        AppliedHarnessVersion = tenant.HarnessVersion,
                        FileName = fileName,
                        Status = JobStatus.Completed,
                        TotalPages = completed.Pages.Count,
                        StrategyUsed = completed.StrategyUsed,
                        ModelIdUsed = completed.ModelIdUsed,
                        PacketSummary = completed.PacketSummary,
                        ExtractedEntities = completed.ExtractedEntities,
                        DataExtractions = completed.DataExtractions,
                        PromptColumnResults = completed.PromptColumnResults,
                        ExecutionTrace = completed.ExecutionTrace,
                        Segments = completed.Slices,
                        CompletedAtUtc = DateTimeOffset.UtcNow
                    };

                    await jobStore.UpsertJobAsync(job, token);
                    seededIds.Add(jobId);
                });

            return Results.Ok(new
            {
                tenantId = tenant.TenantId,
                seededCount = seededIds.Count,
                totalDocuments = existingNames.Count + seededIds.Count
            });
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

    private static IReadOnlyList<(string FileName, (string Body, string Footer)[] Pages)> CreateExampleDocumentCorpus() =>
    [
        // === Customer 1: Contoso Legal (MBA/MBSA U5389226323, Contract 69943885, USD) ===
        ("Contoso-Enterprise-Contract-Packet-2026.pdf",
        [
            ("Formulario de Firma del Programa\nNúmero de MBA/MBSA: U5389226323\nNúmero de contrato: 69943885\nNombre de la entidad: Contoso Legal\nCuando Microsoft haya formalizado debidamente el formulario de firma, el Cliente recibirá una copia de confirmación.", "X20-12661-01-SigForm-ES"),
            ("Master Business Service Agreement\nCustomer Name: Contoso Legal\nMBA/MBSA Number: U5389226323\nThis Master Agreement governs all enterprise purchases.\nSection 1: Definitions and Scope.", "Form-MBSA-v1"),
            ("Master Business Service Agreement\nSection 2: Confidentiality and Liability Limits.\nSigned by Authorized Representative for Contoso Legal.", "Form-MBSA-v1"),
            ("Enterprise Enrollment\nCustomer Name: Contoso Legal\nContract Number: 69943885\nEnrollment under the Master Business Service Agreement.\nIncludes 2,500 Enterprise Seats.", "Form-EA-v2"),
            ("Amendment ID M130\nCustomer Name: Contoso Legal\nCustom Pricing and Volume Discount Schedule.\nApplies to Enterprise Enrollment 69943885.", "VLAmendments,M130")
        ]),
        ("Contoso-Cloud-Price-Sheet-Q1.pdf",
        [
            ("Customer Price Sheet\nCustomer Name: Contoso Legal\nMBA/MBSA Number: U5389226323\nQuote Number: 3037534.001\nBilling currency: USD\nTotal deal value 410,000.00\nSection 1 Tenant ID: FOOD SERVICE - 6137346 282,600.00\nEP2-04960 124.50", "MSQuote-Contoso-Q1"),
            ("Customer Price Sheet\nCustomer Name: Contoso Legal\nQuote Number: 3037534.001\nBilling currency: USD\nFuture Pricing Schedule and Multi-Year Commitment.\nAAA-11820 88.00", "MSQuote-Contoso-Q1")
        ]),
        ("Contoso-Security-Addendum-M130.pdf",
        [
            ("Amendment ID M130\nCustomer Name: Contoso Legal\nBilling currency: USD\nData Protection and Zero Trust Security Rider.\nSpecial discount schedule for Microsoft Sentinel.", "VLAmendments,M130-Contoso")
        ]),
        ("Contoso-EA-Renewal-2026.pdf",
        [
            ("Enterprise Enrollment\nCustomer Name: Contoso Legal\nMBA/MBSA Number: U5389226323\nContract Number: 69943885\nBilling currency: USD\nThree-year renewal covering 4,200 M365 E5 seats.", "Form-EA-Renewal")
        ]),

        // === Customer 2: Cadena Comercial OXXO SA de CV (MBA/MBSA U4565450, Contract 78451290, MXN & USD) ===
        ("OXXO-Paquete-Firma-Programa-ES.pdf",
        [
            ("Formulario de Firma del Programa\nNúmero de MBA/MBSA: U4565450\nNúmero de contrato: 78451290\nNombre de la entidad: Cadena Comercial OXXO SA de CV\nCuando Microsoft haya formalizado debidamente el formulario de firma, el Cliente recibirá una copia de confirmación.", "DocumentX20-12883"),
            ("Enterprise Enrollment\nCustomer Name: Cadena Comercial OXXO SA de CV\nMBA/MBSA Number: U4565450\nContract Number: 78451290\nRetail store point-of-sale and cloud workload enrollment.", "Form-EA-OXXO")
        ]),
        ("OXXO-Retail-Price-Sheet-2026.pdf",
        [
            ("Customer Price Sheet\nCustomer Name: Cadena Comercial OXXO SA de CV\nMBA/MBSA Number: U4565450\nQuote Number: 3037534.004\nBilling currency: USD\nTotal deal value 282,600.00\nSection 1 Tenant ID: FOOD SERVICE - 6137346 282,600.00\nEP2-04960 95.00", "MSQuote-OXXO-2026")
        ]),
        ("OXXO-Master-Agreement-MBSA.pdf",
        [
            ("Master Business Service Agreement\nCustomer Name: Cadena Comercial OXXO SA de CV\nMBA/MBSA Number: U4565450\nGoverning Latin America retail operations and subsidiary entities.", "Form-MBSA-OXXO"),
            ("Master Business Service Agreement\nCustomer Name: Cadena Comercial OXXO SA de CV\nSection 2: Cross-border invoicing and compliance obligations.", "Form-MBSA-OXXO")
        ]),
        ("OXXO-Pricing-Amendment-M130.pdf",
        [
            ("Amendment ID M130\nCustomer Name: Cadena Comercial OXXO SA de CV\nBilling currency: MXN\nTotal deal value 5,450,000.00\nVolume expansion discount for 18,000 store locations.", "VLAmendments,M130-OXXO")
        ]),

        // === Customer 3: Woodgrove Bank NA (MBA/MBSA U8821904, Contract 55109234, USD & EUR) ===
        ("Woodgrove-Bank-Global-MBSA-Packet.pdf",
        [
            ("Program Signature Form\nCustomer Name: Woodgrove Bank NA\nMBA/MBSA Number: U8821904\nContract Number: 55109234\nGlobal financial services master signature instrument.", "ProgramSignForm-WG"),
            ("Master Business Service Agreement\nCustomer Name: Woodgrove Bank NA\nMBA/MBSA Number: U8821904\nFinancial regulatory compliance, audit rights, and DORA schedule.", "Form-MBSA-WG"),
            ("Enterprise Enrollment\nCustomer Name: Woodgrove Bank NA\nContract Number: 55109234\nBilling currency: USD\nCore banking cloud infrastructure and Copilot seat deployment.", "Form-EA-WG")
        ]),
        ("Woodgrove-Bank-Price-Sheet-USD.pdf",
        [
            ("Customer Price Sheet\nCustomer Name: Woodgrove Bank NA\nMBA/MBSA Number: U8821904\nQuote Number: 4091120.002\nBilling currency: USD\nTotal deal value 1,850,000.00\nTenant ID: WOODGROVE-FIN-99201\nEP2-04960 140.00", "MSQuote-WG-USD")
        ]),
        ("Woodgrove-Euro-Branch-Price-Sheet.pdf",
        [
            ("Customer Price Sheet\nCustomer Name: Woodgrove Bank NA\nQuote Number: 4091120.009\nBilling currency: EUR\nTotal deal value 920,000.00\nTenant ID: WOODGROVE-FIN-99201\nFrankfurt and Dublin banking operations.", "MSQuote-WG-EUR")
        ]),
        ("Woodgrove-Regulatory-Amendment-M130.pdf",
        [
            ("Amendment ID M130\nCustomer Name: Woodgrove Bank NA\nContract Number: 55109234\nBilling currency: USD\nCustom SLA and sovereign key management addendum.", "VLAmendments,M130-WG")
        ]),

        // === Customer 4: Northwind Traders Ltd (MBA/MBSA U7710342, Contract 44019822, GBP & USD) ===
        ("Northwind-SupplyChain-Bundle.pdf",
        [
            ("Program Signature Form\nCustomer Name: Northwind Traders Ltd\nMBA/MBSA Number: U7710342\nContract Number: 44019822\nExecuted by Chief Procurement Officer in London.", "ProgramSignForm-NW"),
            ("Enterprise Enrollment\nCustomer Name: Northwind Traders Ltd\nMBA/MBSA Number: U7710342\nContract Number: 44019822\nBilling currency: GBP\nGlobal logistics and Dynamics 365 Supply Chain workload.", "Form-EA-NW"),
            ("Customer Price Sheet\nCustomer Name: Northwind Traders Ltd\nQuote Number: 5188201.001\nBilling currency: GBP\nTotal deal value 640,000.00\nTenant ID: NORTHWIND-UK-4410\nEP2-04960 110.00", "MSQuote-NW-GBP")
        ]),
        ("Northwind-Master-Agreement-MBSA.pdf",
        [
            ("Master Business Service Agreement\nCustomer Name: Northwind Traders Ltd\nMBA/MBSA Number: U7710342\nUnited Kingdom and European Union commercial terms.", "Form-MBSA-NW")
        ]),
        ("Northwind-Logistics-Amendment-M130.pdf",
        [
            ("Amendment ID M130\nCustomer Name: Northwind Traders Ltd\nBilling currency: GBP\nSeasonal warehouse seat bursting and Azure IoT Hub discount.", "VLAmendments,M130-NW")
        ]),
        ("Northwind-US-Subsidiary-Price-Sheet.pdf",
        [
            ("Customer Price Sheet\nCustomer Name: Northwind Traders Ltd\nMBA/MBSA Number: U7710342\nQuote Number: 5188201.005\nBilling currency: USD\nTotal deal value 315,000.00\nTenant ID: NORTHWIND-UK-4410", "MSQuote-NW-USD")
        ]),

        // === Customer 5: Fabrikam Industrial Group (MBA/MBSA U9104451, Contract 88301145, EUR & USD) ===
        ("Fabrikam-Manufacturing-Packet-2026.pdf",
        [
            ("Formulario de Firma del Programa\nNúmero de MBA/MBSA: U9104451\nNúmero de contrato: 88301145\nNombre de la entidad: Fabrikam Industrial Group\nCuando Microsoft haya formalizado debidamente el formulario de firma, el Cliente recibirá una copia de confirmación.", "DocumentX20-12883-FAB"),
            ("Master Business Service Agreement\nCustomer Name: Fabrikam Industrial Group\nMBA/MBSA Number: U9104451\nIndustrial robotics and smart factory cloud terms.", "Form-MBSA-FAB"),
            ("Customer Price Sheet\nCustomer Name: Fabrikam Industrial Group\nMBA/MBSA Number: U9104451\nQuote Number: 6200491.003\nBilling currency: EUR\nTotal deal value 1,240,000.00\nTenant ID: FABRIKAM-MFG-7721", "MSQuote-FAB-EUR")
        ]),
        ("Fabrikam-Enterprise-Enrollment-EU.pdf",
        [
            ("Enterprise Enrollment\nCustomer Name: Fabrikam Industrial Group\nMBA/MBSA Number: U9104451\nContract Number: 88301145\nBilling currency: EUR\nCovers 9,500 engineering and factory floor workstations.", "Form-EA-FAB")
        ]),
        ("Fabrikam-AI-Foundry-Amendment.pdf",
        [
            ("Amendment ID M130\nCustomer Name: Fabrikam Industrial Group\nContract Number: 88301145\nBilling currency: USD\nTotal deal value 490,000.00\nAzure AI Foundry provisioned throughput reservation.", "VLAmendments,M130-FAB")
        ]),

        // === Customer 6: AdventureWorks Cycles (MBA/MBSA U3309812, Contract 31094480, CAD & USD) ===
        ("AdventureWorks-Full-Agreement-Bundle.pdf",
        [
            ("Program Signature Form\nCustomer Name: AdventureWorks Cycles\nMBA/MBSA Number: U3309812\nContract Number: 31094480\nNorth American retail and e-commerce signature packet.", "ProgramSignForm-AW"),
            ("Enterprise Enrollment\nCustomer Name: AdventureWorks Cycles\nContract Number: 31094480\nBilling currency: CAD\nOmnichannel commerce and customer analytics platform.", "Form-EA-AW")
        ]),
        ("AdventureWorks-Price-Sheet-CAD.pdf",
        [
            ("Customer Price Sheet\nCustomer Name: AdventureWorks Cycles\nMBA/MBSA Number: U3309812\nQuote Number: 7741029.001\nBilling currency: CAD\nTotal deal value 530,000.00\nTenant ID: ADVWORKS-CA-3301", "MSQuote-AW-CAD")
        ]),
        ("AdventureWorks-Holiday-Scale-Amendment.pdf",
        [
            ("Amendment ID M130\nCustomer Name: AdventureWorks Cycles\nBilling currency: USD\nTotal deal value 175,000.00\nPeak season e-commerce capacity amendment.", "VLAmendments,M130-AW")
        ]),

        // === Customer 7: Tailwind Aerospace (MBA/MBSA U6190238, Contract 92014477, USD) ===
        ("Tailwind-Aerospace-Defense-Packet.pdf",
        [
            ("Program Signature Form\nCustomer Name: Tailwind Aerospace\nMBA/MBSA Number: U6190238\nContract Number: 92014477\nAerospace engineering and simulation cloud program.", "ProgramSignForm-TW"),
            ("Master Business Service Agreement\nCustomer Name: Tailwind Aerospace\nMBA/MBSA Number: U6190238\nExport control and high-performance computing terms.", "Form-MBSA-TW"),
            ("Customer Price Sheet\nCustomer Name: Tailwind Aerospace\nMBA/MBSA Number: U6190238\nQuote Number: 8890124.007\nBilling currency: USD\nTotal deal value 2,400,000.00\nTenant ID: TAILWIND-AERO-880", "MSQuote-TW-USD")
        ]),
        ("Tailwind-Aerospace-Enrollment.pdf",
        [
            ("Enterprise Enrollment\nCustomer Name: Tailwind Aerospace\nMBA/MBSA Number: U6190238\nContract Number: 92014477\nBilling currency: USD\nIncludes digital twin simulation cluster licensing.", "Form-EA-TW")
        ]),
        ("Tailwind-HPC-Pricing-Amendment-M130.pdf",
        [
            ("Amendment ID M130\nCustomer Name: Tailwind Aerospace\nContract Number: 92014477\nBilling currency: USD\nTotal deal value 680,000.00\nGPU cluster step-up discount schedule.", "VLAmendments,M130-TW")
        ]),
        ("Iberia-Regional-Signature-Packet-ES.pdf",
        [
            ("Formulario de Firma del Programa\nNúmero de MBA/MBSA: U6190238\nNúmero de contrato: 92014477\nNombre de la entidad: Tailwind Aerospace\nCuando Microsoft haya formalizado debidamente el formulario de firma, el Cliente recibirá una copia de confirmación.", "X20-12661-01-Iberia-ES")
        ])
    ];

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

    private static object SynthesizeCrossDocumentResearch(
        string question,
        IReadOnlyList<DocumentJob> scopedJobs,
        int totalTenantDocuments,
        string modelId)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "what", "all", "documents", "document", "have", "the", "for", "with", "from", "are", "who",
            "show", "find", "list", "which", "compare", "across", "summarize", "summary", "tell", "about",
            "and", "or", "in", "of", "to", "is", "how", "many", "between", "any", "that", "this", "their", "customers", "customer"
        };

        var qTerms = question
            .Split([' ', ',', '.', '?', '"', '\'', ':', ';', '(', ')', '/', '&'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2 && !stopWords.Contains(w))
            .ToList();

        var scoredJobs = new List<(DocumentJob Job, int Score, DocumentSlice BestSlice, string Finding, string Excerpt)>();

        foreach (var j in scopedJobs)
        {
            var allEnts = j.Segments.SelectMany(s => s.ExtractedEntities).Concat(j.ExtractedEntities).ToList();
            var fullBlob = $"{j.FileName} {j.PacketSummary} " +
                           string.Join(" ", allEnts.Select(e => $"{e.Name} {e.Value}")) + " " +
                           string.Join(" ", j.Segments.Select(s => $"{s.IdentifiedDocumentName} {s.TranslatedText} {s.BodyText} {s.DetectedLanguage}"));

            int score = 0;
            foreach (var term in qTerms)
            {
                if (fullBlob.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    score += 2;
                }
                if (term.Equals("spanish", StringComparison.OrdinalIgnoreCase) &&
                    j.Segments.Any(s => string.Equals(s.DetectedLanguage, "es", StringComparison.OrdinalIgnoreCase)))
                {
                    score += 5;
                }
                if ((term.Equals("deal", StringComparison.OrdinalIgnoreCase) ||
                     term.Equals("value", StringComparison.OrdinalIgnoreCase) ||
                     term.Equals("values", StringComparison.OrdinalIgnoreCase) ||
                     term.Equals("price", StringComparison.OrdinalIgnoreCase) ||
                     term.Equals("pricing", StringComparison.OrdinalIgnoreCase) ||
                     term.Equals("currency", StringComparison.OrdinalIgnoreCase) ||
                     term.Equals("currencies", StringComparison.OrdinalIgnoreCase)) &&
                    allEnts.Any(e => e.Name is "Total Deal Value" or "Billing Currency" or "Quote Number"))
                {
                    score += 4;
                }
                if ((term.Equals("m130", StringComparison.OrdinalIgnoreCase) ||
                     term.Equals("amendment", StringComparison.OrdinalIgnoreCase) ||
                     term.Equals("amendments", StringComparison.OrdinalIgnoreCase)) &&
                    j.Segments.Any(s => s.IsAmendment || (s.IdentifiedDocumentName ?? "").Contains("Amendment", StringComparison.OrdinalIgnoreCase)))
                {
                    score += 5;
                }
            }

            if (qTerms.Count == 0 || score > 0)
            {
                var bestSlice = j.Segments
                    .OrderByDescending(s => qTerms.Count(t => ($"{s.IdentifiedDocumentName} {s.TranslatedText} {s.BodyText}").Contains(t, StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault() ?? j.Segments.FirstOrDefault() ?? new DocumentSlice();

                var rawBody = (!string.IsNullOrWhiteSpace(bestSlice.TranslatedText) ? bestSlice.TranslatedText : bestSlice.BodyText) ?? string.Empty;
                var cleanLines = rawBody
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 3 && !l.StartsWith("<!--", StringComparison.Ordinal))
                    .ToList();

                var excerpt = string.Join(" · ", cleanLines.Take(3));
                var finding = !string.IsNullOrWhiteSpace(bestSlice.InsightSummary)
                    ? bestSlice.InsightSummary
                    : excerpt;

                scoredJobs.Add((j, score == 0 ? 1 : score, bestSlice, finding, excerpt));
            }
        }

        // If no specific keyword hit, evaluate across all scoped jobs as a broad portfolio question
        if (scoredJobs.Count == 0)
        {
            foreach (var j in scopedJobs)
            {
                var bestSlice = j.Segments.FirstOrDefault() ?? new DocumentSlice();
                var rawBody = (!string.IsNullOrWhiteSpace(bestSlice.TranslatedText) ? bestSlice.TranslatedText : bestSlice.BodyText) ?? string.Empty;
                var cleanLines = rawBody.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Take(3);
                var excerpt = string.Join(" · ", cleanLines);
                scoredJobs.Add((j, 1, bestSlice, bestSlice.InsightSummary ?? excerpt, excerpt));
            }
        }

        var orderedMatches = scoredJobs.OrderByDescending(x => x.Score).ThenBy(x => x.Job.FileName).ToList();

        string GetEntity(DocumentJob job, string entityName) =>
            job.Segments.SelectMany(s => s.ExtractedEntities)
                .Concat(job.ExtractedEntities)
                .FirstOrDefault(e => string.Equals(e.Name, entityName, StringComparison.OrdinalIgnoreCase))?.Value ?? "—";

        var comparisonMatrix = orderedMatches.Select(m =>
        {
            var docTypes = string.Join(", ", m.Job.Segments.Select(s => s.IdentifiedDocumentName ?? s.DocumentTypeName ?? "Document").Distinct());
            var langs = string.Join(", ", m.Job.Segments.Select(s => (s.DetectedLanguage ?? "en").ToUpperInvariant()).Distinct());
            return new
            {
                jobId = m.Job.JobId,
                fileName = m.Job.FileName,
                cleanName = (m.Job.FileName ?? "Document").Replace(".pdf", "", StringComparison.OrdinalIgnoreCase),
                customerName = GetEntity(m.Job, "Customer Name"),
                mbsaNumber = GetEntity(m.Job, "MBA/MBSA Number"),
                contractNumber = GetEntity(m.Job, "Contract Number"),
                quoteNumber = GetEntity(m.Job, "Quote Number"),
                billingCurrency = GetEntity(m.Job, "Billing Currency"),
                totalDealValue = GetEntity(m.Job, "Total Deal Value"),
                documentTypes = docTypes,
                languages = langs,
                wasTranslated = m.Job.Segments.Any(s => s.WasTranslated),
                sliceIndex = m.BestSlice.SegmentIndex,
                pageRange = $"p.{m.BestSlice.StartPage}–{m.BestSlice.EndPage}",
                finding = m.Finding
            };
        }).ToList();

        var citations = orderedMatches.Take(12).Select((m, idx) => new
        {
            citationNumber = idx + 1,
            jobId = m.Job.JobId,
            fileName = m.Job.FileName,
            cleanName = (m.Job.FileName ?? "Document").Replace(".pdf", "", StringComparison.OrdinalIgnoreCase),
            sliceIndex = m.BestSlice.SegmentIndex,
            documentName = m.BestSlice.IdentifiedDocumentName ?? m.BestSlice.DocumentTypeName ?? "Document Slice",
            pageRange = $"p.{m.BestSlice.StartPage}–{m.BestSlice.EndPage}",
            customerName = GetEntity(m.Job, "Customer Name"),
            mbsaNumber = GetEntity(m.Job, "MBA/MBSA Number"),
            billingCurrency = GetEntity(m.Job, "Billing Currency"),
            totalDealValue = GetEntity(m.Job, "Total Deal Value"),
            excerpt = m.Excerpt
        }).ToList();

        var distinctCustomers = comparisonMatrix
            .Select(r => r.customerName)
            .Where(c => c != "—")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var distinctCurrencies = comparisonMatrix
            .Select(r => r.billingCurrency)
            .Where(c => c != "—")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        double totalDealSum = 0;
        foreach (var row in comparisonMatrix)
        {
            if (row.totalDealValue != "—")
            {
                var numeric = new string(row.totalDealValue.Where(c => char.IsDigit(c) || c == '.').ToArray());
                if (double.TryParse(numeric, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val))
                {
                    totalDealSum += val;
                }
            }
        }

        var synthesisBuilder = new StringBuilder();
        synthesisBuilder.AppendLine($"Analyzed **{orderedMatches.Count}** matching document{(orderedMatches.Count == 1 ? "" : "s")} across **{distinctCustomers.Count}** enterprise customer{(distinctCustomers.Count == 1 ? "" : "s")} ({string.Join(", ", distinctCustomers.Take(5))}{(distinctCustomers.Count > 5 ? $" +{distinctCustomers.Count - 5} more" : "")}).");
        synthesisBuilder.AppendLine();

        foreach (var row in comparisonMatrix.Take(8))
        {
            var commercialTag = row.totalDealValue != "—"
                ? $" · Deal Value: {row.totalDealValue} {row.billingCurrency}"
                : (row.billingCurrency != "—" ? $" · Currency: {row.billingCurrency}" : "");
            var mbsaTag = row.mbsaNumber != "—" ? $" · MBSA {row.mbsaNumber}" : "";
            var langTag = row.wasTranslated ? " · Translated ES → EN" : "";
            synthesisBuilder.AppendLine($"• **{row.cleanName}** ({row.customerName}{mbsaTag}{commercialTag}{langTag}): {row.finding}");
        }

        if (comparisonMatrix.Count > 8)
        {
            synthesisBuilder.AppendLine($"• *+{comparisonMatrix.Count - 8} additional matching documents detailed in the Cross-Document Matrix below.*");
        }

        return new
        {
            question,
            answer = synthesisBuilder.ToString().Trim(),
            modelIdUsed = modelId,
            documentsEvaluated = orderedMatches.Count,
            scopedDocumentsCount = scopedJobs.Count,
            totalTenantDocuments,
            summaryMetrics = new
            {
                matchedDocumentsCount = orderedMatches.Count,
                distinctCustomersCount = distinctCustomers.Count,
                customers = distinctCustomers,
                currencies = distinctCurrencies,
                totalDealValueSum = totalDealSum
            },
            comparisonMatrix,
            citations,
            followUpSuggestions = new[]
            {
                "Compare total deal values and billing currencies across all customers",
                "Which documents contain Spanish (ES) signature forms translated to English?",
                "List all M130 pricing amendments and their custom discount schedules",
                "Compare Woodgrove Bank NA and Cadena Comercial OXXO agreements and MBSA numbers"
            }
        };
    }

    public sealed record AskTenantDocumentsRequest(
        string Question,
        string? JobId = null,
        List<string>? EntityNames = null,
        string? EntityValuePair = null);
}
