using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Jobs;

namespace DocIntel.Api.Workers;

/// <summary>
/// Background worker that processes enqueued document jobs asynchronously through the Microsoft Agent Framework workflow.
/// </summary>
public sealed class DocumentJobWorker : BackgroundService
{
    private readonly IJobQueue _jobQueue;
    private readonly IJobStore _jobStore;
    private readonly DocumentJobProcessor _processor;
    private readonly DocumentProcessingOptions _options;
    private readonly ILogger<DocumentJobWorker> _logger;

    public DocumentJobWorker(
        IJobQueue jobQueue,
        IJobStore jobStore,
        DocumentJobProcessor processor,
        DocumentProcessingOptions options,
        ILogger<DocumentJobWorker> logger)
    {
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _processor = processor;
        _options = options;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(ProcessQueueAsync(stoppingToken), RecoverAsync(stoppingToken));
    }

    private async Task RecoverAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.RecoveryPollSeconds));
        do
        {
            try
            {
                foreach (var ticket in await _jobStore.ListRecoverableJobsAsync(100, stoppingToken))
                {
                    if (!await _jobQueue.TryEnqueueAsync(ticket, stoppingToken)) break;
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError("Document recovery is unavailable: {ErrorType}.", exception.GetType().Name);
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
                await foreach (var ticket in _jobQueue.ReadAllAsync(stoppingToken))
                {
                    try { await _processor.ProcessAsync(ticket, stoppingToken); }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError("Document {JobId} could not be processed: {ErrorType}.",
                            ticket.JobId, exception.GetType().Name);
                    }
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError("Document receiver stopped unexpectedly: {ErrorType}.", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
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
