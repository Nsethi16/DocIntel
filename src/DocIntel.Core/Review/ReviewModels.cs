using System.Text.Json.Serialization;
using DocIntel.Core.Authorization;
using DocIntel.Core.Evidence;

namespace DocIntel.Core.Review;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewStatus { Pending, InReview, AwaitingSecondReview, Approved, Rejected }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldReviewOutcome { Pending, Approved, Corrected, Rejected }

/// <summary>Explicit human review, never score-driven automatic approval for uncalibrated extractions.</summary>
public sealed class ReviewPolicy
{
    public bool Enabled { get; set; } = true;
    public bool RequiresDualReview { get; set; }
    public int DueHours { get; set; } = 24;

    public void Validate()
    {
        if (DueHours is < 1 or > 720) throw new ArgumentException("Review due hours must be between 1 and 720 UTC hours.");
    }
}

/// <summary>An original typed extraction plus a separate effective value and auditable human decision.</summary>
public sealed record ReviewField(
    string Id, string Label, string OriginalValue, string CurrentValue, int SliceIndex,
    FieldReviewOutcome Outcome, ConfidenceSignals? ConfidenceSignals, ValueProvenance? OriginalProvenance,
    ActorIdentity? ReviewedBy = null, DateTimeOffset? ReviewedAtUtc = null, string? Reason = null);

/// <summary>Versioned, source-bound review state; source extraction records are not overwritten by corrections.</summary>
public sealed record ReviewSession
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }
    public string Kind { get; init; } = "session";
    public required string JobId { get; init; }
    public required string SourceFingerprint { get; init; }
    public ReviewStatus Status { get; init; } = ReviewStatus.Pending;
    public IReadOnlyList<ReviewField> Fields { get; init; } = [];
    public bool RequiresDualReview { get; init; }
    public ActorIdentity? ClaimedBy { get; init; }
    public DateTimeOffset? ClaimUntilUtc { get; init; }
    public ActorIdentity? FirstReviewer { get; init; }
    public ActorIdentity? SecondReviewer { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset DueAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public long Version { get; init; } = 1;
    public string LastAuditHash { get; init; } = String.Empty;
    public bool HasDevelopmentActivity { get; init; }
    [JsonIgnore]
    public string? StoreETag { get; set; }
}

/// <summary>Append-only application audit evidence; protection against a privileged storage administrator is not claimed.</summary>
public sealed record ReviewAuditEvent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("tenantId")] string TenantId,
    string ReviewId, string JobId, long Sequence, string Action, string? FieldId,
    FieldReviewOutcome? Outcome, string? NewValue, string? Reason, ActorIdentity? Actor,
    DateTimeOffset AtUtc, string PreviousHash, string Hash)
{
    public string Kind => "audit";
}

/// <summary>Atomic session state plus immutable audit append under one tenant partition.</summary>
public interface IReviewStore
{
    Task CreateAsync(ReviewSession session, ReviewAuditEvent audit, CancellationToken cancellationToken = default);
    Task<ReviewSession?> GetAsync(string tenantId, string reviewId, CancellationToken cancellationToken = default);
    Task SaveAsync(ReviewSession session, long expectedVersion, ReviewAuditEvent audit,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReviewAuditEvent>> GetAuditAsync(
        string tenantId, string reviewId, CancellationToken cancellationToken = default);
}

/// <summary>Signals a stale edit, incompatible review phase, or another active reviewer claim.</summary>
public sealed class ReviewConflictException(string message) : InvalidOperationException(message);
