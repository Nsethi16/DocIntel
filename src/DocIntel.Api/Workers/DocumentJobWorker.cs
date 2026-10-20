using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;

namespace DocIntel.Api.Workers;

/// <summary>
/// Background worker that processes enqueued document jobs asynchronously through the Microsoft Agent Framework workflow.
/// </summary>
public sealed class DocumentJobWorker : BackgroundService
{
    private readonly IJobQueue _jobQueue;
    private readonly IJobStore _jobStore;
    private readonly IHarnessStore _harnessStore;
    private readonly DocumentSplittingWorkflow _splittingWorkflow;
    private readonly ILogger<DocumentJobWorker> _logger;

    public DocumentJobWorker(
        IJobQueue jobQueue,
        IJobStore jobStore,
        IHarnessStore harnessStore,
        DocumentSplittingWorkflow splittingWorkflow,
        ILogger<DocumentJobWorker> logger)
    {
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _harnessStore = harnessStore ?? throw new ArgumentNullException(nameof(harnessStore));
        _splittingWorkflow = splittingWorkflow ?? throw new ArgumentNullException(nameof(splittingWorkflow));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var ticket in _jobQueue.ReadAllAsync(stoppingToken))
        {
            DocumentJob? job = null;
            try
            {
                job = await _jobStore.GetJobAsync(ticket.JobId, stoppingToken, ticket.TenantId);
                if (job is null)
                {
                    continue;
                }

                job.Status = JobStatus.Running;
                await _jobStore.UpsertJobAsync(job, stoppingToken);

                var tenant = await _harnessStore.GetTenantAsync(ticket.TenantId, stoppingToken)
                    ?? throw new InvalidOperationException($"Tenant '{ticket.TenantId}' not found.");

                var state = new SplittingState
                {
                    DocumentId = ticket.JobId,
                    Tenant = tenant,
                    FileName = ticket.FileName,
                    SourcePdfBytes = ticket.PdfBytes
                };

                var completedState = await _splittingWorkflow.ExecuteAsync(
                    state,
                    onStepCompleted: async (stepState, ct) =>
                    {
                        job.TotalPages = stepState.Pages.Count;
                        job.StrategyUsed = stepState.StrategyUsed;
                        job.ModelIdUsed = stepState.ModelIdUsed;
                        job.HasDevelopmentResults = stepState.HasDevelopmentResults;
                        job.ExecutionTrace = stepState.ExecutionTrace.ToList();
                        job.Segments = stepState.Slices.ToList();
                        await _jobStore.UpsertJobAsync(job, ct);
                    },
                    cancellationToken: stoppingToken);

                job.TotalPages = completedState.Pages.Count;
                job.StrategyUsed = completedState.StrategyUsed;
                job.ModelIdUsed = completedState.ModelIdUsed;
                job.HasDevelopmentResults = completedState.HasDevelopmentResults;
                job.PacketSummary = completedState.PacketSummary;
                job.ExtractedEntities = completedState.ExtractedEntities;
                job.DataExtractions = completedState.DataExtractions;
                job.PromptColumnResults = completedState.PromptColumnResults;
                job.ExecutionTrace = completedState.ExecutionTrace;
                job.Segments = completedState.Slices;
                job.Status = JobStatus.Completed;
                job.CompletedAtUtc = DateTimeOffset.UtcNow;

                await _jobStore.UpsertJobAsync(job, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError("Document job {JobId} failed with {ErrorType}; root cause {CauseType}.",
                    ticket.JobId, ex.GetType().Name, ex.GetBaseException().GetType().Name);
                if (job is not null)
                {
                    try
                    {
                        job.Status = JobStatus.Failed;
                        job.ErrorMessage = "Document processing failed. Check the provider configuration or source PDF and retry.";
                        job.CompletedAtUtc = DateTimeOffset.UtcNow;
                        await _jobStore.UpsertJobAsync(job, stoppingToken);
                    }
                    catch (Exception persistEx)
                    {
                        _logger.LogWarning(persistEx, "Failed persisting error state for job '{JobId}'.", ticket.JobId);
                    }
                }
            }
        }
    }
}

/// <summary>
/// Background worker that processes long-running agentic Ask AI runs asynchronously (Web UI, Email Mailbox, Microsoft Teams).
/// </summary>
public sealed class AskAiRunWorker : BackgroundService
{
    private readonly IAskAiQueue _askAiQueue;
    private readonly IAskAiRunStore _runStore;
    private readonly AskAiRunProcessor _processor;
    private readonly AskAiExecutionOptions _options;
    private readonly ILogger<AskAiRunWorker> _logger;

    public AskAiRunWorker(
        IAskAiQueue askAiQueue,
        IAskAiRunStore runStore,
        AskAiRunProcessor processor,
        AskAiExecutionOptions options,
        ILogger<AskAiRunWorker> logger)
    {
        _askAiQueue = askAiQueue ?? throw new ArgumentNullException(nameof(askAiQueue));
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(ProcessQueueAsync(stoppingToken), RecoverRunsAsync(stoppingToken));
    }

    private async Task RecoverRunsAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.RecoveryPollSeconds));
        do
        {
            try
            {
                var pending = await _runStore.ListUnfinishedRunsAsync(100, stoppingToken);
                foreach (var ticket in pending)
                {
                    if (!await _askAiQueue.TryEnqueueAsync(ticket, stoppingToken))
                    {
                        break;
                    }
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError("Could not rediscover Ask AI runs: {ErrorType}.", exception.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var ticket in _askAiQueue.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        await _processor.ProcessAsync(ticket, stoppingToken);
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError("Ask AI run {RunId} could not be processed: {ErrorType}.",
                            ticket.RunId, exception.GetType().Name);
                    }
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError("Ask AI dispatch receiver stopped unexpectedly: {ErrorType}.", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
