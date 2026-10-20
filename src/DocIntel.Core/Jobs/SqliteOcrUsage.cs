using System.Globalization;
using DocIntel.Core.Ocr;
using Microsoft.Data.Sqlite;

namespace DocIntel.Core.Jobs;

public sealed partial class SqliteJobStore : IOcrUsageStore
{
    public async Task<OcrBudget> GetBudgetAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadBudgetAsync(connection, null, tenantId, cancellationToken);
    }

    public async Task<OcrBudget> UpdateBudgetAsync(
        string tenantId, OcrBudget budget, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        OcrUsageValidation.Budget(budget);
        await using var connection = await OpenAsync(cancellationToken);
        await ReadBudgetAsync(connection, null, tenantId, cancellationToken);
        await using var command = UsageCommand(connection, null, """
            UPDATE ocr_budgets SET page_limit=@limit,warning=@warning,version=version+1
            WHERE tenant=@tenant AND CAST(version AS TEXT)=@version RETURNING version
            """, ("@tenant", tenantId), ("@limit", budget.MonthlyPageLimit),
            ("@warning", budget.WarningPercent), ("@version", budget.Version));
        var version = await command.ExecuteScalarAsync(cancellationToken);
        if (version is null) throw new OcrBudgetConflictException();
        return budget with { Version = Convert.ToString(version, CultureInfo.InvariantCulture)! };
    }

    public async Task<OcrMonthlyUsage> GetUsageAsync(
        string tenantId, string period, CancellationToken cancellationToken = default)
    {
        OcrUsageValidation.Identity(tenantId, period);
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadUsageAsync(connection, null, tenantId, period, cancellationToken);
    }

    public async Task<OcrAllowance> ReserveAsync(string tenantId, string period, string requestId, int pages,
        string provider, string documentId, CancellationToken cancellationToken = default)
    {
        OcrUsageValidation.Request(tenantId, period, requestId, pages, provider, documentId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var budget = await ReadBudgetAsync(connection, transaction, tenantId, cancellationToken);
        var current = await ReadUsageAsync(connection, transaction, tenantId, period, cancellationToken);
        await using (var previous = UsageCommand(connection, transaction, """
            SELECT period,pages,provider,document_id FROM ocr_requests WHERE tenant=@tenant AND id=@id
            """, ("@tenant", tenantId), ("@id", requestId)))
        {
            await using var reader = await previous.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                OcrUsageValidation.SameRequest(period, pages, provider, documentId,
                    reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3));
                if (current.ReservedPages < pages || current.RequestCount < 1)
                    throw new InvalidDataException("The OCR reservation has no matching usage counter.");
                return new OcrAllowance(budget, current, true);
            }
        }
        if (current.ReservedPages + pages > budget.MonthlyPageLimit) throw new OcrBudgetExceededException();
        await using var update = UsageCommand(connection, transaction, """
            INSERT INTO ocr_months(tenant,period,pages,requests) VALUES(@tenant,@period,@pages,1)
            ON CONFLICT(tenant,period) DO UPDATE SET pages=pages+@pages,requests=requests+1;
            INSERT INTO ocr_requests(tenant,id,period,pages,provider,document_id)
            VALUES(@tenant,@id,@period,@pages,@provider,@document)
            """, ("@tenant", tenantId), ("@period", period), ("@pages", pages), ("@id", requestId),
            ("@provider", provider), ("@document", documentId));
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OcrAllowance(budget, current with
        {
            ReservedPages = current.ReservedPages + pages,
            RequestCount = current.RequestCount + 1
        }, false);
    }

    private static async Task<OcrBudget> ReadBudgetAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string tenant, CancellationToken ct)
    {
        await using (var create = UsageCommand(connection, transaction, """
            INSERT INTO ocr_budgets(tenant,page_limit,warning,version) VALUES(@tenant,100000,80,1)
            ON CONFLICT(tenant) DO NOTHING
            """, ("@tenant", tenant)))
        {
            await create.ExecuteNonQueryAsync(ct);
        }
        await using var read = UsageCommand(connection, transaction,
            "SELECT page_limit,warning,version FROM ocr_budgets WHERE tenant=@tenant", ("@tenant", tenant));
        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidDataException("The OCR budget is unavailable.");
        var limit = reader.GetInt32(0);
        var warning = reader.GetInt32(1);
        var version = reader.GetInt64(2);
        if (limit is < 0 or > 10_000_000 || warning is < 1 or > 100 || version < 1)
            throw new InvalidDataException("The persisted OCR budget has invalid limits or revision.");
        return new OcrBudget(limit, warning, version.ToString(CultureInfo.InvariantCulture));
    }

    private static async Task<OcrMonthlyUsage> ReadUsageAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string tenant, string period, CancellationToken ct)
    {
        await using var read = UsageCommand(connection, transaction,
            "SELECT pages,requests FROM ocr_months WHERE tenant=@tenant AND period=@period",
            ("@tenant", tenant), ("@period", period));
        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new(tenant, period, 0, 0);
        var pages = reader.GetInt64(0);
        var requests = reader.GetInt64(1);
        if (pages is < 0 or > 10_000_000 || requests < 0 || requests > pages)
            throw new InvalidDataException("The persisted OCR usage counter is invalid.");
        return new OcrMonthlyUsage(tenant, period, pages, requests);
    }

    private static SqliteCommand UsageCommand(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }
}
