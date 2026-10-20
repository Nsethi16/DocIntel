using System.Net;
using System.Text.Json;
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
        var job = await GetJobAsync(jobId, cancellationToken, tenantId);
        var now = _jobClock.GetUtcNow();
        if (job is null || job.Status is JobStatus.Completed or JobStatus.Failed ||
            (job.ProcessingOwner is not null && job.ProcessingLeaseUntilUtc > now))
            return null;
        var owner = Guid.NewGuid().ToString("N");
        job.ProcessingOwner = owner;
        job.ProcessingLeaseUntilUtc = now.AddSeconds(60);
        job.ExecutionAttempts++;
        try
        {
            var response = await _container.ReplaceItemAsync(job, jobId, new PartitionKey(tenantId),
                new ItemRequestOptions { IfMatchEtag = RequireETag(job.ETag) }, cancellationToken);
            job.ETag = RequireETag(response.ETag);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return null;
        }
        var saved = JsonSerializer.SerializeToUtf8Bytes(job);

        async Task<bool> ReplaceOwnedAsync(bool renewal, CancellationToken ct)
        {
            if (job.ProcessingOwner != owner || job.ProcessingLeaseUntilUtc <= _jobClock.GetUtcNow())
                return false;
            var body = renewal ? JsonSerializer.Deserialize<DocumentJob>(saved)! : job;
            body.ProcessingOwner = owner;
            body.ProcessingLeaseUntilUtc = _jobClock.GetUtcNow().AddSeconds(60);
            try
            {
                var response = await _container.ReplaceItemAsync(body, jobId, new PartitionKey(tenantId),
                    new ItemRequestOptions { IfMatchEtag = RequireETag(job.ETag) }, ct);
                job.ETag = RequireETag(response.ETag);
                job.ProcessingLeaseUntilUtc = body.ProcessingLeaseUntilUtc;
                body.ETag = response.ETag;
                saved = JsonSerializer.SerializeToUtf8Bytes(body);
                return true;
            }
            catch (CosmosException exception) when (exception.StatusCode is
                HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        async Task ReleaseAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var latest = await GetJobAsync(jobId, deadline.Token, tenantId);
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
