using System.Globalization;

namespace DocIntel.Core.Ocr;

/// <summary>Live operational limits, intentionally separate from frozen processing definitions.</summary>
public sealed record OcrBudget(int MonthlyPageLimit, int WarningPercent, string Version);

/// <summary>Conservative reserved request pages, not confirmed billing or successful recognition pages.</summary>
public sealed record OcrMonthlyUsage(string TenantId, string Period, long ReservedPages, long RequestCount);

/// <summary>The budget and counter observed by one atomic allowance reservation.</summary>
public sealed record OcrAllowance(OcrBudget Budget, OcrMonthlyUsage Usage, bool Replayed);

/// <summary>Signals that a current operational OCR limit would be exceeded before provider invocation.</summary>
public sealed class OcrBudgetExceededException : InvalidOperationException
{
    public OcrBudgetExceededException() : base("The tenant's monthly OCR page allowance is exhausted.") { }
}

/// <summary>Requires an administrator to reload a concurrently edited budget.</summary>
public sealed class OcrBudgetConflictException : InvalidOperationException
{
    public OcrBudgetConflictException() : base("The OCR budget changed. Reload it before saving.") { }
}

/// <summary>Stores live limits and atomically reserves pages under tenant-scoped, idempotent request identifiers.</summary>
public interface IOcrUsageStore
{
    Task<OcrBudget> GetBudgetAsync(string tenantId, CancellationToken cancellationToken = default);
    Task<OcrBudget> UpdateBudgetAsync(string tenantId, OcrBudget budget, CancellationToken cancellationToken = default);
    Task<OcrMonthlyUsage> GetUsageAsync(string tenantId, string period, CancellationToken cancellationToken = default);
    Task<OcrAllowance> ReserveAsync(string tenantId, string period, string requestId, int pages,
        string provider, string documentId, CancellationToken cancellationToken = default);
}

internal static class OcrUsageValidation
{
    public static void Identity(string tenantId, string period)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (tenantId.Length > 512 || period is not { Length: 7 } ||
            !DateTime.TryParseExact(period, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ArgumentException("A valid application tenant and UTC year-month are required.");
    }

    public static void Budget(OcrBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.MonthlyPageLimit is < 0 or > 10_000_000 || budget.WarningPercent is < 1 or > 100 ||
            String.IsNullOrWhiteSpace(budget.Version) || budget.Version == "*" || budget.Version.Length > 256)
            throw new ArgumentException("The OCR limit, warning percentage, or budget version is invalid.");
    }

    public static void Request(string tenantId, string period, string requestId, int pages, string provider, string documentId)
    {
        Identity(tenantId, period);
        if (requestId is not { Length: 32 } || !requestId.All(Char.IsAsciiHexDigit) || pages is < 1 or > 1_000 ||
            String.IsNullOrWhiteSpace(provider) || provider.Length > 256 ||
            String.IsNullOrWhiteSpace(documentId) || documentId.Length > 128)
            throw new ArgumentException("The OCR reservation identity or page count is invalid.");
    }

    public static void SameRequest(string period, int pages, string provider, string documentId,
        string storedPeriod, int storedPages, string storedProvider, string storedDocumentId)
    {
        if (period != storedPeriod || pages != storedPages || provider != storedProvider || documentId != storedDocumentId)
            throw new InvalidDataException("An OCR request identifier was reused for different work.");
    }
}
