using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace DocIntel.Api.Identity;

internal sealed class TenantAccessRequirement : IAuthorizationRequirement
{
}

/// <summary>Authorizes roles and resources before request-body binding or endpoint execution.</summary>
internal sealed class TenantAccessHandler(
    IdentityAccessService access,
    IHarnessStore harnessStore,
    IAskAiRunStore runStore,
    IJobStore jobs,
    ILogger<TenantAccessHandler> logger) : AuthorizationHandler<TenantAccessRequirement>
{
    internal static object DeniedStatusKey { get; } = new();

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext authorization, TenantAccessRequirement requirement)
    {
        if (authorization.Resource is not HttpContext context)
        {
            authorization.Fail();
            return;
        }
        var status = await GetStatusAsync(context);
        if (status == 0)
        {
            authorization.Succeed(requirement);
        }
        else
        {
            context.Items[DeniedStatusKey] = status;
            authorization.Fail();
        }
    }

    private async Task<int> GetStatusAsync(HttpContext context)
    {
        var pattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? String.Empty;
        var read = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        var bearer = context.User.Identities.Any(i => i.AuthenticationType == IdentityHostingExtensions.BearerScheme);
        var routeTenant = context.Request.RouteValues["tenantId"]?.ToString();
        if (routeTenant is null && !await access.HasAnyMembershipAsync(context.User, context.RequestAborted) ||
            pattern.EndsWith("/catalog", StringComparison.Ordinal) &&
                !await access.CanAdministerAnyTenantAsync(context.User, context.RequestAborted))
        {
            return Denied(pattern, context);
        }
        if (context.Request.RouteValues["tenantId"]?.ToString() is { } tenantId)
        {
            var scope = await access.GetAccessAsync(context.User, tenantId, context.RequestAborted);
            if (!scope.CanRead)
            {
                return Denied(pattern, context);
            }
            context.Response.Headers["X-DocIntel-Can-Admin"] = scope.CanAdmin ? "true" : "false";
            context.Response.Headers["X-DocIntel-Can-Process"] = scope.CanProcess ? "true" : "false";
            var tenant = await harnessStore.GetTenantAsync(tenantId, context.RequestAborted);
            var configurationOperation = pattern.EndsWith("/tenants/{tenantId}", StringComparison.Ordinal);
            if (tenant?.IsActive != true && !(configurationOperation && scope.CanAdmin))
            {
                return 404;
            }
            var ask = pattern.Contains("/ask", StringComparison.Ordinal);
            var review = pattern.Contains("/review", StringComparison.Ordinal);
            var usageOperation = pattern.EndsWith("/ocr-budget", StringComparison.Ordinal) ||
                pattern.EndsWith("/ocr-usage", StringComparison.Ordinal);
            if (pattern.Contains("/users", StringComparison.Ordinal) &&
                (!scope.CanAdmin || scope.AllowedJobIds is not null))
                return Denied(pattern, context);
            if (usageOperation && (!scope.CanAdmin || scope.AllowedJobIds is not null))
                return Denied(pattern, context);
            if (HttpMethods.IsDelete(context.Request.Method) &&
                (!scope.CanAdmin || context.Request.RouteValues["jobId"] is null && scope.AllowedJobIds is not null))
                return Denied(pattern, context);
            if (review && !read && !scope.CanReview) return Denied(pattern, context);
            if (!scope.CanRead || (!read && !ask && !review && !scope.CanProcess && !scope.CanAdmin) ||
                (!read && pattern.EndsWith("/tenants/{tenantId}", StringComparison.Ordinal) && !scope.CanAdmin) ||
                (!read && pattern.EndsWith("/jobs", StringComparison.Ordinal) && !scope.CanProcess) ||
                (!read && pattern.EndsWith("/run-split", StringComparison.Ordinal) && !scope.CanProcess) ||
                (!read && pattern.EndsWith("/seed-examples", StringComparison.Ordinal) && !scope.CanProcess))
            {
                return Denied(pattern, context);
            }
            if (context.Request.RouteValues["jobId"]?.ToString() is { } jobId &&
                !scope.CanReadDocument(jobId))
            {
                return 404;
            }
            if (context.Request.RouteValues["runId"]?.ToString() is { } runId)
            {
                var run = await runStore.GetRunAsync(tenantId, runId, context.RequestAborted);
                var pendingEmailConfirmation = run?.Status == JobStatus.AwaitingConfirmation &&
                    pattern.Contains("/ask/email/runs/", StringComparison.Ordinal);
                if (run is null ||
                    (!(scope.CanAdmin && scope.AllowedJobIds is null) &&
                     !pendingEmailConfirmation && run.Actor != IdentityAccessService.GetActor(context.User)) ||
                    run.Citations.Any(c => !scope.CanReadDocument(c.JobId)))
                {
                    return 404;
                }
                if (run.Status == JobStatus.Completed || !String.IsNullOrEmpty(run.Answer) || run.Citations.Count > 0)
                {
                    var referenced = run.ReferencedJobIds.Concat(run.Citations.Select(citation => citation.JobId))
                        .Distinct(StringComparer.Ordinal);
                    foreach (var id in referenced)
                        if (!scope.CanReadDocument(id) ||
                            await jobs.GetJobAsync(id, context.RequestAborted, tenantId) is not { Status: JobStatus.Completed })
                            return 404;
                    if (run.DocumentsEvaluated > 0 && run.ReferencedJobIds.Count == 0)
                        return 404;
                }
            }
        }
        else if ((!read && !await access.IsPlatformAdministratorAsync(context.User, context.RequestAborted)) ||
                 (read && !pattern.EndsWith("/tenants", StringComparison.Ordinal) &&
                  !pattern.EndsWith("/catalog", StringComparison.Ordinal) &&
                  !pattern.EndsWith("/jobs/{jobId}", StringComparison.Ordinal)))
        {
            return Denied(pattern, context);
        }
        var actor = IdentityAccessService.GetActor(context.User);
        if (pattern.Contains("/review", StringComparison.Ordinal) && !read && !access.IsLocalDevelopment &&
            (actor is null || access.IsServicePrincipal(actor)))
            return Denied(pattern, context);
        if (pattern.Contains("/webhooks/", StringComparison.Ordinal) && !access.IsLocalDevelopment &&
            (!bearer || actor is null || !access.IsServicePrincipal(actor)))
        {
            return Denied(pattern, context);
        }
        return 0;
    }

    private int Denied(string pattern, HttpContext context)
    {
        logger.LogWarning("Access denied for {Operation}; trace {TraceId}.", pattern, context.TraceIdentifier);
        return 403;
    }
}

internal sealed class TenantAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Forbidden && context.Items[TenantAccessHandler.DeniedStatusKey] is int status)
        {
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new
            {
                error = status == 404 ? "The requested resource is not available." : "Access to this operation is not permitted."
            });
            return;
        }
        await _defaultHandler.HandleAsync(next, context, policy, result);
    }
}

/// <summary>Validates browser request verification tokens after framework authentication/authorization.</summary>
public sealed class CsrfEndpointFilter(
    IAntiforgery antiforgery, ILogger<CsrfEndpointFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var bearer = context.User.Identities.Any(i => i.AuthenticationType == IdentityHostingExtensions.BearerScheme);
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !bearer)
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                logger.LogWarning("Rejected a browser request without valid CSRF protection.");
                return Results.BadRequest(new { error = "A valid request verification token is required." });
            }
        }
        context.Response.Headers.CacheControl = "no-store";
        return await next(invocation);
    }

}
