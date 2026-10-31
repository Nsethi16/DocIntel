using DocIntel.Core.Authorization;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Jobs;
using DocIntel.Core.Harness;
using DocIntel.Core.Review;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Channels;

/// <summary>Separately leases external delivery; uncertain sends never replay automatically.</summary>
public sealed class ChannelDeliveryProcessor(
    IEnumerable<IChannelTransport> transports, ILogger<ChannelDeliveryProcessor> logger,
    ITenantAccessResolver? access = null, IHarnessStore? harness = null,
    IJobStore? jobs = null, ReviewService? reviews = null)
{
    public async Task ProcessAsync(IAskAiRunLease lease, CancellationToken cancellationToken = default)
    {
        var run = lease.Run;
        var target = run.ChannelDelivery;
        if (run.Status != JobStatus.Completed || target is null ||
            target.Status is not (ChannelDeliveryStatus.Pending or ChannelDeliveryStatus.Sending)) return;
        if (target.Status == ChannelDeliveryStatus.Sending)
        {
            target.Status = ChannelDeliveryStatus.OutcomeUnknown;
            target.ErrorCode = "InterruptedSendRequiresReview";
            await lease.SaveAsync(cancellationToken);
            logger.LogWarning("Channel delivery for run {RunId} was interrupted after send began; no resend was attempted.", run.RunId);
            return;
        }
        var transport = transports.SingleOrDefault(item => item.Name == target.Transport);
        if (transport is null || run.Actor is null || access is null || harness is null ||
            String.IsNullOrWhiteSpace(run.Answer))
        {
            target.Status = ChannelDeliveryStatus.Failed;
            target.ErrorCode = "DeliveryConfigurationIncomplete";
            await lease.SaveAsync(cancellationToken);
            return;
        }
        var tenant = await harness.GetTenantAsync(run.TenantId, cancellationToken);
        if (tenant is not { IsActive: true } ||
            !tenant.Capabilities.TryGetValue(FeatureHarness.AskAiKey, out var feature) || !feature.Enabled)
        {
            target.Status = ChannelDeliveryStatus.Failed;
            target.ErrorCode = "DeliveryTenantDisabled";
            await lease.SaveAsync(cancellationToken);
            return;
        }
        if ((target.Transport == "Teams" && !tenant.Agent.TeamsEnabled) ||
            (target.Transport == "Email" && !tenant.Agent.EmailEnabled))
        {
            target.Status = ChannelDeliveryStatus.Failed;
            target.ErrorCode = "DeliveryChannelDisabled";
            await lease.SaveAsync(cancellationToken);
            return;
        }
        var scope = await access.ResolveAsync(run.Actor, run.TenantId, cancellationToken);
        if (!scope.CanRead || target.AccessFingerprint != ChannelDelivery.GetAccessFingerprint(scope) ||
            run.Citations.Any(citation => !scope.CanReadDocument(citation.JobId)))
        {
            target.Status = ChannelDeliveryStatus.Failed;
            target.ErrorCode = "DeliveryAccessRevoked";
            await lease.SaveAsync(cancellationToken);
            return;
        }
        foreach (var citation in run.Citations.Where(citation => citation.EvidenceKind != "OriginalSourceText"))
        {
            if (jobs is null || reviews is null)
                throw new InvalidOperationException("Reviewed answer delivery requires the review services.");
            var job = await jobs.GetJobAsync(citation.JobId, cancellationToken, run.TenantId);
            ReviewSession? current;
            try
            {
                current = job is null ? null : await reviews.GetAsync(job, cancellationToken);
            }
            catch (ReviewConflictException)
            {
                current = null;
            }
            if (current is null || current.Version != citation.ReviewVersion ||
                citation.EvidenceKind == "HumanReviewedField" && current.Status != ReviewStatus.Approved)
            {
                target.Status = ChannelDeliveryStatus.Failed;
                target.ErrorCode = "DeliveryEvidenceChanged";
                await lease.SaveAsync(cancellationToken);
                return;
            }
        }
        if (!await ValidateSourcesAsync(lease, target, scope, cancellationToken)) return;
        using var send = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.OwnershipLostToken);
        send.CancelAfter(TimeSpan.FromSeconds(90));
        target.Status = ChannelDeliveryStatus.Sending;
        target.AttemptedAtUtc = DateTimeOffset.UtcNow;
        await lease.SaveAsync(send.Token);
        // Persisting the send fence can race reprocessing; recheck before external I/O.
        if (!await ValidateSourcesAsync(lease, target, scope, send.Token)) return;
        try
        {
            await transport.SendAsync(run, target, send.Token);
            target.Status = ChannelDeliveryStatus.Sent;
            target.SentAtUtc = DateTimeOffset.UtcNow;
            target.ErrorCode = null;
        }
        catch (Exception exception)
        {
            target.Status = ChannelDeliveryStatus.OutcomeUnknown;
            target.ErrorCode = "SendOutcomeRequiresReview";
            logger.LogWarning("Delivery for run {RunId} has an uncertain external outcome: {ErrorType}. No automatic resend.",
                run.RunId, exception.GetType().Name);
        }
        if (lease.OwnershipLostToken.IsCancellationRequested) return;
        using var persist = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await lease.SaveAsync(persist.Token);
    }

    private async Task<bool> ValidateSourcesAsync(
        IAskAiRunLease lease, ChannelDelivery target, DocumentAccessScope scope, CancellationToken cancellationToken)
    {
        try
        {
            await AskAiSavedOutputValidator.ValidateAsync(lease.Run, jobs, scope, cancellationToken);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            target.ErrorCode = "DeliveryAccessRevoked";
        }
        catch (AskAiSourceChangedException)
        {
            target.ErrorCode = "DeliveryEvidenceChanged";
        }
        target.Status = ChannelDeliveryStatus.Failed;
        await lease.SaveAsync(cancellationToken);
        return false;
    }
}
