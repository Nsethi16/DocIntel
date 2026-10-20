using DocIntel.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Features.AskAi;

/// <summary>Shared request checks for the web and channel adapters.</summary>
public static class AskAiRequestValidator
{
    public static string? GetError(string? question, string? callbackUrl = null)
    {
        if (String.IsNullOrWhiteSpace(question))
        {
            return "Question is required.";
        }
        if (question.Length > 8_000)
        {
            return "Ask AI questions must not exceed 8,000 characters.";
        }
        if (!String.IsNullOrWhiteSpace(callbackUrl))
        {
            return "Callback delivery is not enabled. Poll the run endpoint until approved destinations are configured.";
        }
        return null;
    }
}

/// <summary>Bounds the execution time of one dequeued run, independently of an HTTP connection.</summary>
public sealed class AskAiExecutionOptions
{
    public const string SectionName = "AskAi";

    public int MaxRunSeconds { get; set; } = 600;

    public string? RunStorageDirectory { get; set; }

    public void Validate()
    {
        if (MaxRunSeconds is < 1 or > 3_600)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRunSeconds),
                "Ask AI execution time must be between 1 and 3,600 seconds.");
        }
    }
}

/// <summary>
/// Persists run transitions and safe failure messages. Scheduling remains the responsibility of the queue.
/// </summary>
public sealed class AskAiRunProcessor(
    IAskAiRunStore runStore,
    AskAiOrchestrator orchestrator,
    AskAiExecutionOptions options,
    ILogger<AskAiRunProcessor> logger)
{
    public async Task<AskAiRun> ExecuteInlineAsync(AskAiRun run, CancellationToken cancellationToken = default)
    {
        await runStore.UpsertRunAsync(run, cancellationToken);
        await ProcessAsync(new AskAiQueueTicket(run.RunId, run.TenantId), cancellationToken);
        using var waitToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitToken.CancelAfter(TimeSpan.FromSeconds(options.MaxRunSeconds + 10));
        while (true)
        {
            var snapshot = await runStore.GetRunAsync(run.TenantId, run.RunId, waitToken.Token)
                ?? throw new InvalidOperationException("The Ask AI run could not be loaded after execution.");
            if (snapshot.Status is JobStatus.Completed or JobStatus.Failed)
            {
                return snapshot;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), waitToken.Token);
        }
    }

    public async Task ProcessAsync(AskAiQueueTicket ticket, CancellationToken stoppingToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        options.Validate();
        await using var lease = await runStore.TryAcquireAsync(ticket.TenantId, ticket.RunId, stoppingToken);
        if (lease is null)
        {
            logger.LogDebug("Ask AI ticket {RunId} is absent or already owned.", ticket.RunId);
            return;
        }
        var run = lease.Run;
        if (run.Status is JobStatus.Completed or JobStatus.Failed)
        {
            logger.LogDebug("Skipping Ask AI ticket {RunId} in state {Status}.", ticket.RunId, run.Status);
            return;
        }
        if ((run.Status == JobStatus.Running || run.ExecutionAttempts > 0) && !orchestrator.CanAutomaticallyReplay)
        {
            run.Status = JobStatus.Failed;
            run.ErrorCode = "RecoveryRequiresReview";
            run.ErrorMessage = "This run was interrupted with extension tools enabled. Review it before resubmitting.";
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            ClearAnswer(run);
            await lease.SaveAsync(stoppingToken);
            return;
        }
        if (run.ExecutionAttempts >= 3)
        {
            run.Status = JobStatus.Failed;
            run.ErrorCode = "RecoveryLimitExceeded";
            run.ErrorMessage = "This run was repeatedly interrupted. Please review it before submitting again.";
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            ClearAnswer(run);
            await lease.SaveAsync(stoppingToken);
            return;
        }
        run.ExecutionAttempts++;

        using var executionToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        executionToken.CancelAfter(TimeSpan.FromSeconds(options.MaxRunSeconds));
        try
        {
            run.Status = JobStatus.Running;
            run.StartedAtUtc = DateTimeOffset.UtcNow;
            run.ErrorCode = null;
            run.ErrorMessage = null;
            run.Answer = null;
            run.Citations.Clear();
            run.ExecutedCosmosQueries.Clear();
            run.Steps.Clear();
            run.DocumentsEvaluated = 0;
            run.CompletedAtUtc = null;
            await lease.SaveAsync(executionToken.Token);
            await orchestrator.ExecuteRunAsync(
                run, (_, ct) => lease.SaveAsync(ct), executionToken.Token);
            executionToken.Token.ThrowIfCancellationRequested();
            await lease.SaveAsync(executionToken.Token);
            return;
        }
        catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
        {
            run.ErrorCode = stoppingToken.IsCancellationRequested ? "Interrupted" : "TimedOut";
            run.ErrorMessage = stoppingToken.IsCancellationRequested
                ? "Ask AI processing paused while the service restarts. The same run will be checked again."
                : "This question exceeded the processing time limit. Try a more focused question.";
            logger.LogWarning("Ask AI run {RunId} stopped with {ErrorCode}.", run.RunId, run.ErrorCode);
        }
        catch (UnauthorizedAccessException)
        {
            run.ErrorCode = "AccessRevoked";
            run.ErrorMessage = "Your access changed while this question was processing. No answer was returned.";
            logger.LogWarning("Access changed before Ask AI run {RunId} completed.", run.RunId);
        }
        catch (Exception exception)
        {
            run.ErrorCode = "ExecutionFailed";
            run.ErrorMessage = "Ask AI could not complete this request. Check the model configuration or try again.";
            logger.LogError("Ask AI run {RunId} failed with {ErrorType}; root cause {CauseType}.",
                run.RunId, exception.GetType().Name, exception.GetBaseException().GetType().Name);
        }

        run.Status = stoppingToken.IsCancellationRequested ? JobStatus.Queued : JobStatus.Failed;
        run.CompletedAtUtc = run.Status == JobStatus.Failed ? DateTimeOffset.UtcNow : null;
        ClearAnswer(run);
        using var persistenceToken = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await lease.SaveAsync(persistenceToken.Token);
    }

    private static void ClearAnswer(AskAiRun run)
    {
        run.Answer = null;
        run.Citations.Clear();
        run.EmailHtmlBody = null;
        run.TeamsAdaptiveCardJson = null;
    }
}
