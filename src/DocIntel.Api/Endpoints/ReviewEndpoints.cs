using System.Globalization;
using System.Net;
using System.Text.Json;
using DocIntel.Api.Identity;
using DocIntel.Core.Authorization;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Review;
using Microsoft.Azure.Cosmos;

namespace DocIntel.Api.Endpoints;

/// <summary>Document-scoped human review controls; caller identities and source snapshots are server-owned.</summary>
public static class ReviewEndpoints
{
    private static readonly ActorIdentity DevelopmentActor = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002"));

    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/harness/tenants/{tenantId}")
            .RequireAuthorization("TenantAccess").RequireRateLimiting("DocIntelApi");
        group.AddEndpointFilter<CsrfEndpointFilter>();
        group.MapGet("/reviews", async (string tenantId, int? offset, int? limit, ReviewStatus? status,
            HttpContext context, IdentityAccessService access, IJobStore jobs, ReviewService reviews, CancellationToken ct) =>
        {
            if (offset is < 0 || limit is < 1 or > 50 || status.HasValue && !Enum.IsDefined(status.Value))
                return Results.Problem(statusCode: 400, title: "Invalid review page or status.");
            var scope = await access.GetAccessAsync(context.User, tenantId, ct);
            var actor = IdentityAccessService.GetActor(context.User);
            var canReview = scope.CanReview &&
                (access.IsLocalDevelopment || actor is not null && !access.IsServicePrincipal(actor));
            var candidates = (await jobs.ListJobsByTenantAsync(tenantId, ct))
                .Where(job => job.Status == JobStatus.Completed && scope.CanReadDocument(job.JobId)).ToList();
            var items = new List<object>();
            var skipped = 0;
            var take = limit ?? 20;
            var next = (int?)null;
            foreach (var job in candidates)
            {
                var session = await reviews.GetAsync(job, ct);
                if (status.HasValue && session?.Status != status.Value) continue;
                if (skipped++ < (offset ?? 0)) continue;
                if (items.Count == take) { next = (offset ?? 0) + take; break; }
                items.Add(new
                {
                    job.JobId,
                    job.FileName,
                    reviewId = session?.Id,
                    status = session?.Status.ToString() ?? "NotStarted",
                    version = session?.Version,
                    dueAtUtc = session?.DueAtUtc,
                    fieldCount = session?.Fields.Count,
                    canReview
                });
            }
            return Results.Ok(new { items, nextOffset = next });
        });
        group.MapGet("/jobs/{jobId}/review", async (string tenantId, string jobId, HttpContext context,
            IdentityAccessService access, IJobStore jobs, ReviewService reviews, CancellationToken ct) =>
        {
            var actor = IdentityAccessService.GetActor(context.User);
            context.Response.Headers["X-DocIntel-Can-Review"] =
                (await access.GetAccessAsync(context.User, tenantId, ct)).CanReview &&
                (access.IsLocalDevelopment || actor is not null && !access.IsServicePrincipal(actor)) ? "true" : "false";
            var job = await jobs.GetJobAsync(jobId, ct, tenantId);
            if (job?.Status != JobStatus.Completed) return Results.NotFound();
            try
            {
                var review = await reviews.GetAsync(job, ct);
                if (review is null) return Results.NotFound();
                context.Response.Headers.ETag = VersionTag(review.Version);
                return Results.Ok(review);
            }
            catch (ReviewConflictException exception)
            {
                return Results.Problem(statusCode: 409, title: exception.Message);
            }
        });
        group.MapGet("/jobs/{jobId}/review/audit", async (string tenantId, string jobId,
            IJobStore jobs, ReviewService reviews, IReviewStore store, CancellationToken ct) =>
        {
            var job = await jobs.GetJobAsync(jobId, ct, tenantId);
            if (job?.Status != JobStatus.Completed) return Results.NotFound();
            var review = await reviews.GetAsync(job, ct);
            if (review is null) return Results.NotFound();
            var events = ReviewEngine.HistorySnapshot(review, await store.GetAuditAsync(tenantId, review.Id, ct));
            return Results.Ok(events);
        });
        group.MapPost("/jobs/{jobId}/review/{action}", async (string tenantId, string jobId, string action,
            ReviewCommand command, HttpContext context, IdentityAccessService access,
            IJobStore jobs, IHarnessStore harnesses, ReviewService reviews, CancellationToken ct) =>
        {
            var actor = IdentityAccessService.GetActor(context.User);
            if (!access.IsLocalDevelopment && (actor is null || access.IsServicePrincipal(actor))) return Results.Forbid();
            actor ??= DevelopmentActor;
            var job = await jobs.GetJobAsync(jobId, ct, tenantId);
            if (job?.Status != JobStatus.Completed) return Results.NotFound();
            try
            {
                if (action == "start")
                {
                    var existing = await reviews.GetAsync(job, ct);
                    if (existing is not null)
                    {
                        context.Response.Headers.ETag = VersionTag(existing.Version);
                        return Results.Ok(existing);
                    }
                    var tenant = await harnesses.GetTenantAsync(tenantId, ct);
                    if (tenant is null) return Results.NotFound();
                    var copy = JsonSerializer.Deserialize<DocumentJob>(JsonSerializer.SerializeToUtf8Bytes(job))!;
                    copy.StoreVersion = job.StoreVersion;
                    copy.ReviewId = await reviews.CreateAsync(copy, tenant.ReviewPolicy, ct);
                    try { await jobs.UpsertJobAsync(copy, ct); }
                    catch (InvalidOperationException)
                    {
                        return Results.Problem(statusCode: 409, title: "The document changed. Reload before starting its review.");
                    }
                    catch (CosmosException exception) when (exception.StatusCode is
                        HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
                    {
                        return Results.Problem(statusCode: 409, title: "The document changed. Reload before starting its review.");
                    }
                    var created = await reviews.GetAsync(copy, ct);
                    context.Response.Headers.ETag = VersionTag(created!.Version);
                    return Results.Ok(created);
                }
                var tag = context.Request.Headers.IfMatch.ToString();
                if (tag.Length < 10 || !tag.StartsWith("\"review-", StringComparison.Ordinal) ||
                    !tag.EndsWith('"') || !Int64.TryParse(tag.AsSpan(8, tag.Length - 9),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
                    return Results.Problem(statusCode: 428, title: "Supply the review's current If-Match version.");
                var changed = await reviews.ChangeAsync(job, actor, version, action, command.FieldId,
                    command.Outcome, command.Value, command.Reason, access.IsLocalDevelopment, ct);
                context.Response.Headers.ETag = VersionTag(changed.Version);
                return Results.Ok(changed);
            }
            catch (ReviewConflictException exception)
            {
                return Results.Problem(statusCode: 409, title: exception.Message);
            }
            catch (ArgumentException exception)
            {
                return Results.Problem(statusCode: 400, title: exception.Message);
            }
        });
        return app;
    }

    private static string VersionTag(long version) => $"\"review-{version.ToString(CultureInfo.InvariantCulture)}\"";
    public sealed record ReviewCommand(string? FieldId = null, FieldReviewOutcome? Outcome = null,
        string? Value = null, string? Reason = null);
}
