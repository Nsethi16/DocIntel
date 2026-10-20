using System.Diagnostics;
using System.Security.Cryptography;
using DocIntel.Core.Evidence;
using DocIntel.Core.Features.DataExtraction;
using DocIntel.Core.Features.DocumentIdentification;
using DocIntel.Core.Features.DocumentInsight;
using DocIntel.Core.Features.DocumentTranslation;
using DocIntel.Core.Features.EntityExtraction;
using DocIntel.Core.Features.PromptColumns;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;
using DocIntel.Core.Storage;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Features.DocumentSplitting;

/// <summary>
/// Orchestrates the multi-stage document intelligence workflow using Microsoft Agent Framework
/// (<see cref="Microsoft.Agents.AI.Workflows"/>):
/// 1. File Splitting & Grouping (Deterministic PdfPig)
/// 2. Identify Documents (Microsoft Decision Model: Defined Names + Undefined)
/// 3. Document Translation (Azure Document Translation)
/// 4. Document Insight / Summary (Admin Summary Instructions)
/// 5. Entity Extraction (Admin Entity Extraction Prompt)
/// </summary>
public sealed class DocumentSplittingWorkflow
{
    public const string DefaultSystemPrompt = "Deterministic layout, footer, pagination, and orientation splitting.";

    private readonly IPageLayoutExtractor _layoutExtractor;
    private readonly IPdfSlicer _pdfSlicer;
    private readonly IModelCatalog _modelCatalog;
    private readonly IDocumentTranslationService _translationService;
    private readonly IArtifactStore? _artifactStore;
    private readonly AzureIntegrationOptions _azureOptions;
    private readonly ILogger<DocumentSplittingWorkflow> _logger;

    public DocumentSplittingWorkflow(
        IPageLayoutExtractor layoutExtractor,
        IPdfSlicer pdfSlicer,
        IModelCatalog modelCatalog,
        IDocumentTranslationService translationService,
        ILogger<DocumentSplittingWorkflow> logger,
        AzureIntegrationOptions? azureOptions = null,
        IArtifactStore? artifactStore = null)
    {
        _layoutExtractor = layoutExtractor ?? throw new ArgumentNullException(nameof(layoutExtractor));
        _pdfSlicer = pdfSlicer ?? throw new ArgumentNullException(nameof(pdfSlicer));
        _modelCatalog = modelCatalog ?? throw new ArgumentNullException(nameof(modelCatalog));
        _translationService = translationService ?? throw new ArgumentNullException(nameof(translationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _azureOptions = azureOptions ?? new AzureIntegrationOptions();
        _artifactStore = artifactStore;
    }

    public async Task<SplittingState> ExecuteAsync(
        SplittingState state,
        Func<SplittingState, CancellationToken, Task>? onStepCompleted = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.Tenant.IsActive)
        {
            throw new InvalidOperationException($"Tenant '{state.Tenant.TenantId}' is inactive.");
        }

        var splitEnabled = state.Tenant.Capabilities.TryGetValue(FeatureHarness.SplitAndGroupKey, out var splitFeature) &&
                           splitFeature.Enabled;
        var anyDownstreamEnabled =
            (state.Tenant.Capabilities.TryGetValue(FeatureHarness.IdentifyDocumentsKey, out var idCap) && idCap.Enabled) ||
            (state.Tenant.Capabilities.TryGetValue(FeatureHarness.DocumentTranslationKey, out var trCap) && trCap.Enabled) ||
            (state.Tenant.Capabilities.TryGetValue(FeatureHarness.DocumentInsightKey, out var inCap) && inCap.Enabled) ||
            (state.Tenant.Capabilities.TryGetValue(FeatureHarness.EntityExtractionKey, out var exCap) && exCap.Enabled) ||
            (state.Tenant.Capabilities.TryGetValue(FeatureHarness.DataExtractionKey, out var deCap) && deCap.Enabled) ||
            (state.Tenant.Capabilities.TryGetValue(FeatureHarness.PromptColumnsKey, out var pcCap) && pcCap.Enabled);

        if (!splitEnabled && !anyDownstreamEnabled)
        {
            state.StrategyUsed = "Skipped";
            state.ExecutionTrace.Add(new WorkflowStepTrace(
                FeatureHarness.SplitAndGroupKey,
                Executed: false,
                Succeeded: true,
                ModelIdUsed: null,
                DurationMs: 0,
                Notes: $"All features are disabled in tenant '{state.Tenant.TenantId}' harness."));
            return state;
        }

        var extractExecutor = new ExtractPageLayoutsExecutor(_layoutExtractor, _artifactStore);
        var detectExecutor = new DetectSliceBoundariesExecutor();
        var slicePdfExecutor = new WritePdfSlicesExecutor(_pdfSlicer, _artifactStore);
        var identifyExecutor = new IdentifyDocumentsExecutor(_modelCatalog);
        var translateExecutor = new TranslateDocumentsExecutor(_translationService, _azureOptions.ResolveEffectiveTranslatorEndpoint(), _artifactStore);
        var insightExecutor = new GenerateDocumentInsightsExecutor(_modelCatalog);
        var entityExecutor = new ExtractEntitiesExecutor(_modelCatalog);
        var dataExtractionExecutor = new ExtractDocumentDataExecutor(_modelCatalog);
        var promptColumnsExecutor = new EvaluatePromptColumnsExecutor(_modelCatalog, _artifactStore);

        var workflow = new WorkflowBuilder(extractExecutor)
            .AddEdge(extractExecutor, detectExecutor)
            .AddEdge(detectExecutor, slicePdfExecutor)
            .AddEdge(slicePdfExecutor, identifyExecutor)
            .AddEdge(identifyExecutor, translateExecutor)
            .AddEdge(translateExecutor, insightExecutor)
            .AddEdge(insightExecutor, entityExecutor)
            .AddEdge(entityExecutor, dataExtractionExecutor)
            .AddEdge(dataExtractionExecutor, promptColumnsExecutor)
            .WithOutputFrom(promptColumnsExecutor)
            .Build();

        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, state, cancellationToken: cancellationToken);
        await foreach (WorkflowEvent evt in run.WatchStreamAsync().WithCancellation(cancellationToken))
        {
            if (evt is ExecutorCompletedEvent completed && completed.Data is SplittingState stepState)
            {
                if (onStepCompleted is not null)
                {
                    await onStepCompleted(stepState, cancellationToken);
                }
            }
            else if (evt is WorkflowOutputEvent output && output.Data is SplittingState finalState)
            {
                _logger.LogInformation(
                    "DocumentSplittingWorkflow completed for Tenant='{TenantId}', DocumentId='{DocumentId}': {SliceCount} slice(s) across {PageCount} page(s).",
                    finalState.Tenant.TenantId, finalState.DocumentId, finalState.Slices.Count, finalState.Pages.Count);
                return finalState;
            }
            else if (evt is ExecutorFailedEvent failed && failed.Data is not null)
            {
                throw failed.Data;
            }
            else if (evt is WorkflowErrorEvent error && error.Exception is not null)
            {
                throw error.Exception;
            }
        }

        throw new InvalidOperationException("Document processing ended without a completed workflow output.");
    }
}

/// <summary>
/// Step 1 Executor: Extracts per-page layouts, bottom-of-page footer signatures, pagination, and markdown.
/// </summary>
[SendsMessage(typeof(SplittingState))]
internal sealed class ExtractPageLayoutsExecutor : Executor<SplittingState, SplittingState>
{
    private readonly IPageLayoutExtractor _extractor;
    private readonly IArtifactStore? _artifacts;

    public ExtractPageLayoutsExecutor(IPageLayoutExtractor extractor, IArtifactStore? artifacts = null)
        : base(nameof(ExtractPageLayoutsExecutor))
    {
        _extractor = extractor;
        _artifacts = artifacts;
    }

    public override async ValueTask<SplittingState> HandleAsync(
        SplittingState message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        if (message.Pages.Count == 0 && message.SourcePdfBytes is { Length: > 0 })
        {
            var config = message.Tenant.Capabilities.TryGetValue(FeatureHarness.SplitAndGroupKey, out var feature)
                ? feature.SplitAndGroup ?? new SplittingConfig()
                : new SplittingConfig();
            var extracted = await _extractor.ExtractPagesAsync(message.SourcePdfBytes, config, cancellationToken);
            message.Pages = extracted.ToList();
        }

        if (_artifacts is not null && message.SourcePdfBytes is { Length: > 0 } original)
        {
            message.LayoutSha256 = await _artifacts.SaveLayoutAsync(new DocumentLayout(
                message.Tenant.TenantId, message.DocumentId, Convert.ToHexString(SHA256.HashData(original)),
                message.Pages), cancellationToken);
        }

        sw.Stop();
        message.ExecutionTrace.Add(new WorkflowStepTrace(
            nameof(ExtractPageLayoutsExecutor),
            Executed: true,
            Succeeded: true,
            ModelIdUsed: null,
            DurationMs: sw.Elapsed.TotalMilliseconds,
            Notes: $"Extracted {message.Pages.Count} page layout(s)."));

        return message;
    }
}

/// <summary>
/// Step 2 Executor: Identifies document boundaries and groups pages deterministically using
/// footer clustering, cover-page backfill, pagination, and amendment rules.
/// </summary>
[SendsMessage(typeof(SplittingState))]
internal sealed class DetectSliceBoundariesExecutor : Executor<SplittingState, SplittingState>
{
    public DetectSliceBoundariesExecutor()
        : base(nameof(DetectSliceBoundariesExecutor))
    {
    }

    public override ValueTask<SplittingState> HandleAsync(
        SplittingState message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var splitEnabled = message.Tenant.Capabilities.TryGetValue(FeatureHarness.SplitAndGroupKey, out var feature) &&
                           feature.Enabled;
        var config = feature?.SplitAndGroup ?? new SplittingConfig();

        if (!splitEnabled)
        {
            message.StrategyUsed = "SingleDocument";
            if (message.Pages.Count > 0)
            {
                message.Slices =
                [
                    new DocumentSlice
                    {
                        SegmentIndex = 0,
                        StartPage = message.Pages[0].PageNumber,
                        EndPage = message.Pages[^1].PageNumber,
                        DocumentTypeName = message.FileName,
                        IsInferred = true,
                        Footers = message.Pages.Select(p => p.FooterText).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().ToArray(),
                        BodyText = string.Join(Environment.NewLine, message.Pages.Select(p => p.BodyText)),
                        Markdown = string.Join("\n<!-- PageBreak -->\n", message.Pages.Select(p => p.Markdown))
                    }
                ];
            }

            sw.Stop();
            message.ExecutionTrace.Add(new WorkflowStepTrace(
                nameof(DetectSliceBoundariesExecutor),
                Executed: false,
                Succeeded: true,
                ModelIdUsed: null,
                DurationMs: sw.Elapsed.TotalMilliseconds,
                Notes: "Skipped splitting (treated whole file as 1 slice)."));
            return ValueTask.FromResult(message);
        }

        message.StrategyUsed = nameof(SplittingStrategy.HeuristicFooter);
        message.ModelIdUsed = null;

        var slices = SliceBoundaryAnalyzer.GroupPagesHeuristically(message.Pages, config);
        if (config.ExpectedDocumentTypes.Count > 0)
        {
            SliceBoundaryAnalyzer.ReconcileWithCatalog(slices, config.ExpectedDocumentTypes, config.InferNameWhenNotInCatalog);
        }

        message.Slices = slices;
        sw.Stop();

        message.ExecutionTrace.Add(new WorkflowStepTrace(
            nameof(DetectSliceBoundariesExecutor),
            Executed: true,
            Succeeded: true,
            ModelIdUsed: null,
            DurationMs: sw.Elapsed.TotalMilliseconds,
            Notes: $"Grouped {message.Pages.Count} page(s) into {slices.Count} document slice(s) deterministically."));

        return ValueTask.FromResult(message);
    }
}

/// <summary>
/// Step 3 Executor: Physically slices the source PDF into standalone PDF byte streams per <see cref="DocumentSlice"/>.
/// </summary>
[SendsMessage(typeof(SplittingState))]
internal sealed class WritePdfSlicesExecutor : Executor<SplittingState, SplittingState>
{
    private readonly IPdfSlicer _pdfSlicer;
    private readonly IArtifactStore? _artifactStore;

    public WritePdfSlicesExecutor(IPdfSlicer pdfSlicer, IArtifactStore? artifactStore = null)
        : base(nameof(WritePdfSlicesExecutor))
    {
        _pdfSlicer = pdfSlicer;
        _artifactStore = artifactStore;
    }

    public override async ValueTask<SplittingState> HandleAsync(
        SplittingState message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var config = message.Tenant.Capabilities.TryGetValue(FeatureHarness.SplitAndGroupKey, out var feature)
            ? feature.SplitAndGroup ?? new SplittingConfig()
            : new SplittingConfig();

        if (config.ProduceSplitPdfBytes &&
            message.SourcePdfBytes is { Length: > 0 } &&
            message.Slices.Count > 0)
        {
            var ranges = message.Slices.Select(s => (s.StartPage, s.EndPage)).ToList();
            var splitBytes = await _pdfSlicer.SlicePdfAsync(message.SourcePdfBytes, ranges, cancellationToken);

            for (var i = 0; i < message.Slices.Count; i++)
            {
                message.Slices[i].SegmentIndex = i;
                message.Slices[i].SplitPdfBytes = splitBytes[i];
                message.Slices[i].SplitPdfSizeBytes = splitBytes[i].Length;

                if (_artifactStore is not null)
                {
                    message.Slices[i].SplitPdfBlobUri = await _artifactStore.SaveSlicePdfAsync(
                        message.Tenant.TenantId,
                        message.DocumentId,
                        i,
                        splitBytes[i],
                        cancellationToken);
                }
            }
        }

        sw.Stop();
        message.ExecutionTrace.Add(new WorkflowStepTrace(
            nameof(WritePdfSlicesExecutor),
            Executed: true,
            Succeeded: true,
            ModelIdUsed: null,
            DurationMs: sw.Elapsed.TotalMilliseconds,
            Notes: $"Sliced source PDF into {message.Slices.Count} standalone PDF artifact(s)."));

        return message;
    }
}
