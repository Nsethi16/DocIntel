using System.Text.Json;
using DocIntel.Core.Review;
using Microsoft.Data.Sqlite;

namespace DocIntel.Core.Jobs;

public sealed partial class SqliteJobStore : IReviewStore
{
    public async Task CreateAsync(ReviewSession session, ReviewAuditEvent audit, CancellationToken cancellationToken = default)
    {
        ReviewStoreValidation.Change(session, audit, 0);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var create = UsageCommand(connection, transaction, """
            INSERT INTO document_reviews(tenant,id,body,version,last_hash)
            VALUES(@tenant,@id,@body,1,@hash) ON CONFLICT(tenant,id) DO NOTHING
            """, ("@tenant", session.TenantId), ("@id", session.Id), ("@body", JsonSerializer.SerializeToUtf8Bytes(session)),
            ("@hash", session.LastAuditHash));
        if (await create.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new ReviewConflictException("The review already exists.");
        await AppendReviewEventAsync(connection, transaction, audit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ReviewSession?> GetAsync(
        string tenantId, string reviewId, CancellationToken cancellationToken = default)
    {
        ReviewStoreValidation.Identity(tenantId, reviewId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var read = UsageCommand(connection, null,
            "SELECT body,version,last_hash FROM document_reviews WHERE tenant=@tenant AND id=@id",
            ("@tenant", tenantId), ("@id", reviewId));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var session = JsonSerializer.Deserialize<ReviewSession>((byte[])reader[0])
            ?? throw new InvalidDataException("The stored review is empty.");
        if (session.TenantId != tenantId || session.Id != reviewId || session.Version != reader.GetInt64(1) ||
            session.LastAuditHash != reader.GetString(2))
            throw new InvalidDataException("The review state does not match its stored identity or version.");
        ReviewStoreValidation.Stored(session, tenantId, reviewId);
        return session;
    }

    public async Task SaveAsync(ReviewSession session, long expectedVersion, ReviewAuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ReviewStoreValidation.Change(session, audit, expectedVersion);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var save = UsageCommand(connection, transaction, """
            UPDATE document_reviews SET body=@body,version=@version,last_hash=@hash
            WHERE tenant=@tenant AND id=@id AND version=@expected AND last_hash=@previous
            """, ("@tenant", session.TenantId), ("@id", session.Id), ("@body", JsonSerializer.SerializeToUtf8Bytes(session)),
            ("@version", session.Version), ("@hash", session.LastAuditHash), ("@expected", expectedVersion),
            ("@previous", audit.PreviousHash));
        if (await save.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new ReviewConflictException("The review changed. Reload it before editing.");
        await AppendReviewEventAsync(connection, transaction, audit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReviewAuditEvent>> GetAuditAsync(
        string tenantId, string reviewId, CancellationToken cancellationToken = default)
    {
        ReviewStoreValidation.Identity(tenantId, reviewId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var read = UsageCommand(connection, null,
            "SELECT body FROM review_events WHERE tenant=@tenant AND review_id=@id ORDER BY sequence LIMIT 10000",
            ("@tenant", tenantId), ("@id", reviewId));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        var result = new List<ReviewAuditEvent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var entry = JsonSerializer.Deserialize<ReviewAuditEvent>((byte[])reader[0])
                ?? throw new InvalidDataException("The stored review audit entry is empty.");
            if (entry.TenantId != tenantId || entry.ReviewId != reviewId)
                throw new InvalidDataException("The review audit identity does not match its storage key.");
            result.Add(entry);
        }
        return result;
    }

    private static async Task AppendReviewEventAsync(
        SqliteConnection connection, SqliteTransaction transaction, ReviewAuditEvent audit, CancellationToken ct)
    {
        await using var append = UsageCommand(connection, transaction, """
            INSERT INTO review_events(tenant,review_id,sequence,id,body) VALUES(@tenant,@review,@sequence,@id,@body)
            """, ("@tenant", audit.TenantId), ("@review", audit.ReviewId), ("@sequence", audit.Sequence),
            ("@id", audit.Id), ("@body", JsonSerializer.SerializeToUtf8Bytes(audit)));
        await append.ExecuteNonQueryAsync(ct);
    }
}
