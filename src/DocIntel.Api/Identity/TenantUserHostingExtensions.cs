using System.Text.Json;
using Azure.Identity;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Storage;

namespace DocIntel.Api.Identity;

public sealed record ChangeTenantUsersRequest(long Version, IReadOnlyList<TenantUserChange>? Changes);

/// <summary>Read-only directory integration and tenant-admin-only access management.</summary>
public static class TenantUserHostingExtensions
{
    public static void AddDocIntelTenantUsers(this WebApplicationBuilder builder)
    {
        var options = new EnterpriseDirectoryOptions();
        builder.Configuration.GetSection("Identity:Directory").Bind(options);
        var identity = builder.Services.First(descriptor => descriptor.ServiceType == typeof(IdentityOptions))
            .ImplementationInstance as IdentityOptions
            ?? throw new InvalidOperationException("Register DocIntel identity before tenant users.");
        if (options.Enabled && identity.Mode != IdentityMode.Entra)
            throw new InvalidOperationException("Enterprise directory access requires production Entra identity.");
        if (options.DevelopmentEndpoint is not null &&
            (!options.Enabled || !builder.Environment.IsDevelopment() || !identity.AllowLoopbackAuthority ||
             !IdentityOptions.IsLoopbackUrl(options.DevelopmentEndpoint) ||
             String.IsNullOrWhiteSpace(builder.Configuration["urls"]) ||
             !builder.Configuration["urls"]!.Split(';').All(IdentityOptions.IsLoopbackUrl) ||
             !options.DevelopmentEndpoint.EndsWith("/", StringComparison.Ordinal)))
            throw new InvalidOperationException("Directory protocol fixtures require an isolated loopback Development identity host.");
        builder.Services.AddSingleton(options);
        builder.Services.AddHttpClient(GraphEnterpriseDirectory.HttpClientName, client =>
            client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton<IEnterpriseDirectory, GraphEnterpriseDirectory>();
        var directory = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "tenant-users");
        builder.Services.AddSingleton<ITenantUserStore>(services =>
            services.GetService<CloudAskAiClients>() is { } cloud
                ? new BlobTenantUserStore(cloud.Runs) : new FileTenantUserStore(directory));
        builder.Services.AddSingleton<TenantUserService>();
        builder.Services.AddSingleton<IAutomaticEmailAuthorization>(services => services.GetRequiredService<TenantUserService>());
    }

    public static void MapDocIntelTenantUsers(this WebApplication app)
    {
        var group = app.MapGroup("/api/harness/tenants/{tenantId}/users")
            .RequireAuthorization("TenantAccess").RequireRateLimiting("DocIntelApi");
        group.AddEndpointFilter<CsrfEndpointFilter>();
        group.AddEndpointFilter(async (invocation, next) =>
        {
            try { return await next(invocation); }
            catch (TenantUsersConflictException exception) { return Results.Conflict(new { error = exception.Message }); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (Exception exception) when (exception is HttpRequestException or AuthenticationFailedException or
                JsonException or InvalidDataException)
            {
                app.Logger.LogWarning("Enterprise people operation unavailable: {ErrorType}; trace {TraceId}.",
                    exception.GetType().Name, invocation.HttpContext.TraceIdentifier);
                return Results.Problem(statusCode: 503,
                    title: "Enterprise directory or access storage is unavailable. No access change was confirmed.");
            }
        });
        group.MapGet("", async (string tenantId, int? offset, int? limit,
            TenantUserService users, CancellationToken ct) =>
            Results.Ok(await users.ListAsync(tenantId, offset ?? 0, limit ?? 50, ct)));
        group.MapGet("/directory", async (string tenantId, string q, IEnterpriseDirectory directory,
            TenantUserService users, CancellationToken ct) =>
        {
            if (!directory.IsConfigured)
                return Results.Problem(statusCode: 503, title: "The enterprise directory is not connected.");
            return Results.Ok(new { people = await users.SearchAssignableAsync(tenantId, q, ct) });
        });
        group.MapGet("/photo/{objectId:guid}", async (Guid objectId,
            IEnterpriseDirectory directory, HttpContext context, CancellationToken ct) =>
        {
            if (!directory.IsConfigured) return Results.NotFound();
            var person = await directory.GetAsync(objectId, ct);
            if (person is not { Member: true }) return Results.NotFound();
            var photo = await directory.GetPhotoAsync(objectId, ct);
            context.Response.Headers.CacheControl = "no-store";
            return photo is null ? Results.NotFound() : Results.File(photo.Bytes, photo.ContentType);
        });
        group.MapPut("", async (string tenantId, ChangeTenantUsersRequest request, HttpContext context,
            IdentityAccessService identity, TenantUserService users, CancellationToken ct) =>
        {
            var actor = IdentityAccessService.GetActor(context.User);
            if (!identity.IsLocalDevelopment && (actor is null || identity.IsServicePrincipal(actor))) return Results.Forbid();
            var version = await users.ChangeAsync(tenantId, actor, identity.IsLocalDevelopment,
                request.Version, request.Changes, ct);
            return Results.Ok(new { version });
        });
    }

    /// <summary>Gate HTML entry points as well as the underlying APIs; static scripts remain non-sensitive resources.</summary>
    public static void UseDocIntelUiAccess(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var admin = context.Request.Path == "/admin" || context.Request.Path == "/admin.html";
            var workspace = context.Request.Path == "/" || context.Request.Path == "/index.html";
            if (!admin && !workspace) { await next(); return; }
            context.Response.Headers.CacheControl = "no-store";
            if (context.User.Identity?.IsAuthenticated != true)
            {
                context.Response.Redirect("/auth/login?returnUrl=" +
                    Uri.EscapeDataString(context.Request.Path + context.Request.QueryString));
                return;
            }
            var access = context.RequestServices.GetRequiredService<IdentityAccessService>();
            bool allowed;
            if (context.Request.Query.TryGetValue("tenant", out var tenantId) && !String.IsNullOrWhiteSpace(tenantId))
            {
                var scope = admin
                    ? await access.GetAccessAsync(context.User, tenantId.ToString(), context.RequestAborted)
                    : await access.ResolveAsync(IdentityAccessService.GetActor(context.User),
                        tenantId.ToString(), context.RequestAborted);
                allowed = admin ? scope.CanAdmin : scope.CanRead;
            }
            else
            {
                allowed = admin ? await access.CanAdministerAnyTenantAsync(context.User, context.RequestAborted)
                    : await access.HasAnyMembershipAsync(context.User, context.RequestAborted);
            }
            if (!allowed)
            {
                context.Response.StatusCode = 403;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(
                    "<!doctype html><html lang=\"en\"><head><title>Access required - DocIntel</title></head>" +
                    "<body><h1>Access required</h1><p>Your account is not assigned the required workspace role. " +
                    "Contact your workspace administrator.</p><a href=\"/\">Document Intelligence</a></body></html>");
                return;
            }
            await next();
        });
    }
}
