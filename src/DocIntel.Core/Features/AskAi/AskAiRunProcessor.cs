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
        return await runStore.GetRunAsync(run.TenantId, run.RunId, cancellationToken)
            ?? throw new InvalidOperationException("The Ask AI run could not be loaded after execution.");
    }

    public async Task ProcessAsync(AskAiQueueTicket ticket, CancellationToken stoppingToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        options.Validate();
        var run = await runStore.GetRunAsync(ticket.TenantId, ticket.RunId, stoppingToken);
        if (run is null)
        {
            logger.LogWarning("Ask AI ticket {RunId} has no run in its tenant.", ticket.RunId);
            return;
        }
        if (run.Status != JobStatus.Queued)
        {
            logger.LogDebug("Skipping Ask AI ticket {RunId} in state {Status}.", ticket.RunId, run.Status);
            return;
        }

        using var executionToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        executionToken.CancelAfter(TimeSpan.FromSeconds(options.MaxRunSeconds));
        try
        {
            run.Status = JobStatus.Running;
            run.StartedAtUtc = DateTimeOffset.UtcNow;
            await runStore.UpsertRunAsync(run, executionToken.Token);
            await orchestrator.ExecuteRunAsync(
                run, (progress, ct) => runStore.UpsertRunAsync(progress, ct), executionToken.Token);
            executionToken.Token.ThrowIfCancellationRequested();
            await runStore.UpsertRunAsync(run, executionToken.Token);
            return;
        }
        catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
        {
            run.ErrorCode = stoppingToken.IsCancellationRequested ? "Interrupted" : "TimedOut";
            run.ErrorMessage = stoppingToken.IsCancellationRequested
                ? "Ask AI processing stopped before completion. Please submit the question again."
                : "This question exceeded the processing time limit. Try a more focused question.";
            logger.LogWarning("Ask AI run {RunId} stopped with {ErrorCode}.", run.RunId, run.ErrorCode);
        }
        catch (Exception exception)
        {
            run.ErrorCode = "ExecutionFailed";
            run.ErrorMessage = "Ask AI could not complete this request. Check the model configuration or try again.";
            logger.LogError(exception, "Ask AI run {RunId} failed.", run.RunId);
        }

        run.Status = JobStatus.Failed;
        run.CompletedAtUtc = DateTimeOffset.UtcNow;
        run.Answer = null;
        run.Citations.Clear();
        run.EmailHtmlBody = null;
        run.TeamsAdaptiveCardJson = null;
        using var persistenceToken = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await runStore.UpsertRunAsync(run, persistenceToken.Token);
    }
}
