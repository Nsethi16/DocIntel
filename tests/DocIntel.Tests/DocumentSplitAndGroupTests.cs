using System.Text.Json;
using DocIntel.Core;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Features.DocumentIdentification;
using DocIntel.Core.Jobs;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.DocumentTranslation;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using UglyToad.PdfPig;
using Xunit;

namespace DocIntel.Tests;

public sealed class DocumentSplitAndGroupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DocIntel.Workflow.Tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void NormalizePageFootersBySimilarity_Clusters_Ocr_Garbled_Footers_To_Canonical_Value()
    {
        // Arrange
        var pages = new List<PageLayout>
        {
            new() { PageNumber = 1, FooterText = "SelectPlus2013Agreement", BodyText = "Page 1" },
            new() { PageNumber = 2, FooterText = "SelectPlus2013Agreement", BodyText = "Page 2" },
            new() { PageNumber = 3, FooterText = "SelectPlus2O13Agreement", BodyText = "Page 3 OCR typo O vs 0" }
        };

        // Act
        SliceBoundaryAnalyzer.NormalizePageFootersBySimilarity(pages, similarityThreshold: 0.85);

        // Assert
        Assert.All(pages, p => Assert.Equal("SelectPlus2013Agreement", p.FooterText));
    }

    [Fact]
    public void GroupPagesHeuristically_Splits_MultiForm_And_Amendment_Pages_Correctly()
    {
        // Arrange
        var pages = new List<PageLayout>
        {
            new() { PageNumber = 1, FooterText = "MBSA-2024", BodyText = "Master Agreement Page 1", Markdown = "Master Agreement Page 1" },
            new() { PageNumber = 2, FooterText = "MBSA-2024", BodyText = "Master Agreement Page 2", Markdown = "Master Agreement Page 2" },
            new() { PageNumber = 3, FooterText = "EnterpriseEnrollment-2024", BodyText = "Enrollment Page 1", Markdown = "Enrollment Page 1" },
            new() { PageNumber = 4, FooterText = "VLAmendments,M130", BodyText = "Amendment ID M130 terms", Markdown = "Amendment ID M130 terms\n<!-- PageFooter: M130 -->" },
            new() { PageNumber = 5, FooterText = "VLAmendments,M130", BodyText = "Amendment M130 continuation", Markdown = "Amendment M130 continuation\n<!-- PageFooter: M130 -->" },
            new() { PageNumber = 6, FooterText = "VLAmendments,CTM-M608", BodyText = "Amendment ID CTM-M608 custom terms", Markdown = "Amendment ID CTM-M608 custom terms\n<!-- PageFooter: CTM-M608 -->" }
        };
        var config = new SplittingConfig();

        // Act
        var slices = SliceBoundaryAnalyzer.GroupPagesHeuristically(pages, config);

        // Assert
        Assert.Equal(4, slices.Count);

        Assert.Equal(1, slices[0].StartPage);
        Assert.Equal(2, slices[0].EndPage);
        Assert.False(slices[0].IsAmendment);

        Assert.Equal(3, slices[1].StartPage);
        Assert.Equal(3, slices[1].EndPage);
        Assert.False(slices[1].IsAmendment);

        Assert.Equal(4, slices[2].StartPage);
        Assert.Equal(5, slices[2].EndPage);
        Assert.True(slices[2].IsAmendment);
        Assert.Equal("M130", slices[2].AmendmentId);

        Assert.Equal(6, slices[3].StartPage);
        Assert.Equal(6, slices[3].EndPage);
        Assert.True(slices[3].IsAmendment);
        Assert.Equal("CTM-M608", slices[3].AmendmentId);
    }

    [Fact]
    public async Task PdfPigLayoutExtractor_Extracts_Pages_And_Physically_Slices_MultiDocument_Pdf()
    {
        // Arrange
        var samplePdfBytes = PdfPigLayoutExtractor.CreateSamplePdf(
        [
            ("Master Business Agreement - Page 1", "Form-MBSA-v1"),
            ("Master Business Agreement - Page 2", "Form-MBSA-v1"),
            ("Enterprise Enrollment - Page 1", "Form-EA-v2"),
            ("Amendment ID M130 Pricing Terms", "Amendment-M130")
        ]);

        var extractor = new PdfPigLayoutExtractor();
        var config = new SplittingConfig();

        // Act
        var pages = await extractor.ExtractPagesAsync(samplePdfBytes, config);
        var slices = SliceBoundaryAnalyzer.GroupPagesHeuristically(pages, config);
        var ranges = slices.Select(s => (s.StartPage, s.EndPage)).ToList();
        var splitPdfs = await extractor.SlicePdfAsync(samplePdfBytes, ranges);

        // Assert
        Assert.Equal(4, pages.Count);
        Assert.Equal(3, slices.Count);
        Assert.Equal(3, splitPdfs.Count);

        using var doc0 = PdfDocument.Open(splitPdfs[0]);
        using var doc1 = PdfDocument.Open(splitPdfs[1]);
        using var doc2 = PdfDocument.Open(splitPdfs[2]);

        Assert.Equal(2, doc0.NumberOfPages);
        Assert.Equal(1, doc1.NumberOfPages);
        Assert.Equal(1, doc2.NumberOfPages);
    }

    [Fact]
    public async Task DocumentSplittingWorkflow_Runs_Deterministic_Workflow_And_Catalog_Matching()
    {
        // Arrange
        var tenant = new TenantHarness
        {
            TenantId = "contoso-legal",
            DisplayName = "Contoso Legal Ops",
            Capabilities = new Dictionary<string, FeatureHarness>(StringComparer.OrdinalIgnoreCase)
            {
                [FeatureHarness.SplitAndGroupKey] = new FeatureHarness
                {
                    Enabled = true,
                    SplitAndGroup = new SplittingConfig
                    {
                        InferNameWhenNotInCatalog = true,
                        ExpectedDocumentTypes =
                        [
                            new DocumentCatalogEntry
                            {
                                Name = "Master Business Service Agreement",
                                IdentificationHint = "Contains MBSA footer or Master Business header",
                                SchemaId = "schema-mbsa-v1",
                                IsActive = true
                            }
                        ]
                    }
                }
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDocIntelCore(allowDevelopmentSimulation: true);

        using var provider = services.BuildServiceProvider();
        var workflow = provider.GetRequiredService<DocumentSplittingWorkflow>();

        var pdfBytes = PdfPigLayoutExtractor.CreateSamplePdf(
        [
            ("Master Business Service Agreement\nPage 1 terms", "Form-MBSA-v1"),
            ("Master Business Service Agreement\nPage 2 terms", "Form-MBSA-v1"),
            ("Special Partner Addendum\nPage 1 terms", "Custom-Addendum-99")
        ]);

        var state = new SplittingState
        {
            Tenant = tenant,
            FileName = "contoso-packet.pdf",
            SourcePdfBytes = pdfBytes
        };

        // Act
        var completed = await workflow.ExecuteAsync(state);

        // Assert
        Assert.Null(completed.ModelIdUsed);
        Assert.Equal(10, completed.ExecutionTrace.Count);
        Assert.Equal(2, completed.Slices.Count);

        Assert.Equal("Master Business Service Agreement", completed.Slices[0].DocumentTypeName);
        Assert.False(completed.Slices[0].IsInferred);
        Assert.Equal("schema-mbsa-v1", completed.Slices[0].SchemaId);
        Assert.True(completed.Slices[0].SplitPdfSizeBytes > 0);

        Assert.Equal("Special Partner Addendum", completed.Slices[1].DocumentTypeName);
        Assert.True(completed.Slices[1].IsInferred);
        Assert.Null(completed.Slices[1].SchemaId);
        Assert.True(completed.Slices[1].SplitPdfSizeBytes > 0);
    }

    [Fact]
    public async Task FullPipeline_Executes_Identify_Translate_Insight_And_EntityExtraction()
    {
        // Arrange
        var tenant = new TenantHarness
        {
            TenantId = "contoso-full",
            DisplayName = "Contoso Full Pipeline",
            DefaultModelId = "phi-4",
            AllowedModels = ["phi-4", "mai-reasoning"],
            Capabilities = new Dictionary<string, FeatureHarness>(StringComparer.OrdinalIgnoreCase)
            {
                [FeatureHarness.SplitAndGroupKey] = new FeatureHarness
                {
                    Enabled = true,
                    SplitAndGroup = new SplittingConfig()
                },
                [FeatureHarness.IdentifyDocumentsKey] = new FeatureHarness
                {
                    Enabled = true,
                    ModelIdOverride = "phi-4",
                    IdentifyDocuments = new DocumentIdentificationConfig
                    {
                        DecisionModelId = "phi-4",
                        Rules =
                        [
                            new DocumentIdentificationRule
                            {
                                DocumentName = "Program Signature Form",
                                IdentificationPrompt = "If document footer contains DocumentX20-12883 or title contains Formulario de Firma"
                            }
                        ]
                    }
                },
                [FeatureHarness.DocumentTranslationKey] = new FeatureHarness
                {
                    Enabled = true,
                    DocumentTranslation = new DocumentTranslationConfig
                    {
                        TargetLanguage = "en",
                        DoNotTranslateTerms = ["OXXO", "MBSA"]
                    }
                },
                [FeatureHarness.DocumentInsightKey] = new FeatureHarness
                {
                    Enabled = true,
                    ModelIdOverride = "phi-4",
                    DocumentInsight = new DocumentInsightConfig
                    {
                        SummaryInstructions = "Highlight the customer entity and contract number."
                    }
                },
                [FeatureHarness.EntityExtractionKey] = new FeatureHarness
                {
                    Enabled = true,
                    ModelIdOverride = "phi-4",
                    EntityExtraction = new EntityExtractionConfig
                    {
                        ExtractionPrompt = "Extract Customer Name, MBA/MBSA Number, and Quote Number."
                    }
                },
                [FeatureHarness.DataExtractionKey] = new FeatureHarness
                {
                    Enabled = true,
                    ModelIdOverride = "phi-4",
                    DataExtraction = new DataExtractionConfig
                    {
                        Profiles =
                        [
                            new DocumentDataExtractionProfile
                            {
                                DocumentName = "Program Signature Form",
                                Fields =
                                [
                                    new DataExtractionFieldSchema
                                    {
                                        FieldName = "MbaMbsaNumber",
                                        Instruction = "Extract the MBA/MBSA agreement number"
                                    },
                                    new DataExtractionFieldSchema
                                    {
                                        FieldName = "CustomerEntityName",
                                        Instruction = "Extract the legal customer entity name"
                                    }
                                ],
                                Tables =
                                [
                                    new DataExtractionTableSchema
                                    {
                                        TableName = "PurchaseOrderSummary",
                                        Instruction = "Extract purchase order summary rows",
                                        Columns =
                                        [
                                            new DataExtractionColumnSchema { ColumnName = "Section", Instruction = "Section name" },
                                            new DataExtractionColumnSchema { ColumnName = "TenantId", Instruction = "Tenant ID" },
                                            new DataExtractionColumnSchema { ColumnName = "Amount", Instruction = "Total amount" }
                                        ]
                                    }
                                ]
                            }
                        ]
                    }
                }
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDocIntelCore(allowDevelopmentSimulation: true);

        using var provider = services.BuildServiceProvider();
        var workflow = provider.GetRequiredService<DocumentSplittingWorkflow>();

        var pdfBytes = PdfPigLayoutExtractor.CreateSamplePdf(
        [
            ("Formulario de Firma del Programa\nNúmero de MBA/MBSA U4565450\nCliente Cadena Comercial OXXO SA de CV\nSection 1 Tenant ID: FOOD SERVICE - 6137346 282,600.00", "DocumentX20-12883"),
            ("Unlisted Custom Mystery Document\nQuote Number: 3037534.004\nTotal deal value 282,600.00", "MysteryFooter-999")
        ]);

        var state = new SplittingState
        {
            Tenant = tenant,
            FileName = "test-bundle.pdf",
            SourcePdfBytes = pdfBytes
        };

        // Act
        var completed = await workflow.ExecuteAsync(state);

        // Assert
        Assert.Equal(2, completed.Slices.Count);

        // Slice 0 matched rule -> "Program Signature Form", translated from Spanish ("es") to English ("en"), DataExtraction populated!
        Assert.Equal("Program Signature Form", completed.Slices[0].IdentifiedDocumentName);
        Assert.Equal("es", completed.Slices[0].DetectedLanguage);
        Assert.True(completed.Slices[0].WasTranslated);
        Assert.Contains("Program Signature Form", completed.Slices[0].TranslatedText);
        Assert.Null(completed.Slices[0].InsightSummary);
        Assert.Contains(completed.Slices[0].ExtractedEntities, e => e.Name == "MBA/MBSA Number" && e.Value == "U4565450");

        Assert.NotNull(completed.Slices[0].DataExtraction);
        Assert.Equal("Program Signature Form", completed.Slices[0].DataExtraction!.DocumentName);
        Assert.Contains(completed.Slices[0].DataExtraction!.Fields, f => f.FieldName == "MbaMbsaNumber" && f.FieldValue == "U4565450");
        Assert.Contains(completed.Slices[0].DataExtraction!.Fields, f => f.FieldName == "CustomerEntityName" && f.FieldValue == "Cadena Comercial OXXO SA de CV");
        Assert.Single(completed.Slices[0].DataExtraction!.Tables);
        Assert.Equal("PurchaseOrderSummary", completed.Slices[0].DataExtraction!.Tables[0].TableName);
        Assert.Single(completed.Slices[0].DataExtraction!.Tables[0].Rows);

        // Slice 1 did not match any defined rule -> classified as "Undefined" (no DataExtraction profile)
        Assert.Equal(IdentifyDocumentsExecutor.UndefinedDocumentName, completed.Slices[1].IdentifiedDocumentName);
        Assert.Null(completed.Slices[1].DataExtraction);
        Assert.Equal("en", completed.Slices[1].DetectedLanguage);
        Assert.False(completed.Slices[1].WasTranslated);
        Assert.Null(completed.Slices[1].InsightSummary);
        Assert.Contains(completed.Slices[1].ExtractedEntities, e => e.Name == "Quote Number" && e.Value == "3037534.004");
        Assert.NotEmpty(completed.ExtractedEntities);
        // Incomplete identification uses one file insight, without separate page/slice summaries.
        Assert.False(string.IsNullOrWhiteSpace(completed.PacketSummary));
    }

    [Fact]
    public async Task DocumentSplittingWorkflow_Evaluates_PromptColumns_With_Entity_Filters_And_Pdf_Base64()
    {
        // Arrange
        var tenant = new TenantHarness
        {
            TenantId = "contoso-prompts",
            DisplayName = "Contoso Prompt Studio",
            DefaultModelId = "gpt-5-mini"
        };

        tenant.Capabilities[FeatureHarness.PromptColumnsKey] = new FeatureHarness
        {
            Enabled = true,
            ModelIdOverride = "gpt-5-mini",
            PromptColumns = new PromptColumnsConfig
            {
                Columns =
                [
                    new PromptColumnDefinition
                    {
                        ColumnId = "col-all",
                        ColumnName = "Summarize Document",
                        PromptInstructions = "Summarize the contents of {{Document.Content}}.",
                        FilterMode = "All",
                        EntityFilters = []
                    },
                    new PromptColumnDefinition
                    {
                        ColumnId = "col-mbsa-only",
                        ColumnName = "MBSA Verification",
                        PromptInstructions = "Verify MBA/MBSA clauses in {{Document.Content}}.",
                        FilterMode = "And",
                        EntityFilters =
                        [
                            new PromptEntityFilterRule
                            {
                                EntityName = "MBA/MBSA Number",
                                Operator = "Exists"
                            }
                        ]
                    }
                ]
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDocIntelCore(allowDevelopmentSimulation: true);

        using var provider = services.BuildServiceProvider();
        var workflow = provider.GetRequiredService<DocumentSplittingWorkflow>();

        var pdfBytes = PdfPigLayoutExtractor.CreateSamplePdf(
        [
            ("Formulario de Firma del Programa\nNúmero de MBA/MBSA U4565450\nCliente Cadena Comercial OXXO SA de CV", "DocumentX20-12883"),
            ("Customer Price Sheet\nQuote Number: 3037534.004\nTotal deal value 282,600.00", "MSQuote-999")
        ]);

        var state = new SplittingState
        {
            Tenant = tenant,
            FileName = "prompts-test.pdf",
            SourcePdfBytes = pdfBytes
        };

        // Act
        var completed = await workflow.ExecuteAsync(state);

        // Assert
        Assert.Equal(2, completed.Slices.Count);
        Assert.Equal(4, completed.PromptColumnResults.Count);

        // Slice 0 has MBA/MBSA Number -> BOTH prompt columns match and are processed as PDF/Base64
        var slice0All = completed.Slices[0].PromptColumnResults.Single(r => r.ColumnId == "col-all");
        var slice0Mbsa = completed.Slices[0].PromptColumnResults.Single(r => r.ColumnId == "col-mbsa-only");
        Assert.True(slice0All.FilterMatched);
        Assert.True(slice0All.ProcessedAsFileBase64);
        Assert.StartsWith("JVBERi0", slice0All.Base64Preview);
        Assert.True(slice0Mbsa.FilterMatched);
        Assert.True(slice0Mbsa.ProcessedAsFileBase64);
        Assert.Contains(slice0Mbsa.MatchedEntities, m => m.Contains("MBA/MBSA Number = U4565450"));

        // Slice 1 does NOT have MBA/MBSA Number -> "col-all" matches, "col-mbsa-only" is SkippedByFilter
        var slice1All = completed.Slices[1].PromptColumnResults.Single(r => r.ColumnId == "col-all");
        var slice1Mbsa = completed.Slices[1].PromptColumnResults.Single(r => r.ColumnId == "col-mbsa-only");
        Assert.True(slice1All.FilterMatched);
        Assert.True(slice1All.ProcessedAsFileBase64);
        Assert.False(slice1Mbsa.FilterMatched);
        Assert.Equal("SkippedByFilter", slice1Mbsa.Status);
        Assert.False(slice1Mbsa.ProcessedAsFileBase64);
    }

    [Fact]
    public async Task AskAiOrchestrator_Injects_PromptColumns_Generates_CosmosSql_And_Returns_Citations_And_ChannelPayloads()
    {
        // Arrange
        var harnessPath = Path.Combine(_directory, "harness.json");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDocIntelCore(harnessFilePath: harnessPath, allowDevelopmentSimulation: true);

        using var provider = services.BuildServiceProvider();
        var harnessStore = provider.GetRequiredService<IHarnessStore>();
        var jobStore = provider.GetRequiredService<IJobStore>();
        var workflow = provider.GetRequiredService<DocumentSplittingWorkflow>();
        var askClient = new Mock<IChatClient>();
        askClient.SetupSequence(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("search-1", "search_documents", new Dictionary<string, object?>
                {
                    ["query"] = JsonSerializer.SerializeToElement(new DocumentQuery { SearchText = "Woodgrove" })
                })
            ])))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("inspect-1", "inspect_document_slice", new Dictionary<string, object?>
                {
                    ["jobId"] = "job-wg-1", ["sliceIndex"] = 0, ["textOffset"] = 0
                })
            ])))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "{\"kind\":\"DocumentAnswer\",\"answer\":\"The Woodgrove agreement is supported by the source document [1].\"," +
                "\"evidenceIds\":[\"e1\"]}")));
        var askCatalog = new ModelCatalog(customClients: new Dictionary<string, IChatClient>
        {
            ["gpt-5-mini"] = askClient.Object
        });
        var askOrchestrator = new AskAiOrchestrator(
            harnessStore, jobStore, askCatalog, [], NullLogger<AskAiOrchestrator>.Instance);

        var tenant = await harnessStore.GetTenantAsync("contoso-legal")
                     ?? new TenantHarness { TenantId = "contoso-legal", DisplayName = "Contoso Legal" };

        // Verify system prompt includes Prompt Column definitions and Cosmos DB schema
        var systemPrompt = AskAiPromptBuilder.BuildSystemPrompt(tenant);
        Assert.Contains("ADMIN-DEFINED PROMPT COLUMNS", systemPrompt);
        Assert.Contains("COSMOS DB 'jobs' CONTAINER JSON SCHEMA", systemPrompt);
        Assert.Contains("Contract Check", systemPrompt);

        var pdfBytes = PdfPigLayoutExtractor.CreateSamplePdf(
        [
            ("Program Signature Form\nCustomer Name: Woodgrove Bank NA\nMBA/MBSA Number: U8821904\nContract Number: 55109234", "ProgramSignForm-WG"),
            ("Customer Price Sheet\nCustomer Name: Woodgrove Bank NA\nQuote Number: 4091120.002\nBilling currency: USD\nTotal deal value 1,850,000.00", "MSQuote-WG-USD")
        ]);

        var completedState = await workflow.ExecuteAsync(new SplittingState
        {
            DocumentId = "job-wg-1",
            Tenant = tenant,
            FileName = "Woodgrove-Bank-Global-MBSA-Packet.pdf",
            SourcePdfBytes = pdfBytes
        });

        await jobStore.UpsertJobAsync(new DocIntel.Core.Jobs.DocumentJob
        {
            JobId = "job-wg-1",
            TenantId = tenant.TenantId,
            TenantDisplayName = tenant.DisplayName,
            AppliedHarnessVersion = tenant.HarnessVersion,
            FileName = "Woodgrove-Bank-Global-MBSA-Packet.pdf",
            Status = DocIntel.Core.Jobs.JobStatus.Completed,
            TotalPages = completedState.Pages.Count,
            ExtractedEntities = completedState.ExtractedEntities,
            DataExtractions = completedState.DataExtractions,
            PromptColumnResults = completedState.PromptColumnResults,
            Segments = completedState.Slices
        });

        // Act
        var run = new AskAiRun
        {
            RunId = "run-1",
            TenantId = tenant.TenantId,
            Question = "What agreements and deal values exist for Woodgrove Bank?",
            ReplyChannel = new AskAiReplyChannel { ChannelType = AskAiChannelType.Email, FromAddress = "analyst@contoso.com" }
        };

        var finishedRun = await askOrchestrator.ExecuteRunAsync(run);

        // Assert
        Assert.Equal(DocIntel.Core.Jobs.JobStatus.Completed, finishedRun.Status);
        Assert.NotEmpty(finishedRun.ExecutedCosmosQueries);
        Assert.StartsWith("SELECT", finishedRun.ExecutedCosmosQueries[0]);
        Assert.NotEmpty(finishedRun.Citations);
        Assert.Equal(1, finishedRun.Citations[0].CitationNumber);
        Assert.Equal("Woodgrove-Bank-Global-MBSA-Packet", finishedRun.Citations[0].CleanName);
        Assert.Contains("[1]", finishedRun.Answer);
        Assert.Contains("Woodgrove", finishedRun.Answer);
        Assert.Contains("Open PDF", finishedRun.EmailHtmlBody);
        Assert.Contains("AdaptiveCard", finishedRun.TeamsAdaptiveCardJson);
    }

    [Fact]
    public async Task DocumentSplittingWorkflow_Skips_Execution_When_Disabled_In_Tenant_Harness()
    {
        // Arrange
        var tenant = new TenantHarness
        {
            TenantId = "fabrikam-disabled",
            DisplayName = "Fabrikam",
            Capabilities = new Dictionary<string, FeatureHarness>(StringComparer.OrdinalIgnoreCase)
            {
                [FeatureHarness.SplitAndGroupKey] = new FeatureHarness
                {
                    Enabled = false
                }
            }
        };

        var extractor = new PdfPigLayoutExtractor();
        var catalog = new ModelCatalog(allowDevelopmentSimulation: true);
        var translator = new AzureDocumentTranslationService(allowDevelopmentSimulation: true);
        var workflow = new DocumentSplittingWorkflow(extractor, extractor, catalog, translator, NullLogger<DocumentSplittingWorkflow>.Instance);

        var state = new SplittingState
        {
            Tenant = tenant,
            FileName = "sample.pdf"
        };

        // Act
        var completed = await workflow.ExecuteAsync(state);

        // Assert
        Assert.Empty(completed.Slices);
        Assert.Single(completed.ExecutionTrace);
        Assert.False(completed.ExecutionTrace[0].Executed);
    }

    [Fact]
    public async Task FileHarnessStore_Persists_Tenant_Harness_Settings_And_Increments_Version()
    {
        // Arrange
        var tempFile = Path.Combine(Path.GetTempPath(), $"harness-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new FileHarnessStore(tempFile);

            // Act
            var seeded = await store.ListTenantsAsync();
            var contoso = await store.GetTenantAsync("contoso-legal");
            Assert.NotNull(contoso);
            Assert.Equal(1, contoso.HarnessVersion);

            contoso.DefaultModelId = "mistral-large";
            await store.UpsertTenantAsync(contoso);

            var reloadedStore = new FileHarnessStore(tempFile);
            var reloadedContoso = await reloadedStore.GetTenantAsync("contoso-legal");

            // Assert
            Assert.True(seeded.Count >= 2);
            Assert.NotNull(reloadedContoso);
            Assert.Equal(2, reloadedContoso.HarnessVersion);
            Assert.Equal("mistral-large", reloadedContoso.DefaultModelId);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
