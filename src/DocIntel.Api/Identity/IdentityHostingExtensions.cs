using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using DocIntel.Core.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DocIntel.Api.Identity;

public static class IdentityHostingExtensions
{
    public const string SessionScheme = "DocIntelSession";
    public const string BearerScheme = "DocIntelBearer";
    private const string SmartScheme = "DocIntel";
    private const string LocalScheme = "DocIntelLocal";

    public static void AddDocIntelIdentity(this WebApplicationBuilder builder)
    {
        var grants = builder.Configuration["DOCINTEL_IDENTITY_GRANTS"];
        if (!String.IsNullOrWhiteSpace(grants))
        {
            if (grants.Length > 65_536) throw new InvalidOperationException("Identity grants configuration is too large.");
            using var document = JsonDocument.Parse(grants);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Any(property =>
                    !String.Equals(property.Name, "Memberships", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(property.Name, "PlatformAdministrators", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Identity grant input may configure memberships and platform administrators only.");
            var values = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, JsonElement>
            {
                [IdentityOptions.SectionName] = document.RootElement.Clone()
            });
            builder.Configuration.AddJsonStream(new MemoryStream(values));
        }
        var settings = new IdentityOptions();
        builder.Configuration.GetSection(IdentityOptions.SectionName).Bind(settings);
        settings.Validate(builder.Environment, builder.Configuration["urls"]);
        if (builder.Configuration.GetValue<bool>("Models:AllowDevelopmentSimulation") &&
            (!builder.Environment.IsDevelopment() || String.IsNullOrWhiteSpace(builder.Configuration["urls"]) ||
             !builder.Configuration["urls"]!.Split(';', StringSplitOptions.RemoveEmptyEntries)
                 .All(IdentityOptions.IsLoopbackUrl)))
        {
            throw new InvalidOperationException("Development simulation requires an explicit loopback Development host.");
        }
        if (!String.IsNullOrWhiteSpace(builder.Configuration["AzureAI:DevelopmentStorageConnectionString"]) &&
            (!builder.Environment.IsDevelopment() || String.IsNullOrWhiteSpace(builder.Configuration["urls"]) ||
             !builder.Configuration["urls"]!.Split(';', StringSplitOptions.RemoveEmptyEntries)
                 .All(IdentityOptions.IsLoopbackUrl)))
            throw new InvalidOperationException("Storage emulator connections require a loopback Development host.");
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Services.AddSingleton(settings);
        var webRoot = builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
        RequirePrivateDirectory(settings.DataProtectionDirectory ??
            Path.Combine(builder.Environment.ContentRootPath, "App_Data", "identity-keys"), webRoot);
        var runDirectory = builder.Configuration["AskAi:RunStorageDirectory"];
        if (!String.IsNullOrWhiteSpace(runDirectory)) RequirePrivateDirectory(runDirectory, webRoot);
        var jobDatabase = builder.Configuration["DocumentJobs:DatabasePath"];
        if (!String.IsNullOrWhiteSpace(jobDatabase)) RequirePrivateDirectory(jobDatabase, webRoot);
        builder.Services.AddOptions<IdentityOptions>()
            .Bind(builder.Configuration.GetSection(IdentityOptions.SectionName))
            .Validate(o => o.Mode == settings.Mode && o.DirectoryTenantId == settings.DirectoryTenantId &&
                           o.ClientId == settings.ClientId && o.ApiClientId == settings.ApiClientId &&
                           o.HasValidMemberships(), "Invalid identity configuration or membership reload.")
            .ValidateOnStart();
        builder.Services.AddSingleton<IdentityAccessService>();
        builder.Services.AddSingleton<ITenantAccessResolver>(sp => sp.GetRequiredService<IdentityAccessService>());
        var keyDirectory = settings.DataProtectionDirectory ??
            Path.Combine(builder.Environment.ContentRootPath, "App_Data", "identity-keys");
        var protection = builder.Services.AddDataProtection().SetApplicationName($"DocIntel:{settings.ClientId}");
        if (String.IsNullOrWhiteSpace(settings.DataProtectionBlobUri))
        {
            protection.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
        }
        else
        {
            var uri = new Uri(settings.DataProtectionBlobUri);
            protection.PersistKeysToAzureBlobStorage(services =>
            {
                if (uri.Scheme == Uri.UriSchemeHttps)
                    return new BlobClient(uri, services.GetRequiredService<TokenCredential>());
                var storage = services.GetRequiredService<BlobServiceClient>();
                if (!storage.Uri.IsBaseOf(uri))
                    throw new InvalidOperationException("The identity key emulator must match the configured storage account.");
                var path = new BlobUriBuilder(uri);
                return storage.GetBlobContainerClient(path.BlobContainerName).GetBlobClient(path.BlobName);
            });
        }
        if (!String.IsNullOrWhiteSpace(settings.DataProtectionKeyIdentifier))
        {
            protection.ProtectKeysWithAzureKeyVault(new Uri(settings.DataProtectionKeyIdentifier),
                services => services.GetRequiredService<TokenCredential>());
        }
        var development = builder.Environment.IsDevelopment();
        builder.Services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-DocIntel-CSRF";
            o.Cookie.Name = ".DocIntel.Csrf";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
        });
        builder.Services.AddRateLimiter(o =>
        {
            o.AddPolicy("DocIntelApi", context =>
            {
                var read = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
                var key = IdentityAccessService.GetActor(context.User)?.ToString() ??
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter((key, read), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = read ? settings.ReadRequestsPerMinute : settings.WriteRequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
            o.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Ceiling(retry.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "The request limit was reached. Wait before checking or submitting again." },
                    cancellationToken);
            };
        });
        var authentication = builder.Services.AddAuthentication(SmartScheme)
            .AddPolicyScheme(SmartScheme, SmartScheme, o => o.ForwardDefaultSelector = context =>
                settings.Mode == IdentityMode.LocalDevelopment ? LocalScheme :
                context.Request.Headers.ContainsKey("Authorization") ? BearerScheme : SessionScheme)
            .AddScheme<AuthenticationSchemeOptions, LocalDevelopmentHandler>(LocalScheme, _ => { });
        authentication.AddCookie(SessionScheme, o =>
        {
            o.Cookie.Name = ".DocIntel.Session";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            o.SlidingExpiration = false;
            o.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        if (settings.Mode == IdentityMode.Entra)
        {
            var authority = settings.AllowLoopbackAuthority ? settings.LoopbackAuthority! :
                $"https://login.microsoftonline.com/{settings.DirectoryTenantId:D}/v2.0";
            var managedClientId = settings.ManagedIdentityClientId ?? Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
            TokenCredential? assertionCredential = !settings.UseManagedIdentityAssertion ? null :
                String.IsNullOrWhiteSpace(managedClientId)
                    ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
                    : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(managedClientId));
            authentication.AddJwtBearer(BearerScheme, o =>
            {
                o.Authority = authority;
                o.Audience = settings.ApiClientId.ToString("D");
                o.RequireHttpsMetadata = !settings.AllowLoopbackAuthority;
                o.MapInboundClaims = false;
                o.IncludeErrorDetails = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    AuthenticationType = BearerScheme,
                    ValidateIssuerSigningKey = true,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
                o.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var actor = IdentityAccessService.GetActor(context.Principal!);
                        var scopes = context.Principal!.FindFirst("scp")?.Value.Split(' ') ?? [];
                        var access = context.HttpContext.RequestServices.GetRequiredService<IdentityAccessService>();
                        if (actor?.DirectoryTenantId != settings.DirectoryTenantId ||
                            (!scopes.Contains(settings.ApiAccessPermission, StringComparer.Ordinal) &&
                             !(actor is not null && access.IsServicePrincipal(actor) &&
                               context.Principal.FindAll("roles").Any(c =>
                                   String.Equals(c.Value, settings.ApiAccessPermission, StringComparison.Ordinal)))))
                        {
                            context.Fail("A verified API access grant is required.");
                        }
                        return Task.CompletedTask;
                    }
                };
            });
            authentication.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                o.SignInScheme = SessionScheme;
                o.Authority = authority;
                o.ClientId = settings.ClientId.ToString("D");
                o.ClientSecret = settings.UseManagedIdentityAssertion ? null : settings.ClientSecret;
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.ResponseMode = OpenIdConnectResponseMode.Query;
                o.CorrelationCookie.SameSite = SameSiteMode.Lax;
                o.NonceCookie.SameSite = SameSiteMode.Lax;
                o.UsePkce = true;
                o.MapInboundClaims = false;
                o.SaveTokens = false;
                o.RequireHttpsMetadata = !settings.AllowLoopbackAuthority;
                o.TokenValidationParameters.AuthenticationType = SessionScheme;
                o.CallbackPath = "/signin-oidc";
                if (development)
                {
                    o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    o.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                }
                o.Events.OnRedirectToIdentityProvider = context =>
                {
                    if (!String.IsNullOrWhiteSpace(settings.PublicOrigin))
                    {
                        context.ProtocolMessage.RedirectUri = settings.PublicOrigin.TrimEnd('/') + "/signin-oidc";
                    }
                    return Task.CompletedTask;
                };
                o.Events.OnAuthorizationCodeReceived = async context =>
                {
                    if (!String.IsNullOrWhiteSpace(settings.PublicOrigin))
                    {
                        context.TokenEndpointRequest!.RedirectUri =
                            settings.PublicOrigin.TrimEnd('/') + "/signin-oidc";
                    }
                    if (settings.UseManagedIdentityAssertion)
                    {
                        var assertion = await assertionCredential!.GetTokenAsync(
                            new TokenRequestContext(["api://AzureADTokenExchange/.default"]),
                            context.HttpContext.RequestAborted);
                        context.TokenEndpointRequest!.ClientSecret = null;
                        context.TokenEndpointRequest.ClientAssertion = assertion.Token;
                        context.TokenEndpointRequest.ClientAssertionType =
                            "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
                    }
                };
                o.Events.OnTokenValidated = context =>
                {
                    var actor = IdentityAccessService.GetActor(context.Principal!);
                    if (actor?.DirectoryTenantId != settings.DirectoryTenantId)
                    {
                        context.Fail("A verified directory identity is required.");
                    }
                    else
                    {
                        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim("tid", actor.DirectoryTenantId.ToString("D")),
                            new Claim("oid", actor.ObjectId.ToString("D")),
                            new Claim(ClaimTypes.NameIdentifier, $"{actor.DirectoryTenantId:D}:{actor.ObjectId:D}")
                        ], SessionScheme));
                    }
                    return Task.CompletedTask;
                };
                o.Events.OnRemoteFailure = context =>
                {
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return context.Response.WriteAsJsonAsync(new { error = "Sign-in could not be completed." });
                };
            });
        }
        builder.Services.AddSingleton<IAuthorizationHandler, TenantAccessHandler>();
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, TenantAuthorizationResultHandler>();
        builder.Services.AddAuthorization(o => o.AddPolicy("TenantAccess",
            policy => policy.RequireAuthenticatedUser().AddRequirements(new TenantAccessRequirement())));
    }

    private static void RequirePrivateDirectory(string directory, string webRoot)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(webRoot), Path.GetFullPath(directory));
        if (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Private run snapshots and identity keys must be outside the web root.");
        }
    }

    public static void MapDocIntelIdentity(this WebApplication app)
    {
        app.MapGet("/auth/login", (string? returnUrl, IdentityOptions settings) =>
        {
            if (settings.Mode == IdentityMode.LocalDevelopment)
            {
                return Results.LocalRedirect("/");
            }
            var destination = returnUrl ?? "/";
            if (!destination.StartsWith('/') || destination.StartsWith("//", StringComparison.Ordinal) ||
                destination.Contains('\\') || destination.Any(Char.IsControl))
            {
                return Results.BadRequest(new { error = "A local return URL is required." });
            }
            return Results.Challenge(new AuthenticationProperties { RedirectUri = destination },
                [OpenIdConnectDefaults.AuthenticationScheme]);
        }).AllowAnonymous();
        app.MapGet("/api/identity/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken });
        }).RequireAuthorization();
        app.MapGet("/api/identity/me", async (HttpContext context, IdentityAccessService access, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var memberships = new List<object>();
            var ids = await access.GetTenantIdsAsync(context.User, cancellationToken: ct);
            if (ids is not null)
                foreach (var id in ids)
                {
                    var scope = await access.GetAccessAsync(context.User, id, ct);
                    memberships.Add(new { tenantId = id, canAdmin = scope.CanAdmin, canProcess = scope.CanProcess });
                }
            return Results.Ok(new
            {
                localDevelopment = access.IsLocalDevelopment,
                hasMembership = await access.HasAnyMembershipAsync(context.User, ct),
                platformAdministrator = await access.IsPlatformAdministratorAsync(context.User, ct),
                canAdministerAnyTenant = await access.CanAdministerAnyTenantAsync(context.User, ct),
                tenants = memberships,
                chatPartition = access.GetChatPartition(context.User)
            });
        }).RequireAuthorization();
        app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest(new { error = "A valid request verification token is required." });
            }
            await context.SignOutAsync(SessionScheme);
            return Results.Ok(new { signedOut = true });
        }).RequireAuthorization();
        app.MapGet("/auth/signed-out", () => Results.Content(
            "<!doctype html><html lang=\"en\"><head><title>DocIntel</title></head>" +
            "<body><h1>You are signed out of DocIntel</h1><a href=\"/auth/login\">Sign in</a></body></html>",
            "text/html")).AllowAnonymous();
    }
}

internal sealed class LocalDevelopmentHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IdentityOptions identity) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (identity.Mode != IdentityMode.LocalDevelopment || Context.Connection.RemoteIpAddress is not { } address ||
            !IPAddress.IsLoopback(address))
        {
            return Task.FromResult(AuthenticateResult.Fail("Loopback development identity is not available."));
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "DocIntel-Loopback-Development")], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
