using System.Globalization;
using DocIntel.Api.Identity;
using DocIntel.Core.Ocr;

namespace DocIntel.Api.Endpoints;

/// <summary>Tenant-wide administrator controls for live OCR limits and conservative monthly request accounting.</summary>
public static class OcrUsageEndpoints
{
    public static IEndpointRouteBuilder MapOcrUsageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/harness/tenants/{tenantId}")
            .RequireAuthorization("TenantAccess").RequireRateLimiting("DocIntelApi");
        group.AddEndpointFilter<CsrfEndpointFilter>();
        group.MapGet("/ocr-budget", async (string tenantId, IOcrUsageStore store, CancellationToken ct) =>
            Results.Ok(await store.GetBudgetAsync(tenantId, ct)));
        group.MapPut("/ocr-budget", async (string tenantId, OcrBudget budget, IOcrUsageStore store, CancellationToken ct) =>
        {
            try { return Results.Ok(await store.UpdateBudgetAsync(tenantId, budget, ct)); }
            catch (OcrBudgetConflictException)
            {
                return Results.Problem(statusCode: 409, title: "The OCR budget changed. Reload it before saving.");
            }
            catch (ArgumentException)
            {
                return Results.Problem(statusCode: 400, title: "A valid page limit, warning percentage and budget version are required.");
            }
        });
        group.MapGet("/ocr-usage", async (string tenantId, string? period, IOcrUsageStore store, CancellationToken ct) =>
        {
            var month = period ?? DateTimeOffset.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            try
            {
                var usage = await store.GetUsageAsync(tenantId, month, ct);
                var budget = await store.GetBudgetAsync(tenantId, ct);
                return Results.Ok(new
                {
                    usage.TenantId,
                    usage.Period,
                    usage.ReservedPages,
                    usage.RequestCount,
                    budget,
                    remainingPages = Math.Max(0, budget.MonthlyPageLimit - usage.ReservedPages),
                    accounting = "Reserved provider-request pages, including failed or interrupted attempts; not an Azure invoice."
                });
            }
            catch (ArgumentException)
            {
                return Results.Problem(statusCode: 400, title: "Period must be a UTC year-month in yyyy-MM format.");
            }
        });
        return app;
    }
}
