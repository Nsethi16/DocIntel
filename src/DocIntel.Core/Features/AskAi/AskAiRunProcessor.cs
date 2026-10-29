using DocIntel.Core.Jobs;
using DocIntel.Core.Channels;
using DocIntel.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Features.AskAi;

/// <summary>Shared request checks for the web and channel adapters.</summary>
public static class AskAiRequestValidator
{
    public static string? GetError(string? question, string? callbackUrl = null, string? previousRunId = null)
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
        if (previousRunId is not null && (previousRunId.Length is < 1 or > 128 ||
            previousRunId.Any(character => !Char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))))
            return "The prior run identifier is invalid.";
        return null;
    }
}

/// <summary>Bounds the execution time of one dequeued run, independently of an HTTP connection.</summary>
public sealed class AskAiExecutionOptions
{
    public const string SectionName = "AskAi";

    public int MaxRunSeconds { get; set; } = 600;

    public string? RunStorageDirectory { get; set; }

    public string RunContainerName { get; set; } = "ask-ai-runs";

    public string DispatchQueueName { get; set; } = "ask-ai";

    public string PoisonQueueName { get; set; } = "ask-ai-poison";

    public int RecoveryPollSeconds { get; set; } = 5;

    public void Validate()
    {
        if (MaxRunSeconds is < 1 or > 3_600)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRunSeconds),
                "Ask AI execution time must be between 1 and 3,600 seconds.");
        }
        if (RecoveryPollSeconds is < 1 or > 300 ||
            new[] { RunContainerName, DispatchQueueName, PoisonQueueName }.Any(name =>
                String.IsNullOrWhiteSpace(name) || name.Length is < 3 or > 63 ||
                name[0] == '-' || name[^1] == '-' || name.Contains("--", StringComparison.Ordinal) ||
                name.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))))
        {
            throw new ArgumentException("Invalid Ask AI storage names or recovery interval.");
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
    ILogger<AskAiRunProcessor> logger,
    ChannelDeliveryProcessor? delivery = null)
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
        if (run.Status == JobStatus.AwaitingConfirmation) return;
        if (run.Status == JobStatus.Completed && run.ChannelDelivery is not null)
        {
            if (delivery is null)
                throw new InvalidOperationException("Trusted channel delivery requires a configured delivery processor.");
            await delivery.ProcessAsync(lease, stoppingToken);
            return;
        }
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
        using var operation = AgentDiagnostics.Begin("ask.run", logger, run.TenantId, run.RunId);

        using var executionToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.OwnershipLostToken);
        executionToken.CancelAfter(TimeSpan.FromSeconds(options.MaxRunSeconds));
        try
        {
            run.Status = JobStatus.Running;
            run.StartedAtUtc = DateTimeOffset.UtcNow;
            run.ErrorCode = null;
            run.ProviderFailure = null;
            run.ErrorMessage = null;
            run.Answer = null;
            run.Citations.Clear();
            run.Analyses.Clear();
            run.SourceVersions.Clear();
            run.ExecutedCosmosQueries.Clear();
            run.Steps.Clear();
            run.DocumentsEvaluated = 0;
            run.ReferencedJobIds.Clear();
            run.CompletedAtUtc = null;
            await lease.SaveAsync(executionToken.Token);
            await orchestrator.ExecuteRunAsync(
                run, (_, ct) => lease.SaveAsync(ct), executionToken.Token);
            executionToken.Token.ThrowIfCancellationRequested();
            await lease.SaveAsync(executionToken.Token);
            operation.Complete();
            return;
        }
        catch (OperationCanceledException) when (lease.OwnershipLostToken.IsCancellationRequested)
        {
            operation.Cancel();
            logger.LogWarning("Ask AI run {RunId} lost ownership; its replacement owner must recover it.", run.RunId);
            return;
        }
        catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
        {
            operation.Cancel();
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
            operation.Fail(exception);
            run.ErrorCode = AskAiFailureDiagnostics.GetCode(exception);
            run.ProviderFailure = AskAiProviderFailure.FromException(exception);
            run.ErrorMessage = AskAiFailureDiagnostics.GetMessage(run.ErrorCode);
            var cause = exception.GetBaseException();
            logger.LogError("Ask AI run {RunId} failed with {ErrorType}; root cause {CauseType}; code {Code}; site {Site}. " +
                "Failure code {FailureCode}; searches {Searches}; inspections {Inspections}; matched documents {Documents}.",
                run.RunId, exception.GetType().Name, cause.GetType().Name, cause.HResult & 0xffff,
                cause.TargetSite?.DeclaringType?.Name + "." + cause.TargetSite?.Name, run.ErrorCode,
                run.Steps.Count(step => step.StepName == "search_documents"),
                run.Steps.Count(step => step.StepName == "inspect_document_slice"), run.DocumentsEvaluated);
            if (run.ProviderFailure is { } provider)
                logger.LogError("Ask AI provider failure for {RunId}: HTTP {ProviderStatus}; code {ProviderCode}; " +
                    "parameter {ProviderParameter}; schema keyword {SchemaKeyword}.",
                    run.RunId, provider.HttpStatus, provider.Code, provider.Parameter, provider.SchemaKeyword);
            if (AskAiDataFailure.FromException(exception) is { } data)
                logger.LogError("Ask AI data failure for {RunId}: HTTP {DataStatus}; substatus {DataSubStatus}.",
                    run.RunId, data.HttpStatus, data.SubStatus);
        }

        run.Status = stoppingToken.IsCancellationRequested ? JobStatus.Queued : JobStatus.Failed;
        run.CompletedAtUtc = run.Status == JobStatus.Failed ? DateTimeOffset.UtcNow : null;
        ClearAnswer(run);
        using var persistenceToken = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await lease.SaveAsync(persistenceToken.Token);
    }

    private static void ClearAnswer(AskAiRun run)
    {
        if (run.Status == JobStatus.Failed && run.ChannelDelivery is not null)
        {
            run.ChannelDelivery.Status = ChannelDeliveryStatus.Failed;
            run.ChannelDelivery.ErrorCode = "QuestionExecutionFailed";
        }
        run.Answer = null;
        run.Citations.Clear();
        run.Analyses.Clear();
        run.SourceVersions.Clear();
        run.EmailHtmlBody = null;
        run.TeamsAdaptiveCardJson = null;
    }
}
