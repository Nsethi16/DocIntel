using System.Security.Cryptography;
using DocIntel.Api.Identity;
using DocIntel.Core.Authorization;
using DocIntel.Core.Export;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Review;
using DocIntel.Core.Storage;

namespace DocIntel.Api.Endpoints;

/// <summary>Downloads source-bound exports directly through current document authorization, without bearer SAS links.</summary>
public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/harness/tenants/{tenantId}")
            .RequireAuthorization("TenantAccess").RequireRateLimiting("DocIntelApi");
        group.AddEndpointFilter<CsrfEndpointFilter>();
        group.MapGet("/jobs/{jobId}/export", async (
            string tenantId, string jobId, string? format, string? view, HttpContext context,
            IdentityAccessService access, IJobStore jobs, IHarnessStore harnesses, ReviewService reviews,
            IReviewStore reviewStore, IArtifactStore artifacts, CancellationToken ct) =>
        {
            var kind = new[] { "csv", "xlsx", "json" }.FirstOrDefault(candidate =>
                String.Equals(candidate, format ?? "csv", StringComparison.OrdinalIgnoreCase));
            if (kind is null || view is not null &&
                !String.Equals(view, "reviewed", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(view, "original", StringComparison.OrdinalIgnoreCase))
                return Results.Problem(statusCode: 400, title: "Choose csv, xlsx or json and reviewed or original values.");
            var original = String.Equals(view, "original", StringComparison.OrdinalIgnoreCase);
            var scope = access.GetAccess(context.User, tenantId);
            if (original && scope.Role is not (TenantRole.Processor or TenantRole.Admin)) return Results.Forbid();
            var job = await jobs.GetJobAsync(jobId, ct, tenantId);
            if (job is null) return Results.NotFound();
            try
            {
                var review = original ? null : await reviews.GetAsync(job, ct);
                if (review is not null)
                    ReviewEngine.HistorySnapshot(review, await reviewStore.GetAuditAsync(tenantId, review.Id, ct));
                var snapshot = DocumentExporter.Snapshot(job, review, original, DateTimeOffset.UtcNow);
                var bytes = DocumentExporter.Write(snapshot, kind);
                ct.ThrowIfCancellationRequested();
                var currentAccess = access.GetAccess(context.User, tenantId);
                if (!currentAccess.CanReadDocument(jobId)) return Results.NotFound();
                if (original && currentAccess.Role is not (TenantRole.Processor or TenantRole.Admin)) return Results.Forbid();
                var currentJob = await jobs.GetJobAsync(jobId, ct, tenantId);
                if (currentJob is null || currentJob.Status != JobStatus.Completed ||
                    ReviewEngine.Fingerprint(currentJob) != ReviewEngine.Fingerprint(job) ||
                    !original && currentJob.ReviewId != review!.Id)
                    return Results.Problem(statusCode: 409, title: "The document changed during export. Reload it.");
                if ((await harnesses.GetTenantAsync(tenantId, ct))?.IsActive != true) return Results.NotFound();
                await artifacts.SaveExportAuditAsync(new ExportAuditEvent(snapshot.ExportId, tenantId, jobId,
                    IdentityAccessService.GetActor(context.User), kind, snapshot.View, snapshot.ReviewVersion,
                    job.OriginalSha256, Convert.ToHexString(SHA256.HashData(bytes)), snapshot.Fields.Count,
                    snapshot.CreatedAtUtc, access.IsLocalDevelopment), ct);
                context.Response.Headers["X-DocIntel-Export-Id"] = snapshot.ExportId;
                var contentType = kind switch
                {
                    "csv" => "text/csv; charset=utf-8",
                    "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    _ => "application/json"
                };
                return Results.File(bytes, contentType, $"docintel-{jobId}-{snapshot.View}.{kind}");
            }
            catch (ReviewConflictException exception)
            {
                return Results.Problem(statusCode: 409, title: exception.Message);
            }
            catch (ExportLimitException exception)
            {
                return Results.Problem(statusCode: 413, title: exception.Message);
            }
        });
        return app;
    }
}
