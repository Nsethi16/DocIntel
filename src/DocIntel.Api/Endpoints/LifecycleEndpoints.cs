using DocIntel.Api.Identity;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;

namespace DocIntel.Api.Endpoints;

/// <summary>Explicit, admin-only logical deletion; permanent erasure and retention scheduling are not implied.</summary>
public static class LifecycleEndpoints
{
    public static void MapLifecycleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/harness/tenants/{tenantId}")
            .RequireAuthorization("TenantAccess").RequireRateLimiting("DocIntelApi");
        group.AddEndpointFilter<CsrfEndpointFilter>();
        group.MapDelete("/jobs/{jobId}", async (
            string tenantId, string jobId, HttpContext context, IdentityAccessService access,
            DocumentLifecycleService lifecycle, CancellationToken cancellationToken) =>
        {
            if (context.Request.Headers["X-DocIntel-Confirm-Delete"] != jobId)
                return Results.BadRequest(new { error = "Confirm this document ID in X-DocIntel-Confirm-Delete." });
            try
            {
                var deleted = await lifecycle.SoftDeleteAsync(tenantId, jobId,
                    IdentityAccessService.GetActor(context.User), access.IsLocalDevelopment, cancellationToken);
                return deleted is null ? Results.NotFound() : Results.Ok(new
                {
                    jobId,
                    status = "Deleted",
                    deleted.DeletedAtUtc,
                    dataRetained = true,
                    permanentErasure = false
                });
            }
            catch (DocumentLifecycleConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });
        group.MapDelete("", async (
            string tenantId, HttpContext context, IdentityAccessService access, IHarnessStore harnesses,
            CancellationToken cancellationToken) =>
        {
            if (context.Request.Headers["X-DocIntel-Confirm-Delete"] != tenantId)
                return Results.BadRequest(new { error = "Confirm this tenant ID in X-DocIntel-Confirm-Delete." });
            var tenant = await harnesses.GetTenantAsync(tenantId, cancellationToken);
            if (tenant is null) return Results.NotFound();
            if (tenant.IsActive)
            {
                tenant.IsActive = false;
                tenant.DeactivatedAtUtc = DateTimeOffset.UtcNow;
                tenant.DeactivatedBy = IdentityAccessService.GetActor(context.User);
                tenant.DeactivationInDevelopment = access.IsLocalDevelopment;
                try { tenant = await harnesses.UpsertTenantAsync(tenant, cancellationToken); }
                catch (HarnessConflictException exception)
                {
                    return Results.Conflict(new { error = exception.Message });
                }
            }
            return Results.Ok(new
            {
                tenantId,
                status = "Deactivated",
                tenant.HarnessVersion,
                tenant.DeactivatedAtUtc,
                dataRetained = true,
                permanentErasure = false,
                automaticPurgeEnabled = false
            });
        });
    }
}
