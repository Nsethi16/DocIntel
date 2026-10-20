namespace DocIntel.Core.Review;

internal static class ReviewStoreValidation
{
    public const int MaximumVersions = 10_000;
    public static void Identity(string tenant, string review)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        if (tenant.Length > 512 || review is not { Length: 32 } || !review.All(Char.IsAsciiHexDigit))
            throw new ArgumentException("A valid tenant and opaque review identity are required.");
    }

    public static void Change(ReviewSession session, ReviewAuditEvent audit, long expected)
    {
        Stored(session, session.TenantId, session.Id);
        if (session.Version != expected + 1 || audit.Sequence != session.Version ||
            audit.TenantId != session.TenantId || audit.ReviewId != session.Id || audit.JobId != session.JobId ||
            session.LastAuditHash != audit.Hash || audit.Hash.Length != 64 || audit.PreviousHash is null || session.Fields.Count > 2_000 ||
            session.Version > MaximumVersions || audit.Hash != ReviewEngine.AuditHash(audit) ||
            (expected == 0 ? audit.PreviousHash.Length != 0 : audit.PreviousHash.Length != 64))
            throw new InvalidDataException("The review state and audit append do not form a valid versioned update.");
    }

    public static void Stored(ReviewSession session, string tenant, string review)
    {
        Identity(tenant, review);
        if (session.TenantId != tenant || session.Id != review || session.Kind != "session" ||
            String.IsNullOrWhiteSpace(session.JobId) || session.JobId.Length > 128 ||
            session.SourceFingerprint is not { Length: 64 } || !session.SourceFingerprint.All(Char.IsAsciiHexDigit) ||
            session.Version is < 1 or > MaximumVersions || session.LastAuditHash is not { Length: 64 } ||
            !session.LastAuditHash.All(Char.IsAsciiHexDigit) || !Enum.IsDefined(session.Status) ||
            session.Fields is null || session.Fields.Count > 2_000 ||
            session.Fields.Any(field => field is null || String.IsNullOrWhiteSpace(field.Id) ||
                field.OriginalValue is null || field.CurrentValue is null || !Enum.IsDefined(field.Outcome)) ||
            session.Fields.Select(field => field.Id).Distinct(StringComparer.Ordinal).Count() != session.Fields.Count)
            throw new InvalidDataException("The stored review identity, version, field collection or source binding is invalid.");
        if (session.Status == ReviewStatus.AwaitingSecondReview &&
            (!session.RequiresDualReview || session.FirstReviewer is null) ||
            session.Status == ReviewStatus.Approved && session.RequiresDualReview &&
            (session.FirstReviewer is null || session.SecondReviewer is null || session.FirstReviewer == session.SecondReviewer))
            throw new InvalidDataException("The review approval phase has invalid reviewer identities.");
    }
}
