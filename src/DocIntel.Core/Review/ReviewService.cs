using DocIntel.Core.Authorization;
using DocIntel.Core.Jobs;

namespace DocIntel.Core.Review;

/// <summary>Coordinates source-bound human review changes and atomic application audit appends.</summary>
public sealed class ReviewService(IReviewStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<string> CreateAsync(
        DocumentJob job, ReviewPolicy policy, CancellationToken cancellationToken = default)
    {
        if (job.Status != JobStatus.Completed || job.OriginalSha256 is not { Length: 64 } ||
            !job.OriginalSha256.All(Char.IsAsciiHexDigit))
            throw new ReviewConflictException("Complete processing with a persisted original before starting human review.");
        policy.Validate();
        var now = _clock.GetUtcNow();
        var session = ReviewEngine.Create(job, policy, now);
        var audit = ReviewEngine.Audit(session, "Created", null, now);
        session = session with { LastAuditHash = audit.Hash };
        await store.CreateAsync(session, audit, cancellationToken);
        return session.Id;
    }

    public async Task<ReviewSession?> GetAsync(DocumentJob job, CancellationToken cancellationToken = default)
    {
        if (job.ReviewId is null) return null;
        var session = await store.GetAsync(job.TenantId, job.ReviewId, cancellationToken);
        if (session is null || session.JobId != job.JobId || session.SourceFingerprint != ReviewEngine.Fingerprint(job))
            throw new ReviewConflictException("The review snapshot no longer matches the current extraction.");
        return session;
    }

    public async Task<ReviewSession> ChangeAsync(DocumentJob job, ActorIdentity actor, long expectedVersion,
        string action, string? fieldId = null, FieldReviewOutcome? outcome = null, string? value = null,
        string? reason = null, bool development = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (action != "field" && (fieldId is not null || outcome is not null || value is not null))
            throw new ArgumentException("Field arguments are only accepted for a field decision.");
        if (reason?.Length > 2_000 || reason?.Contains('\0') == true)
            throw new ArgumentException("A bounded review reason is required.");
        var current = await GetAsync(job, cancellationToken)
            ?? throw new ReviewConflictException("This document has no review session.");
        if (current.Version != expectedVersion)
            throw new ReviewConflictException("The review changed. Reload it before editing.");
        if (current.Version >= ReviewStoreValidation.MaximumVersions ||
            current.Version >= ReviewStoreValidation.MaximumVersions - 1 && action != "reject" ||
            current.Version >= ReviewStoreValidation.MaximumVersions - 2 && action is not ("claim" or "reject") ||
            current.Version >= ReviewStoreValidation.MaximumVersions - 10 && action == "field")
            throw new ReviewConflictException("The field-change audit limit was reached. Finalize or reject this review; do not lose the audit history.");
        var now = _clock.GetUtcNow();
        ReviewSession changed;
        switch (action)
        {
            case "claim":
                changed = ReviewEngine.Claim(current, actor, now);
                break;
            case "field":
                changed = ReviewEngine.Decide(current, actor, fieldId ?? String.Empty,
                    outcome ?? throw new ArgumentException("A field decision is required."), value, reason, now);
                break;
            case "release":
                ReviewEngine.Owns(current, actor, now);
                changed = current with
                {
                    ClaimedBy = null,
                    ClaimUntilUtc = null,
                    Status = current.Status == ReviewStatus.AwaitingSecondReview ? current.Status : ReviewStatus.Pending
                };
                break;
            case "submit":
                changed = ReviewEngine.Submit(current, actor, now);
                break;
            case "reject":
                var expiredTerminalClaim = current.Version == ReviewStoreValidation.MaximumVersions - 1 &&
                    current.Status is ReviewStatus.Pending or ReviewStatus.InReview or ReviewStatus.AwaitingSecondReview &&
                    (current.ClaimedBy is null || current.ClaimUntilUtc is null || current.ClaimUntilUtc <= now);
                if (!expiredTerminalClaim) ReviewEngine.Owns(current, actor, now);
                if (String.IsNullOrWhiteSpace(reason) || reason.Length > 2_000 || reason.Contains('\0'))
                    throw new ArgumentException("A bounded rejection reason is required.");
                changed = current with { Status = ReviewStatus.Rejected, ClaimedBy = null, ClaimUntilUtc = null };
                break;
            default: throw new ArgumentException("The review action is not supported.");
        }
        changed = changed with
        {
            Version = current.Version + 1,
            UpdatedAtUtc = now,
            HasDevelopmentActivity = current.HasDevelopmentActivity || development
        };
        var audit = ReviewEngine.Audit(changed, action, actor, now, fieldId, outcome, value, reason);
        changed = changed with { LastAuditHash = audit.Hash };
        await store.SaveAsync(changed, current.Version, audit, cancellationToken);
        return changed;
    }
}
