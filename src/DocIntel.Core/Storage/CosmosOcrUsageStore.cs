using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using DocIntel.Core.Ocr;
using Microsoft.Azure.Cosmos;

namespace DocIntel.Core.Storage;

/// <summary>Uses same-partition transactions to fence live budget edits, monthly counters and idempotent reservations.</summary>
public sealed class CosmosOcrUsageStore(Container container) : IOcrUsageStore
{
    private const int MaxAttempts = 8;

    public async Task CheckReadinessAsync(CancellationToken cancellationToken = default) =>
        await container.ReadContainerAsync(cancellationToken: cancellationToken);

    public async Task<OcrBudget> GetBudgetAsync(string tenantId, CancellationToken cancellationToken = default) =>
        Snapshot((await ReadBudgetAsync(tenantId, cancellationToken)).Resource);

    public async Task<OcrBudget> UpdateBudgetAsync(
        string tenantId, OcrBudget budget, CancellationToken cancellationToken = default)
    {
        OcrUsageValidation.Budget(budget);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = await ReadBudgetAsync(tenantId, cancellationToken);
            if (Snapshot(current.Resource).Version != budget.Version) throw new OcrBudgetConflictException();
            var updated = current.Resource with
            {
                Limit = budget.MonthlyPageLimit,
                Warning = budget.WarningPercent,
                Revision = current.Resource.Revision + 1
            };
            try
            {
                await container.ReplaceItemAsync(updated, "budget", new PartitionKey(tenantId),
                    new ItemRequestOptions { IfMatchEtag = ETag(current) }, cancellationToken);
                return Snapshot(updated);
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            }
        }
        throw new OcrBudgetConflictException();
    }

    public async Task<OcrMonthlyUsage> GetUsageAsync(
        string tenantId, string period, CancellationToken cancellationToken = default)
    {
        OcrUsageValidation.Identity(tenantId, period);
        var response = await ReadAsync<MonthRecord>(tenantId, "month-" + period, cancellationToken);
        return Usage(tenantId, period, response?.Resource);
    }

    public async Task<OcrAllowance> ReserveAsync(string tenantId, string period, string requestId, int pages,
        string provider, string documentId, CancellationToken cancellationToken = default)
    {
        OcrUsageValidation.Request(tenantId, period, requestId, pages, provider, documentId);
        var partition = new PartitionKey(tenantId);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var previous = await ReadAsync<RequestRecord>(tenantId, "request-" + requestId, cancellationToken);
            var budget = await ReadBudgetAsync(tenantId, cancellationToken);
            var monthly = await ReadAsync<MonthRecord>(tenantId, "month-" + period, cancellationToken);
            var usage = Usage(tenantId, period, monthly?.Resource);
            if (previous is not null)
            {
                if (previous.Resource.TenantId != tenantId || previous.Resource.Id != "request-" + requestId)
                    throw new InvalidDataException("The OCR request does not match its tenant partition.");
                OcrUsageValidation.SameRequest(period, pages, provider, documentId, previous.Resource.Period,
                    previous.Resource.Pages, previous.Resource.Provider, previous.Resource.DocumentId);
                if (usage.ReservedPages < pages || usage.RequestCount < 1)
                    throw new InvalidDataException("The OCR reservation has no matching usage counter.");
                return new OcrAllowance(Snapshot(budget.Resource), usage, true);
            }
            if (usage.ReservedPages + pages > budget.Resource.Limit) throw new OcrBudgetExceededException();
            var updated = new MonthRecord("month-" + period, tenantId, period,
                usage.ReservedPages + pages, usage.RequestCount + 1);
            var batch = container.CreateTransactionalBatch(partition)
                .ReplaceItem("budget", budget.Resource,
                    new TransactionalBatchItemRequestOptions { IfMatchEtag = ETag(budget) });
            if (monthly is null) batch.CreateItem(updated);
            else batch.ReplaceItem(updated.Id, updated,
                new TransactionalBatchItemRequestOptions { IfMatchEtag = ETag(monthly) });
            batch.CreateItem(new RequestRecord("request-" + requestId, tenantId, period, pages, provider, documentId));
            using var response = await batch.ExecuteAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
                return new OcrAllowance(Snapshot(budget.Resource), Usage(tenantId, period, updated), false);
            if (response.StatusCode is not (HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed) &&
                !response.Any(operation => operation.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed))
                throw new CosmosException("OCR allowance storage rejected its atomic transaction.",
                    response.StatusCode, 0, response.ActivityId, response.RequestCharge);
            await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
        }
        throw new InvalidOperationException("OCR allowance storage is busy. No provider request was authorized.");
    }

    private async Task<ItemResponse<BudgetRecord>> ReadBudgetAsync(string tenant, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        var response = await ReadAsync<BudgetRecord>(tenant, "budget", ct);
        if (response is null)
        {
            try
            {
                response = await container.CreateItemAsync(new BudgetRecord("budget", tenant, 100_000, 80, 1),
                    new PartitionKey(tenant), cancellationToken: ct);
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                response = await ReadAsync<BudgetRecord>(tenant, "budget", ct);
            }
        }
        if (response?.Resource is null || response.Resource.Id != "budget" || response.Resource.TenantId != tenant)
            throw new InvalidDataException("The live OCR budget is unavailable or does not match its tenant.");
        ETag(response);
        if (response.Resource.Limit is < 0 or > 10_000_000 || response.Resource.Warning is < 1 or > 100 ||
            response.Resource.Revision < 1)
            throw new InvalidDataException("The persisted OCR budget has invalid limits or revision.");
        return response;
    }

    private async Task<ItemResponse<T>?> ReadAsync<T>(string tenant, string id, CancellationToken ct)
    {
        try { return await container.ReadItemAsync<T>(id, new PartitionKey(tenant), cancellationToken: ct); }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    private static string ETag<T>(ItemResponse<T> response) =>
        !String.IsNullOrWhiteSpace(response.ETag) && response.ETag != "*" ? response.ETag :
            throw new InvalidDataException("A specific OCR allowance version is required for a fenced update.");

    private static OcrBudget Snapshot(BudgetRecord budget) =>
        new(budget.Limit, budget.Warning, budget.Revision.ToString(CultureInfo.InvariantCulture));

    private static OcrMonthlyUsage Usage(string tenant, string period, MonthRecord? record)
    {
        if (record is null) return new(tenant, period, 0, 0);
        if (record.TenantId != tenant || record.Period != period || record.Id != "month-" + period ||
            record.Pages is < 0 or > 10_000_000 || record.Requests < 0 || record.Requests > record.Pages)
            throw new InvalidDataException("The OCR usage counter does not match its tenant/month.");
        return new(tenant, period, record.Pages, record.Requests);
    }

    public sealed record BudgetRecord(
        [property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("tenantId")] string TenantId,
        int Limit, int Warning, long Revision);
    public sealed record MonthRecord(
        [property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("tenantId")] string TenantId,
        string Period, long Pages, long Requests);
    public sealed record RequestRecord(
        [property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("tenantId")] string TenantId,
        string Period, int Pages, string Provider, string DocumentId);
}
