using System.Security.Cryptography;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Storage;
using DocIntel.Core.Ocr;
using DocIntel.Core.Review;
using DocIntel.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Jobs;

/// <summary>Executes persisted document work under bounded, renewable, version-fenced ownership.</summary>
public sealed class DocumentJobProcessor(
    IJobStore jobs,
    IHarnessStore harnesses,
    IArtifactStore artifacts,
    DocumentSplittingWorkflow workflow,
    DocumentProcessingOptions options,
    ILogger<DocumentJobProcessor> logger,
    ITenantAccessResolver? access = null)
{
    public async Task ProcessAsync(JobQueueTicket ticket, CancellationToken stoppingToken = default)
    {
        await using var lease = await jobs.TryAcquireJobAsync(ticket.TenantId, ticket.JobId, stoppingToken);
        if (lease is null) return;
        var job = lease.Job;
        using var operation = AgentDiagnostics.Begin("document.run", logger,
            job.TenantId, job.JobId, job.AppliedHarnessVersion);
        if (job.ExecutionAttempts > 3)
        {
            job.Status = JobStatus.Failed;
            job.ErrorMessage = "Document processing was repeatedly interrupted. Review the job before resubmitting.";
            job.CompletedAtUtc = DateTimeOffset.UtcNow;
            await lease.SaveAsync(stoppingToken);
            return;
        }
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.OwnershipLostToken);
        execution.CancelAfter(TimeSpan.FromSeconds(options.MaxRunSeconds));
        async Task CheckAccessAsync(CancellationToken ct)
        {
            if (access is not null && !(await access.ResolveAsync(job.SubmittedBy, job.TenantId, ct)).CanProcess)
                throw new UnauthorizedAccessException("Document processing permission changed.");
        }
        try
        {
            job.Status = JobStatus.Running;
            job.ErrorMessage = null;
            job.CompletedAtUtc = null;
            job.PacketSummary = null;
            job.ExtractedEntities = [];
            job.DataExtractions = [];
            job.PromptColumnResults = [];
            job.ExecutionTrace = [];
            job.Segments = [];
            job.LayoutSha256 = null;
            job.ProcessingResultSha256 = null;
            job.ReviewId = null;
            job.ArtifactDocumentId = $"{job.JobId}-a{job.ExecutionAttempts}";
            await lease.SaveAsync(execution.Token);
            await CheckAccessAsync(execution.Token);
            var currentTenant = await harnesses.GetTenantAsync(job.TenantId, execution.Token)
                ?? throw new InvalidOperationException("The processing workspace is unavailable.");
            if (!currentTenant.IsActive) throw new InvalidOperationException("The processing workspace is inactive.");
            var tenant = job.ProcessingHarness ?? currentTenant;
            if (tenant.TenantId != job.TenantId || tenant.HarnessVersion != job.AppliedHarnessVersion)
                throw new InvalidDataException("The accepted processing configuration is unavailable or inconsistent.");
            var original = await artifacts.GetOriginalPdfAsync(job.TenantId, job.JobId, execution.Token)
                ?? throw new InvalidDataException("The persisted original is unavailable.");
            if (job.OriginalSha256 is null || !String.Equals(job.OriginalSha256,
                Convert.ToHexString(SHA256.HashData(original)), StringComparison.Ordinal))
                throw new InvalidDataException("The persisted original does not match the accepted source hash.");
            var result = await workflow.ExecuteAsync(new SplittingState
            {
                DocumentId = job.ArtifactDocumentId,
                FileName = job.FileName,
                Tenant = tenant,
                SourcePdfBytes = original
            }, async (state, ct) =>
            {
                await CheckAccessAsync(ct);
                Update(job, state);
                await lease.SaveAsync(ct);
            }, execution.Token);
            await CheckAccessAsync(execution.Token);
            execution.Token.ThrowIfCancellationRequested();
            Update(job, result);
            job.Status = JobStatus.Completed;
            job.CompletedAtUtc = DateTimeOffset.UtcNow;
            job.ProcessingResultSha256 = await artifacts.SaveProcessingResultAsync(job, execution.Token);
            if (job.ProcessingResultSha256 is not { Length: 64 } ||
                !job.ProcessingResultSha256.All(Char.IsAsciiHexDigit))
                throw new InvalidDataException("The completed processing snapshot was not acknowledged.");
            await CheckAccessAsync(execution.Token);
            await lease.SaveAsync(execution.Token);
            operation.Complete();
            return;
        }
        catch (Exception exception) when (lease.OwnershipLostToken.IsCancellationRequested)
        {
            operation.Cancel();
            logger.LogWarning("Document {JobId} lost processing ownership after {ErrorType}; no stale state was published.",
                job.JobId, exception.GetType().Name);
            return;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            operation.Cancel();
            job.Status = JobStatus.Queued;
            job.ErrorMessage = "Processing paused while the service restarts.";
            job.CompletedAtUtc = null;
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            operation.Cancel();
            job.Status = JobStatus.Failed;
            job.ErrorMessage = "Document processing exceeded its time limit.";
            job.CompletedAtUtc = DateTimeOffset.UtcNow;
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            var cause = exception.GetBaseException();
            logger.LogError("Document {JobId} failed with {ErrorType}; code {Code}; site {Site}.",
                job.JobId, cause.GetType().Name, cause.HResult & 0xffff,
                cause.TargetSite?.DeclaringType?.Name + "." + cause.TargetSite?.Name);
            job.Status = JobStatus.Failed;
            job.ErrorMessage = exception switch
            {
                UnauthorizedAccessException => "Document processing access was revoked.",
                OcrBudgetExceededException => "The monthly OCR page allowance is exhausted. An administrator must review the budget.",
                _ => "Document processing failed. Check the provider configuration or source PDF and retry."
            };
            job.CompletedAtUtc = DateTimeOffset.UtcNow;
        }
        using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        job.ProcessingResultSha256 = null;
        await lease.SaveAsync(persistence.Token);
    }

    private static void Update(DocumentJob job, SplittingState state)
    {
        job.TotalPages = state.Pages.Count;
        job.LayoutSha256 = state.LayoutSha256;
        job.StrategyUsed = state.StrategyUsed;
        job.ModelIdUsed = state.ModelIdUsed;
        job.HasDevelopmentResults = state.HasDevelopmentResults;
        job.PacketSummary = state.PacketSummary;
        job.ExtractedEntities = state.ExtractedEntities.ToList();
        job.DataExtractions = state.DataExtractions.ToList();
        job.PromptColumnResults = state.PromptColumnResults.ToList();
        job.ExecutionTrace = state.ExecutionTrace.ToList();
        job.Segments = state.Slices.ToList();
    }
}
