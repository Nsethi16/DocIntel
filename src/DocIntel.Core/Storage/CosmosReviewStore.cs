using System.Net;
using DocIntel.Core.Review;
using Microsoft.Azure.Cosmos;

namespace DocIntel.Core.Storage;

/// <summary>Atomic versioned review state and append-only application audit records in one tenant partition.</summary>
public sealed class CosmosReviewStore(Container container) : IReviewStore
{
    public async Task CheckReadinessAsync(CancellationToken cancellationToken = default) =>
        await container.ReadContainerAsync(cancellationToken: cancellationToken);

    public async Task CreateAsync(ReviewSession session, ReviewAuditEvent audit, CancellationToken cancellationToken = default)
    {
        ReviewStoreValidation.Change(session, audit, 0);
        using var response = await container.CreateTransactionalBatch(new PartitionKey(session.TenantId))
            .CreateItem(session).CreateItem(audit).ExecuteAsync(cancellationToken);
        EnsureSuccess(response);
    }

    public async Task<ReviewSession?> GetAsync(
        string tenantId, string reviewId, CancellationToken cancellationToken = default)
    {
        ReviewStoreValidation.Identity(tenantId, reviewId);
        try
        {
            var response = await container.ReadItemAsync<ReviewSession>(
                reviewId, new PartitionKey(tenantId), cancellationToken: cancellationToken);
            var session = response.Resource;
            if (session is null || session.TenantId != tenantId || session.Id != reviewId || session.Kind != "session")
                throw new InvalidDataException("The review identity does not match its tenant partition.");
            ReviewStoreValidation.Stored(session, tenantId, reviewId);
            session.StoreETag = SpecificVersion(response.ETag);
            return session;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task SaveAsync(ReviewSession session, long expectedVersion, ReviewAuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ReviewStoreValidation.Change(session, audit, expectedVersion);
        var current = await GetAsync(session.TenantId, session.Id, cancellationToken);
        if (current is null || current.Version != expectedVersion || current.LastAuditHash != audit.PreviousHash)
            throw new ReviewConflictException("The review changed. Reload it before editing.");
        using var response = await container.CreateTransactionalBatch(new PartitionKey(session.TenantId))
            .ReplaceItem(session.Id, session,
                new TransactionalBatchItemRequestOptions { IfMatchEtag = SpecificVersion(current.StoreETag) })
            .CreateItem(audit).ExecuteAsync(cancellationToken);
        EnsureSuccess(response);
    }

    public async Task<IReadOnlyList<ReviewAuditEvent>> GetAuditAsync(
        string tenantId, string reviewId, CancellationToken cancellationToken = default)
    {
        ReviewStoreValidation.Identity(tenantId, reviewId);
        var query = new QueryDefinition("""
            SELECT TOP 10000 * FROM c WHERE c.tenantId=@tenant AND c.reviewId=@review AND c.kind=@kind ORDER BY c.sequence
            """).WithParameter("@tenant", tenantId).WithParameter("@review", reviewId).WithParameter("@kind", "audit");
        using var iterator = container.GetItemQueryIterator<ReviewAuditEvent>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId), MaxItemCount = 100 });
        var result = new List<ReviewAuditEvent>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            if (page.Any(entry => entry.TenantId != tenantId || entry.ReviewId != reviewId))
                throw new InvalidDataException("The review audit identity does not match its tenant partition.");
            result.AddRange(page);
        }
        return result;
    }

    private static string SpecificVersion(string? version) =>
        !String.IsNullOrWhiteSpace(version) && version != "*" ? version :
            throw new InvalidDataException("Review writes require a specific storage version.");

    private static void EnsureSuccess(TransactionalBatchResponse response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed ||
            response.Any(operation => operation.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed))
            throw new ReviewConflictException("The review changed. Reload it before editing.");
        throw new CosmosException("Review storage rejected the atomic state/audit transaction.",
            response.StatusCode, 0, response.ActivityId, response.RequestCharge);
    }
}
