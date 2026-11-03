using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DocIntel.Api.Identity;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Features.AskAi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DocIntel.Api.Channels;

/// <summary>Separate consumer OAuth flow that never replaces the signed-in DocIntel identity.</summary>
public static class PersonalEmailHostingExtensions
{
    public const string Scheme = "DocIntelPersonalEmail";
    private const string ActorPolicy = "DocIntelPersonalEmailActor";
    private const string SessionPolicy = "DocIntelPersonalEmailSession";
    private const string TransientScheme = "DocIntelPersonalEmailTransient";
    private const string RequestItem = "personal-email-request";
    private const string CacheItem = "personal-email-cache";
    private const string AccountItem = "personal-email-account";
    private const string TokenItem = "personal-email-token";
    private const string ExpiryItem = "personal-email-expiry";

    public static void AddDocIntelPersonalEmail(
        this WebApplicationBuilder builder, EmailChannelOptions settings, IdentityOptions identity)
    {
        if (!settings.Personal.IsConfigured) return;
        if (settings.Personal.ClientId == identity.ClientId || settings.Personal.ClientId == identity.ApiClientId)
            throw new InvalidOperationException("Personal email requires a separate Microsoft OAuth registration.");
        builder.Services.AddHttpClient(PersonalEmailOAuthClient.HttpClientName, client =>
            client.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        var privateDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "personal-email");
        var development = builder.Environment.IsDevelopment();
        builder.Services.AddSingleton<IPersonalEmailStateStore>(services =>
        {
            var protection = services.GetRequiredService<IDataProtectionProvider>();
            if (services.GetService<CloudAskAiClients>() is { } cloud)
                return new BlobPersonalEmailStateStore(cloud.Runs, protection);
            if (!development)
                throw new InvalidOperationException("Personal email requires shared private execution storage in production.");
            return new FilePersonalEmailStateStore(privateDirectory, protection);
        });
        builder.Services.AddSingleton<IPersonalEmailOAuthClient, PersonalEmailOAuthClient>();
        builder.Services.AddSingleton<IPersonalEmailConnections, PersonalEmailConnections>();
        builder.Services.AddAuthorization(authorization =>
        {
            authorization.AddPolicy(ActorPolicy, policy => policy.RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    IdentityAccessService.GetActor(context.User)?.DirectoryTenantId == settings.TenantId));
            authorization.AddPolicy(SessionPolicy, policy => policy
                .AddAuthenticationSchemes(IdentityHostingExtensions.SessionScheme).RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    IdentityAccessService.GetActor(context.User)?.DirectoryTenantId == settings.TenantId));
        });
        builder.Services.AddAuthentication()
            .AddCookie(TransientScheme, options =>
            {
                options.Cookie.Name = ".DocIntel.PersonalEmailTransient";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(1);
                options.SlidingExpiration = false;
            })
            .AddOpenIdConnect(Scheme, options =>
            {
                options.SignInScheme = TransientScheme;
                options.Authority = PersonalEmailOAuthClient.Authority;
                options.ClientId = settings.Personal.ClientId.ToString("D");
                options.ClientSecret = settings.Personal.ClientSecret;
                options.CallbackPath = PersonalEmailOAuthClient.CallbackPath;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.UsePkce = true;
                options.SaveTokens = false;
                options.MapInboundClaims = false;
                options.BackchannelTimeout = TimeSpan.FromSeconds(60);
                options.BackchannelHttpHandler = new SocketsHttpHandler { AllowAutoRedirect = false };
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.Scope.Clear();
                foreach (var scope in new[] { "openid", "profile", "offline_access", "User.Read" })
                    options.Scope.Add(scope);
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    AuthenticationType = Scheme,
                    ValidateIssuer = true,
                    ValidIssuer = PersonalEmailOAuthClient.Issuer,
                    ValidateAudience = true,
                    ValidAudience = settings.Personal.ClientId.ToString("D"),
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
                options.Events.OnRedirectToIdentityProvider = async context =>
                {
                    var actor = await OriginalActorAsync(context.HttpContext);
                    var connections = context.HttpContext.RequestServices.GetRequiredService<IPersonalEmailConnections>();
                    var pending = await connections.GetPendingAsync(actor, RequestId(context.Properties),
                        context.HttpContext.RequestAborted);
                    context.ProtocolMessage.RedirectUri = settings.PublicOrigin.TrimEnd('/') +
                        PersonalEmailOAuthClient.CallbackPath;
                    context.ProtocolMessage.Scope = "openid profile offline_access " +
                        String.Join(' ', PersonalEmailOAuthClient.Scopes(pending.Purpose));
                    context.ProtocolMessage.Prompt = "select_account";
                    if (pending.Purpose == PersonalEmailPurpose.Mailbox)
                        context.ProtocolMessage.LoginHint = settings.MailboxAddress;
                };
                options.Events.OnAuthorizationCodeReceived = async context =>
                {
                    var actor = await OriginalActorAsync(context.HttpContext);
                    var connections = context.HttpContext.RequestServices.GetRequiredService<IPersonalEmailConnections>();
                    var pending = await connections.GetPendingAsync(actor, RequestId(context.Properties),
                        context.HttpContext.RequestAborted);
                    if (context.TokenEndpointRequest is null ||
                        !context.TokenEndpointRequest.Parameters.TryGetValue("code_verifier", out var verifier) ||
                        String.IsNullOrWhiteSpace(verifier))
                        throw new UnauthorizedAccessException("The email connection proof is unavailable.");
                    var oauth = context.HttpContext.RequestServices.GetRequiredService<IPersonalEmailOAuthClient>();
                    var token = await oauth.RedeemAsync(context.ProtocolMessage.Code, verifier, pending.Purpose,
                        context.HttpContext.RequestAborted);
                    if (String.IsNullOrWhiteSpace(token.IdToken))
                        throw new InvalidDataException("Microsoft returned no signed personal account identity.");
                    context.Properties.Items[CacheItem] = Convert.ToBase64String(token.TokenCache);
                    context.Properties.Items[AccountItem] = token.HomeAccountId;
                    context.Properties.Items[TokenItem] = token.AccessToken;
                    context.Properties.Items[ExpiryItem] = token.ExpiresAtUtc.ToUnixTimeSeconds()
                        .ToString(CultureInfo.InvariantCulture);
                    context.HandleCodeRedemption(token.AccessToken, token.IdToken);
                };
                options.Events.OnTokenValidated = PersonalEmailOAuthClient.ValidateTokenResponseAsync;
                // TicketReceived runs after framework signature, state, correlation and nonce validation.
                options.Events.OnTicketReceived = async context =>
                {
                    context.HandleResponse();
                    try
                    {
                        var actor = await OriginalActorAsync(context.HttpContext);
                        var subject = context.Principal?.FindFirst("sub")?.Value;
                        if (context.Principal?.FindFirst("tid")?.Value != PersonalEmailOAuthClient.ConsumerTenantId ||
                            String.IsNullOrWhiteSpace(subject))
                            throw new UnauthorizedAccessException("A signed personal Microsoft account is required.");
                        var items = context.Properties.Items;
                        var token = new PersonalEmailToken(
                            Required(items, AccountItem), Required(items, TokenItem),
                            DateTimeOffset.FromUnixTimeSeconds(Int64.Parse(Required(items, ExpiryItem),
                                CultureInfo.InvariantCulture)),
                            Convert.FromBase64String(Required(items, CacheItem)));
                        var connections = context.HttpContext.RequestServices.GetRequiredService<IPersonalEmailConnections>();
                        var pending = await connections.CompleteAsync(actor, RequestId(context.Properties),
                            subject, token, context.HttpContext.RequestAborted);
                        var destination = pending.Purpose == PersonalEmailPurpose.Mailbox
                            ? "/admin.html?tenant=" + Uri.EscapeDataString(settings.ApplicationTenantId) +
                                "&personal-email=connected"
                            : pending.ReturnRunId is null
                                ? "/?tenant=" + Uri.EscapeDataString(settings.ApplicationTenantId) + "&personal-email=verified"
                                : "/email-question.html?tenant=" + Uri.EscapeDataString(settings.ApplicationTenantId) +
                                    "&run=" + Uri.EscapeDataString(pending.ReturnRunId) + "&personal-email=verified";
                        context.Response.Redirect(destination);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = "Connection was not completed. Use the original DocIntel account and the configured Microsoft mailbox."
                        });
                    }
                    catch (PersonalEmailStateConflictException exception)
                    {
                        context.Response.StatusCode = StatusCodes.Status409Conflict;
                        await context.Response.WriteAsJsonAsync(new { error = exception.Message });
                    }
                    catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or
                        CryptographicException or FormatException or JsonException)
                    {
                        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger(Scheme).LogWarning("Personal email consent could not be persisted: {ErrorType}.",
                                exception.GetType().Name);
                        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = "Microsoft verification or secure email storage is unavailable. No connection was confirmed."
                        });
                    }
                    finally
                    {
                        foreach (var key in new[] { CacheItem, AccountItem, TokenItem, ExpiryItem })
                            context.Properties.Items.Remove(key);
                    }
                };
                options.Events.OnRemoteFailure = context =>
                {
                    context.HandleResponse();
                    context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(Scheme).LogWarning("Personal Microsoft account connection failed; trace {TraceId}.",
                            context.HttpContext.TraceIdentifier);
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return context.Response.WriteAsJsonAsync(new
                    {
                        error = "Microsoft account connection was not completed. Return to DocIntel and start again."
                    });
                };
            });
    }

    public static void MapDocIntelPersonalEmail(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<EmailChannelOptions>();
        if (!settings.Enabled || !settings.Personal.IsConfigured) return;
        var group = app.MapGroup("/api/harness/tenants/{tenantId}/email/personal")
            .RequireAuthorization(ActorPolicy).RequireRateLimiting("DocIntelApi");
        group.AddEndpointFilter<CsrfEndpointFilter>();
        group.AddEndpointFilter(async (invocation, next) =>
        {
            try { return await next(invocation); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (PersonalEmailStateConflictException exception)
            { return Results.Conflict(new { error = exception.Message }); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or
                CryptographicException or JsonException or PersonalEmailConnectionRequiredException)
            {
                app.Logger.LogWarning("Personal email connection unavailable: {ErrorType}; trace {TraceId}.",
                    exception.GetType().Name, invocation.HttpContext.TraceIdentifier);
                return Results.Problem(statusCode: 503,
                    title: "Email connection storage or Microsoft account verification is unavailable.");
            }
        });
        foreach (var purpose in Enum.GetValues<PersonalEmailPurpose>())
        {
            var segment = purpose == PersonalEmailPurpose.Mailbox ? "/mailbox" : "/sender";
            group.MapGet(segment, async (string tenantId, HttpContext context,
                IPersonalEmailConnections connections, CancellationToken ct) =>
                Results.Ok(await connections.GetStatusAsync(
                    RequireActor(context), tenantId, purpose, ct)));
            group.MapPost(segment + "/connect", async (string tenantId, string? returnRunId, HttpContext context,
                IPersonalEmailConnections connections, CancellationToken ct) =>
            {
                var requestId = await connections.BeginAsync(
                    RequireActor(context), tenantId, purpose, returnRunId, ct);
                return Results.Ok(new { redirectUrl = "/auth/email/personal/start?request=" + requestId });
            });
            group.MapPost(segment + "/disconnect", async (string tenantId, HttpContext context,
                IPersonalEmailConnections connections, CancellationToken ct) =>
            {
                await connections.DisconnectAsync(RequireActor(context), tenantId, purpose, ct);
                return Results.Ok(new { disconnected = true });
            });
        }
        app.MapGet("/auth/email/personal/start", async (string request, HttpContext context,
            IPersonalEmailConnections connections, CancellationToken ct) =>
        {
            var actor = await OriginalActorAsync(context);
            await connections.GetPendingAsync(actor, request, ct);
            var properties = new AuthenticationProperties();
            properties.Items[RequestItem] = request;
            return Results.Challenge(properties, [Scheme]);
        }).RequireAuthorization(SessionPolicy).RequireRateLimiting("DocIntelApi");
    }

    private static ActorIdentity RequireActor(HttpContext context) =>
        IdentityAccessService.GetActor(context.User)
            ?? throw new UnauthorizedAccessException("A signed-in DocIntel user is required.");

    private static async Task<ActorIdentity> OriginalActorAsync(HttpContext context)
    {
        var session = await context.AuthenticateAsync(IdentityHostingExtensions.SessionScheme);
        return session.Succeeded && session.Principal is { } principal &&
            IdentityAccessService.GetActor(principal) is { } actor ? actor :
            throw new UnauthorizedAccessException("The original DocIntel session expired or changed.");
    }

    private static string RequestId(AuthenticationProperties properties) => Required(properties.Items, RequestItem);
    private static string Required(IDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) && !String.IsNullOrWhiteSpace(value) ? value :
            throw new UnauthorizedAccessException("The email connection proof is unavailable.");
}
