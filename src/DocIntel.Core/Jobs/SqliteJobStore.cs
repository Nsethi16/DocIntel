using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocIntel.Core.Jobs;

/// <summary>Transactional single-host job metadata, with expiring owner tokens and version-fenced writes.</summary>
public sealed partial class SqliteJobStore : IJobStore
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private bool _initialized;

    public SqliteJobStore(string path, TimeProvider? clock = null, ILogger<SqliteJobStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<SqliteJobStore>.Instance;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        if (!_initialized)
        {
            await _initialization.WaitAsync(ct);
            try
            {
                if (!_initialized)
                {
                    await using var initial = new SqliteConnection(_connectionString);
                    await initial.OpenAsync(ct);
                    await using var schema = initial.CreateCommand();
                    schema.CommandText = """
                        PRAGMA journal_mode=WAL;
                        CREATE TABLE IF NOT EXISTS document_jobs (
                            tenant TEXT NOT NULL, id TEXT NOT NULL, body BLOB NOT NULL,
                            status INTEGER NOT NULL, version INTEGER NOT NULL, created INTEGER NOT NULL,
                            owner TEXT NULL, expires INTEGER NULL, attempts INTEGER NOT NULL DEFAULT 0,
                            PRIMARY KEY (tenant, id));
                        CREATE INDEX IF NOT EXISTS document_jobs_pending ON document_jobs(status, expires);
                        CREATE TABLE IF NOT EXISTS ocr_budgets (
                            tenant TEXT PRIMARY KEY, page_limit INTEGER NOT NULL, warning INTEGER NOT NULL,
                            version INTEGER NOT NULL);
                        CREATE TABLE IF NOT EXISTS ocr_months (
                            tenant TEXT NOT NULL, period TEXT NOT NULL, pages INTEGER NOT NULL, requests INTEGER NOT NULL,
                            PRIMARY KEY(tenant,period));
                        CREATE TABLE IF NOT EXISTS ocr_requests (
                            tenant TEXT NOT NULL, id TEXT NOT NULL, period TEXT NOT NULL, pages INTEGER NOT NULL,
                            provider TEXT NOT NULL, document_id TEXT NOT NULL, PRIMARY KEY(tenant,id));
                        CREATE TABLE IF NOT EXISTS document_reviews (
                            tenant TEXT NOT NULL,id TEXT NOT NULL,body BLOB NOT NULL,version INTEGER NOT NULL,
                            last_hash TEXT NOT NULL,PRIMARY KEY(tenant,id));
                        CREATE TABLE IF NOT EXISTS review_events (
                            tenant TEXT NOT NULL,review_id TEXT NOT NULL,sequence INTEGER NOT NULL,
                            id TEXT NOT NULL,body BLOB NOT NULL,PRIMARY KEY(tenant,review_id,sequence),
                            UNIQUE(tenant,id));
                        """;
                    await schema.ExecuteNonQueryAsync(ct);
                    _initialized = true;
                }
            }
            finally { _initialization.Release(); }
        }
        var connection = new SqliteConnection(_connectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task UpsertJobAsync(DocumentJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        Validate(job.TenantId, job.JobId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO document_jobs(tenant,id,body,status,version,created)
            VALUES(@tenant,@id,@body,@status,1,@created)
            ON CONFLICT(tenant,id) DO UPDATE SET body=@body,status=@status,version=version+1,owner=NULL,expires=NULL
            WHERE (owner IS NULL OR expires<=@now) AND version=@version
            RETURNING version;
            """;
        Bind(command, job);
        command.Parameters.AddWithValue("@created", job.CreatedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@version", job.StoreVersion);
        command.Parameters.AddWithValue("@now", Now());
        var version = await command.ExecuteScalarAsync(cancellationToken);
        if (version is null) throw new InvalidOperationException("The document changed or is owned by a worker.");
        job.StoreVersion = Convert.ToInt64(version, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<DocumentJob?> GetJobAsync(
        string jobId, CancellationToken cancellationToken = default, string? tenantId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = tenantId is null
            ? "SELECT body,version,attempts,tenant,id FROM document_jobs WHERE id=@id LIMIT 2"
            : "SELECT body,version,attempts,tenant,id FROM document_jobs WHERE tenant=@tenant AND id=@id";
        command.Parameters.AddWithValue("@id", jobId);
        if (tenantId is not null) command.Parameters.AddWithValue("@tenant", tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var job = Read(reader);
        if (tenantId is null && await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("A tenant ID is required for an ambiguous document ID.");
        return job.Status == JobStatus.Deleted ? null : job;
    }

    public async Task<IReadOnlyList<DocumentJob>> ListJobsByTenantAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT body,version,attempts,tenant,id FROM document_jobs
            WHERE tenant=@tenant AND status<>@deleted ORDER BY created DESC,id
            """;
        command.Parameters.AddWithValue("@tenant", tenantId);
        command.Parameters.AddWithValue("@deleted", (int)JobStatus.Deleted);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var jobs = new List<DocumentJob>();
        while (await reader.ReadAsync(cancellationToken)) jobs.Add(Read(reader));
        return jobs;
    }

    public async Task<DocumentQueryPage> SearchDocumentsAsync(
        string tenantId, DocumentQuery query, CancellationToken cancellationToken = default)
    {
        DocumentQueryCompiler.Compile(tenantId, query);
        var offset = 0;
        if (query.ContinuationToken is not null && (!Int32.TryParse(query.ContinuationToken,
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out offset) || offset < 0))
            throw new ArgumentException("Invalid local document continuation token.");
        var jobs = (await ListJobsByTenantAsync(tenantId, cancellationToken))
            .Where(job => DocumentQueryCompiler.Matches(job, tenantId, query))
            .Skip(offset).Take(query.PageSize + 1).ToList();
        return new DocumentQueryPage(jobs.Take(query.PageSize).Select(job => new DocumentReference(
            job.JobId, job.FileName, job.TotalPages, job.Segments.Count)).ToList(),
            jobs.Count > query.PageSize ? (offset + query.PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    public async Task<IDocumentJobLease?> TryAcquireJobAsync(
        string tenantId, string jobId, CancellationToken cancellationToken = default)
    {
        Validate(tenantId, jobId);
        var owner = Guid.NewGuid().ToString("N");
        await using var connection = await OpenAsync(cancellationToken);
        await using var claim = connection.CreateCommand();
        claim.CommandText = """
            UPDATE document_jobs SET owner=@owner,expires=@expires,version=version+1,attempts=attempts+1
            WHERE tenant=@tenant AND id=@id AND status IN (0,1) AND (owner IS NULL OR expires<=@now)
            RETURNING body,version,attempts,tenant,id;
            """;
        claim.Parameters.AddWithValue("@tenant", tenantId);
        claim.Parameters.AddWithValue("@id", jobId);
        claim.Parameters.AddWithValue("@owner", owner);
        claim.Parameters.AddWithValue("@now", Now());
        claim.Parameters.AddWithValue("@expires", Now() + 60_000);
        await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var job = Read(reader);
        return new DocumentJobLease(job,
            ct => SaveOwnedAsync(job, owner, renewOnly: false, ct),
            ct => SaveOwnedAsync(job, owner, renewOnly: true, ct),
            () => ReleaseAsync(tenantId, jobId, owner), _logger);
    }

    private async Task<bool> SaveOwnedAsync(DocumentJob job, string owner, bool renewOnly, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = renewOnly ? """
            UPDATE document_jobs SET expires=@expires,version=version+1
            WHERE tenant=@tenant AND id=@id AND owner=@owner AND expires>@now AND version=@version RETURNING version;
            """ : """
            UPDATE document_jobs SET body=@body,status=@status,expires=@expires,version=version+1
            WHERE tenant=@tenant AND id=@id AND owner=@owner AND expires>@now AND version=@version RETURNING version;
            """;
        if (renewOnly)
        {
            command.Parameters.AddWithValue("@tenant", job.TenantId);
            command.Parameters.AddWithValue("@id", job.JobId);
        }
        else Bind(command, job);
        command.Parameters.AddWithValue("@owner", owner);
        command.Parameters.AddWithValue("@version", job.StoreVersion);
        command.Parameters.AddWithValue("@now", Now());
        command.Parameters.AddWithValue("@expires", Now() + 60_000);
        var version = await command.ExecuteScalarAsync(ct);
        if (version is null) return false;
        job.StoreVersion = Convert.ToInt64(version, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private async Task ReleaseAsync(string tenantId, string jobId, string owner)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = await OpenAsync(timeout.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE document_jobs SET owner=NULL,expires=NULL WHERE tenant=@tenant AND id=@id AND owner=@owner";
        command.Parameters.AddWithValue("@tenant", tenantId);
        command.Parameters.AddWithValue("@id", jobId);
        command.Parameters.AddWithValue("@owner", owner);
        await command.ExecuteNonQueryAsync(timeout.Token);
    }

    public async Task<IReadOnlyList<JobQueueTicket>> ListRecoverableJobsAsync(
        int maxCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxCount, 100);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT body,version,attempts,tenant,id FROM document_jobs
            WHERE status IN(0,1) AND (owner IS NULL OR expires<=@now) ORDER BY created,id LIMIT @count;
            """;
        command.Parameters.AddWithValue("@now", Now());
        command.Parameters.AddWithValue("@count", maxCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tickets = new List<JobQueueTicket>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var job = Read(reader);
            tickets.Add(new JobQueueTicket(job.JobId, job.TenantId));
        }
        return tickets;
    }

    private long Now() => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private static void Validate(string tenant, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
    }

    private static void Bind(SqliteCommand command, DocumentJob job)
    {
        command.Parameters.AddWithValue("@tenant", job.TenantId);
        command.Parameters.AddWithValue("@id", job.JobId);
        command.Parameters.AddWithValue("@body", JsonSerializer.SerializeToUtf8Bytes(job));
        command.Parameters.AddWithValue("@status", (int)job.Status);
    }

    private static DocumentJob Read(SqliteDataReader reader)
    {
        var job = JsonSerializer.Deserialize<DocumentJob>((byte[])reader[0])
            ?? throw new InvalidDataException("Stored document metadata is empty.");
        if (job.TenantId != reader.GetString(3) || job.JobId != reader.GetString(4))
            throw new InvalidDataException("Stored document identity does not match its metadata key.");
        job.StoreVersion = reader.GetInt64(1);
        job.ExecutionAttempts = reader.GetInt32(2);
        return job;
    }
}
