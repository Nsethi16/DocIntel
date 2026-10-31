using System.Security.Cryptography;
using System.Text.Json;
using DocIntel.Core.Authorization;
using DocIntel.Core.Evidence;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Jobs;

namespace DocIntel.Core.Review;

/// <summary>Pure source snapshot and human-decision rules, with no automatic approval or implicit correction of model data.</summary>
public static class ReviewEngine
{
    public static IReadOnlyList<ReviewField> Fields(DocumentJob job)
    {
        var result = EnumerateFields(job).Take(2_001).ToList();
        if (result.Count > 2_000 || result.Select(field => field.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidDataException("The review field count or source field identities exceed the supported limits.");
        return result;
    }

    /// <summary>Enumerates complete source fields without imposing the retired human-review field limit.</summary>
    public static IEnumerable<ReviewField> EnumerateFields(DocumentJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        foreach (var slice in job.Segments)
        {
            for (var index = 0; index < slice.ExtractedEntities.Count; index++)
            {
                var entity = slice.ExtractedEntities[index];
                yield return new ReviewField($"entity:{slice.SegmentIndex}:{index}", entity.Name,
                    entity.Value, entity.Value, slice.SegmentIndex, FieldReviewOutcome.Pending,
                    entity.ConfidenceSignals, entity.Provenance);
            }
            if (slice.DataExtraction is not { } data) continue;
            ReviewField Field(string id, ExtractedFieldValue field) => new(id, field.FieldName,
                field.FieldValue, field.FieldValue, slice.SegmentIndex, FieldReviewOutcome.Pending,
                field.ConfidenceSignals, field.Provenance);
            for (var index = 0; index < data.Fields.Count; index++)
                yield return Field($"field:{slice.SegmentIndex}:{index}", data.Fields[index]);
            for (var table = 0; table < data.Tables.Count; table++)
                for (var row = 0; row < data.Tables[table].Rows.Count; row++)
                    for (var column = 0; column < data.Tables[table].Rows[row].Fields.Count; column++)
                        yield return Field($"cell:{slice.SegmentIndex}:{table}:{row}:{column}",
                            data.Tables[table].Rows[row].Fields[column]);
        }
    }

    public static string Fingerprint(DocumentJob job) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            job.JobId,
            job.TenantId,
            job.OriginalSha256,
            job.LayoutSha256,
            job.AppliedHarnessVersion,
            fields = Fields(job).Select(field => new { field.Id, field.Label, field.OriginalValue, field.SliceIndex })
        })));

    public static ReviewSession Create(DocumentJob job, ReviewPolicy policy, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        JobId = job.JobId,
        TenantId = job.TenantId,
        SourceFingerprint = Fingerprint(job),
        Fields = Fields(job),
        RequiresDualReview = policy.RequiresDualReview,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
        DueAtUtc = now.AddHours(policy.DueHours)
    };

    public static ReviewSession Claim(ReviewSession session, ActorIdentity actor, DateTimeOffset now)
    {
        if (session.Status is ReviewStatus.Approved or ReviewStatus.Rejected)
            throw new ReviewConflictException("The review is already final.");
        if (session.ClaimedBy is not null && session.ClaimedBy != actor && session.ClaimUntilUtc > now)
            throw new ReviewConflictException("Another reviewer holds the active claim.");
        if (session.Status == ReviewStatus.AwaitingSecondReview && session.FirstReviewer == actor)
            throw new ReviewConflictException("A different verified reviewer must perform the second review.");
        return session with
        {
            ClaimedBy = actor,
            ClaimUntilUtc = now.AddMinutes(15),
            Status = session.Status == ReviewStatus.AwaitingSecondReview ? session.Status : ReviewStatus.InReview
        };
    }

    public static ReviewSession Decide(ReviewSession session, ActorIdentity actor, string fieldId,
        FieldReviewOutcome outcome, string? value, string? reason, DateTimeOffset now)
    {
        Owns(session, actor, now);
        if (session.Status == ReviewStatus.AwaitingSecondReview)
            throw new ReviewConflictException("Second review cannot silently change the first review's fields. Reject for rework.");
        if (!Enum.IsDefined(outcome) || outcome == FieldReviewOutcome.Pending)
            throw new ArgumentException("A supported field decision is required.");
        if (outcome is FieldReviewOutcome.Corrected or FieldReviewOutcome.Rejected && String.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Corrections and rejections require a reason.");
        if (value?.Length > 16_000 || reason?.Length > 2_000 || value?.Contains('\0') == true || reason?.Contains('\0') == true)
            throw new ArgumentException("The corrected value or review reason exceeds its supported limits.");
        var original = session.Fields.FirstOrDefault(field => field.Id == fieldId)
            ?? throw new ArgumentException("The field does not exist in this review snapshot.");
        if (outcome == FieldReviewOutcome.Corrected && String.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A nonempty corrected value is required.");
        var changed = original with
        {
            CurrentValue = outcome == FieldReviewOutcome.Corrected ? value! : original.CurrentValue,
            Outcome = outcome == FieldReviewOutcome.Approved && original.CurrentValue != original.OriginalValue
                ? FieldReviewOutcome.Corrected : outcome,
            ReviewedBy = actor,
            ReviewedAtUtc = now,
            Reason = reason
        };
        return session with
        {
            Fields = session.Fields.Select(field => field.Id == fieldId ? changed : field).ToList(),
            ClaimUntilUtc = now.AddMinutes(15)
        };
    }

    public static ReviewSession Submit(ReviewSession session, ActorIdentity actor, DateTimeOffset now)
    {
        Owns(session, actor, now);
        if (session.Fields.Any(field => field.Outcome is FieldReviewOutcome.Pending or FieldReviewOutcome.Rejected))
            throw new ReviewConflictException("Every field must be approved or corrected before submission.");
        var second = session.Status == ReviewStatus.AwaitingSecondReview;
        if (second && session.FirstReviewer == actor)
            throw new ReviewConflictException("The first reviewer cannot approve the second review.");
        return session with
        {
            Status = session.RequiresDualReview && !second ? ReviewStatus.AwaitingSecondReview : ReviewStatus.Approved,
            FirstReviewer = second ? session.FirstReviewer : actor,
            SecondReviewer = second ? actor : null,
            ClaimedBy = null,
            ClaimUntilUtc = null
        };
    }

    public static void Owns(ReviewSession session, ActorIdentity actor, DateTimeOffset now)
    {
        if (session.Status is not (ReviewStatus.InReview or ReviewStatus.AwaitingSecondReview) ||
            session.ClaimedBy != actor || session.ClaimUntilUtc is null || session.ClaimUntilUtc <= now)
            throw new ReviewConflictException("Claim the current review before modifying it.");
    }

    public static ReviewAuditEvent Audit(ReviewSession session, string action, ActorIdentity? actor,
        DateTimeOffset now, string? fieldId = null, FieldReviewOutcome? outcome = null,
        string? value = null, string? reason = null)
    {
        var id = "audit-" + Guid.CreateVersion7().ToString("N");
        var audit = new ReviewAuditEvent(id, session.TenantId, session.Id, session.JobId,
            session.Version, action, fieldId, outcome, value, reason, actor, now,
            session.LastAuditHash, String.Empty);
        return audit with { Hash = AuditHash(audit) };
    }

    public static string AuditHash(ReviewAuditEvent entry)
    {
        if (String.IsNullOrWhiteSpace(entry.Id) || !entry.Id.StartsWith("audit-", StringComparison.Ordinal) || entry.Id.Length != 38 ||
            !entry.Id.AsSpan(6).ToArray().All(Char.IsAsciiHexDigit))
            throw new InvalidDataException("The review audit event identity is invalid.");
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            action = entry.Action,
            actor = entry.Actor,
            atUtc = entry.AtUtc.ToUniversalTime(),
            fieldId = entry.FieldId,
            id = entry.Id[6..],
            jobId = entry.JobId,
            newValue = entry.NewValue,
            outcome = entry.Outcome,
            previousHash = entry.PreviousHash,
            reason = entry.Reason,
            reviewId = entry.ReviewId,
            sequence = entry.Sequence,
            tenantId = entry.TenantId
        })));
    }

    public static void VerifyHistory(ReviewSession session, IReadOnlyList<ReviewAuditEvent> events)
    {
        var previous = String.Empty;
        long sequence = 0;
        foreach (var entry in events)
        {
            if (entry.TenantId != session.TenantId || entry.ReviewId != session.Id || entry.JobId != session.JobId ||
                entry.Sequence != ++sequence || entry.PreviousHash != previous || entry.Hash != AuditHash(entry))
                throw new InvalidDataException("Review audit continuity or content verification failed.");
            previous = entry.Hash;
        }
        if (sequence != session.Version || previous != session.LastAuditHash)
            throw new InvalidDataException("The review audit does not reach the current state version.");
    }

    public static IReadOnlyList<ReviewAuditEvent> HistorySnapshot(
        ReviewSession session, IReadOnlyList<ReviewAuditEvent> events)
    {
        var prefix = events.Where(entry => entry.Sequence <= session.Version).ToList();
        VerifyHistory(session, prefix);
        return prefix;
    }
}
