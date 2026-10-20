using System.Globalization;

namespace DocIntel.Core.Ocr;

/// <summary>Isolated library/test allowance store; deployed hosts select transactional persistent storage.</summary>
public sealed class InMemoryOcrUsageStore : IOcrUsageStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OcrBudget> _budgets = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Tenant, string Period), OcrMonthlyUsage> _usage = new();
    private readonly Dictionary<(string Tenant, string Id), (string Period, int Pages, string Provider, string Document)> _requests = new();

    private OcrBudget Budget(string tenant) =>
        _budgets.TryGetValue(tenant, out var budget) ? budget : _budgets[tenant] = new OcrBudget(100_000, 80, "1");

    public Task<OcrBudget> GetBudgetAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(Budget(tenantId));
    }

    public Task<OcrBudget> UpdateBudgetAsync(string tenantId, OcrBudget budget, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        OcrUsageValidation.Budget(budget);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var current = Budget(tenantId);
            if (current.Version != budget.Version) throw new OcrBudgetConflictException();
            var updated = budget with
            {
                Version = (Int64.Parse(current.Version, CultureInfo.InvariantCulture) + 1)
                .ToString(CultureInfo.InvariantCulture)
            };
            _budgets[tenantId] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<OcrMonthlyUsage> GetUsageAsync(string tenantId, string period, CancellationToken cancellationToken = default)
    {
        OcrUsageValidation.Identity(tenantId, period);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_usage.GetValueOrDefault((tenantId, period)) ?? new(tenantId, period, 0, 0));
    }

    public Task<OcrAllowance> ReserveAsync(string tenantId, string period, string requestId, int pages,
        string provider, string documentId, CancellationToken cancellationToken = default)
    {
        OcrUsageValidation.Request(tenantId, period, requestId, pages, provider, documentId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var budget = Budget(tenantId);
            var current = _usage.GetValueOrDefault((tenantId, period)) ?? new(tenantId, period, 0, 0);
            if (_requests.TryGetValue((tenantId, requestId), out var previous))
            {
                OcrUsageValidation.SameRequest(period, pages, provider, documentId,
                    previous.Period, previous.Pages, previous.Provider, previous.Document);
                return Task.FromResult(new OcrAllowance(budget, current, true));
            }
            if (current.ReservedPages + pages > budget.MonthlyPageLimit) throw new OcrBudgetExceededException();
            current = current with { ReservedPages = current.ReservedPages + pages, RequestCount = current.RequestCount + 1 };
            _requests[(tenantId, requestId)] = (period, pages, provider, documentId);
            _usage[(tenantId, period)] = current;
            return Task.FromResult(new OcrAllowance(budget, current, false));
        }
    }
}
