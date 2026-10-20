using System.Text.Json;

namespace DocIntel.Core.Review;

/// <summary>Isolated library/test review storage with atomic audit appends; deployed hosts use persistent implementations.</summary>
public sealed class InMemoryReviewStore : IReviewStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Tenant, string Id), ReviewSession> _sessions = new();
    private readonly Dictionary<(string Tenant, string Id), List<ReviewAuditEvent>> _events = new();
    private static ReviewSession Copy(ReviewSession session) =>
        JsonSerializer.Deserialize<ReviewSession>(JsonSerializer.SerializeToUtf8Bytes(session))!;

    public Task CreateAsync(ReviewSession session, ReviewAuditEvent audit, CancellationToken cancellationToken = default)
    {
        ReviewStoreValidation.Change(session, audit, 0);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (session.TenantId, session.Id);
            if (_sessions.ContainsKey(key)) throw new ReviewConflictException("The review already exists.");
            _sessions.Add(key, Copy(session));
            _events.Add(key, [audit]);
        }
        return Task.CompletedTask;
    }

    public Task<ReviewSession?> GetAsync(string tenantId, string reviewId, CancellationToken cancellationToken = default)
    {
        ReviewStoreValidation.Identity(tenantId, reviewId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_sessions.TryGetValue((tenantId, reviewId), out var session) ? Copy(session) : null);
    }

    public Task SaveAsync(ReviewSession session, long expectedVersion, ReviewAuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ReviewStoreValidation.Change(session, audit, expectedVersion);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (session.TenantId, session.Id);
            if (!_sessions.TryGetValue(key, out var previous) || previous.Version != expectedVersion ||
                previous.LastAuditHash != audit.PreviousHash)
                throw new ReviewConflictException("The review changed. Reload it before editing.");
            _sessions[key] = Copy(session);
            _events[key].Add(audit);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ReviewAuditEvent>> GetAuditAsync(
        string tenantId, string reviewId, CancellationToken cancellationToken = default)
    {
        ReviewStoreValidation.Identity(tenantId, reviewId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult<IReadOnlyList<ReviewAuditEvent>>(
            _events.TryGetValue((tenantId, reviewId), out var events) ? events.ToArray() : []);
    }
}
