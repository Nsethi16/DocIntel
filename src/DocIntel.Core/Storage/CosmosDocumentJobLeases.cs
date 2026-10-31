using System.Net;
using DocIntel.Core.Jobs;
using Microsoft.Azure.Cosmos;

namespace DocIntel.Core.Storage;

public sealed partial class CosmosJobStore
{
    public async Task<IDocumentJobLease?> TryAcquireJobAsync(
        string tenantId, string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var stored = await GetStoredJobAsync(jobId, tenantId, cancellationToken);
        var now = _jobClock.GetUtcNow();
        if (stored is null || stored.Status is not (JobStatus.Queued or JobStatus.Running) ||
            (stored.ProcessingOwner is not null && stored.ProcessingLeaseUntilUtc > now))
            return null;
        var job = await HydrateJobAsync(stored, cancellationToken);
        var owner = Guid.NewGuid().ToString("N");
        job.ProcessingOwner = owner;
        job.ExecutionAttempts++;
        job.ArtifactDocumentId = $"{job.JobId}-a{job.ExecutionAttempts}";
        job.LayoutSha256 = null;
        job.ProcessingResultSha256 = null;
        job.ContentSnapshot = null;
        job.SearchTextIsPartial = false;
        job.SearchMetadataUnavailable = false;
        job.PacketSummary = null;
        job.ExtractedEntities = [];
        job.DataExtractions = [];
        job.PromptColumnResults = [];
        job.ExecutionTrace = [];
        job.Segments = [];
        job.ReviewId = null;
        var claim = await PrepareJobAsync(job, cancellationToken);
        try
        {
            var response = await WriteJobAsync(claim, RequireETag(job.ETag), cancellationToken, body =>
            {
                body.ProcessingOwner = owner;
                body.ProcessingLeaseUntilUtc = _jobClock.GetUtcNow().AddSeconds(60);
            });
            ApplyStoredState(job, claim.Job, RequireETag(response.ETag));
            claim.Job.ETag = job.ETag;
            ThrowIfDocumentLimit(claim);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return null;
        }
        var saved = CloneJob(claim.Job);

        bool OwnsJob() => job.ProcessingOwner == owner &&
            job.ProcessingLeaseUntilUtc is { } until && until > _jobClock.GetUtcNow();

        async Task<bool> ReplaceOwnedAsync(bool renewal, CancellationToken ct)
        {
            if (!OwnsJob()) return false;
            var prepared = renewal ? new PreparedJob(CloneJob(saved)) : await PrepareJobAsync(job, ct);
            if (!OwnsJob()) return false;
            try
            {
                var response = await WriteJobAsync(prepared, RequireETag(job.ETag), ct, body =>
                {
                    if (!OwnsJob()) throw new OperationCanceledException("Document ownership expired before publication.");
                    body.ProcessingOwner = owner;
                    body.ProcessingLeaseUntilUtc = _jobClock.GetUtcNow().AddSeconds(60);
                }, authorize: renewal ? null : AuthorizePublicationAsync);
                var etag = RequireETag(response.ETag);
                if (renewal && prepared.Failure is null)
                {
                    job.ETag = etag;
                    job.ProcessingLeaseUntilUtc = prepared.Job.ProcessingLeaseUntilUtc;
                }
                else ApplyStoredState(job, prepared.Job, etag);
                prepared.Job.ETag = etag;
                saved = CloneJob(prepared.Job);
                ThrowIfDocumentLimit(prepared);
                return true;
            }
            catch (CosmosException exception) when (exception.StatusCode is
                HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        async Task AuthorizePublicationAsync(DocumentJob body, CancellationToken ct)
        {
            if (body.Status is JobStatus.Failed or JobStatus.Deleted || _jobAccess is null) return;
            if (!(await _jobAccess.ResolveAsync(job.SubmittedBy, tenantId, ct)).CanProcess)
                throw new DocumentPublicationAccessException();
        }

        async Task ReleaseAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var latest = await GetStoredJobAsync(jobId, tenantId, deadline.Token);
            if (latest?.ProcessingOwner != owner) return;
            latest.ProcessingOwner = null;
            latest.ProcessingLeaseUntilUtc = null;
            try
            {
                await _container.ReplaceItemAsync(latest, jobId, new PartitionKey(tenantId),
                    new ItemRequestOptions { IfMatchEtag = RequireETag(latest.ETag) }, deadline.Token);
            }
            catch (CosmosException exception) when (exception.StatusCode is
                HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
            {
                // A replacement owner or administrator changed the record; never release that owner's claim.
            }
        }

        return new DocumentJobLease(job, ct => ReplaceOwnedAsync(false, ct),
            ct => ReplaceOwnedAsync(true, ct), ReleaseAsync, _jobLogger);
    }

    public async Task<IReadOnlyList<JobQueueTicket>> ListRecoverableJobsAsync(
        int maxCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxCount, 100);
        var query = new QueryDefinition("""
            SELECT TOP @count c.jobId, c.tenantId FROM c
            WHERE (c.status = @queued OR c.status = @running)
              AND (NOT IS_DEFINED(c.processingOwner) OR IS_NULL(c.processingOwner)
                OR NOT IS_DEFINED(c.processingLeaseUntilUtc) OR IS_NULL(c.processingLeaseUntilUtc)
                OR c.processingLeaseUntilUtc <= @now)
            ORDER BY c.createdAtUtc
            """).WithParameter("@count", maxCount)
            .WithParameter("@queued", nameof(JobStatus.Queued))
            .WithParameter("@running", nameof(JobStatus.Running))
            .WithParameter("@now", _jobClock.GetUtcNow());
        using var iterator = _container.GetItemQueryIterator<JobQueueTicket>(query,
            requestOptions: new QueryRequestOptions { MaxItemCount = maxCount });
        var tickets = new List<JobQueueTicket>();
        while (iterator.HasMoreResults && tickets.Count < maxCount)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            tickets.AddRange(page.Take(maxCount - tickets.Count));
        }
        return tickets;
    }
}
