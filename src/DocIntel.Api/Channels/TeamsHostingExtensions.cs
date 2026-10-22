using DocIntel.Api.Identity;
using DocIntel.Core.Channels;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Core;

namespace DocIntel.Api.Channels;

/// <summary>Hosts the native, authenticated Teams SDK without replacing DocIntel's browser/API policies.</summary>
public static class TeamsHostingExtensions
{
    public static void AddDocIntelTeams(this WebApplicationBuilder builder)
    {
        var options = new TeamsChannelOptions();
        var section = builder.Configuration.GetSection(TeamsChannelOptions.SectionName);
        section.Bind(options);
        options.Validate();
        builder.Services.AddSingleton(options);
        if (!options.Enabled) return;
        var identity = new IdentityOptions();
        builder.Configuration.GetSection(IdentityOptions.SectionName).Bind(identity);
        if (identity.Mode != IdentityMode.Entra || identity.DirectoryTenantId != options.TenantId ||
            new Uri(identity.PublicOrigin ?? "about:blank") != new Uri(options.PublicOrigin) ||
            section.GetValue<bool>("DangerouslyAllowUnauthenticatedRequests") ||
            section["Instance"] is { } instance && instance != "https://login.microsoftonline.com/" ||
            builder.Configuration["BotFramework:OpenIdMetadataUrl"] is { } metadata &&
                metadata != "https://login.botframework.com/v1/.well-known/openid-configuration" ||
            builder.Configuration["BotFramework:BotTokenIssuer"] is { } issuer && issuer != "https://api.botframework.com" ||
            builder.Configuration["BotFramework:GraphBaseUrl"] is { } graph && graph != "https://graph.microsoft.com")
            throw new InvalidOperationException("Teams requires matching Entra directory/application origin and standard " +
                "authenticated public-cloud Bot Framework endpoints. Authentication bypass is not supported.");

        // SDK pre-host inspection creates/disposes a temporary provider; do not let it own host configuration.
        builder.Services.AddSingleton<IConfiguration>(builder.Configuration);
        builder.Services.AddDistributedMemoryCache(o => o.SizeLimit = 4 * 1024 * 1024);
        builder.Services.AddTeamsBotApplication(TeamsChannelOptions.SectionName);
        // SDK registration changes the default authorization policy. Keep it isolated to the bot route.
        builder.Services.AddAuthorization(o =>
            o.DefaultPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.PostConfigure<JwtBearerOptions>(TeamsChannelOptions.SectionName, o =>
        {
            o.IncludeErrorDetails = false;
            o.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
            var sdkIssuerValidator = o.TokenValidationParameters.IssuerValidator ??
                throw new InvalidOperationException("The Teams SDK must supply issuer validation.");
            o.TokenValidationParameters.IssuerValidator = (issuer, token, parameters) =>
                issuer == "https://api.botframework.com"
                    ? sdkIssuerValidator(issuer, token, parameters)
                    : throw new SecurityTokenInvalidIssuerException("Only Bot Framework activity tokens are accepted.");
        });
        builder.Logging.AddFilter("Microsoft.Teams", LogLevel.Warning);
        builder.Services.AddSingleton<TeamsQuestionReceiver>();
        builder.Services.AddSingleton<IChannelTransport, TeamsChannelTransport>();
    }

    public static void MapDocIntelTeams(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<TeamsChannelOptions>().Enabled) return;
        var teams = app.Services.GetRequiredService<TeamsBotApplication>();
        var receiver = app.Services.GetRequiredService<TeamsQuestionReceiver>();
        teams.OnMessage(async (context, cancellationToken) =>
        {
            var activity = context.Activity;
            var mention = TeamsMentionParser.Parse(activity.ToJson(), activity.Recipient?.Id ?? String.Empty, activity.Text);
            var question = new VerifiedTeamsQuestion(
                activity.ChannelId?.ToString() ?? String.Empty, activity.ChannelData?.Tenant?.Id,
                activity.Conversation?.TenantId,
                activity.From?.AadObjectId, activity.Recipient?.Id, activity.Conversation?.ConversationType?.ToString(),
                activity.Conversation?.IsGroup, activity.Conversation?.Id, activity.Id, activity.ServiceUrl, mention.Text, mention.Mentioned);
            try
            {
                receiver.ValidateRouting(question);
            }
            catch (UnauthorizedAccessException)
            {
                app.Logger.LogWarning("Teams activity has an unaccepted private routing envelope.");
                return;
            }
            TeamsSubmission accepted;
            try
            {
                accepted = await receiver.ReceiveAsync(question, cancellationToken, async ct =>
                {
                    try
                    {
                        var created = await teams.ConversationClient.CreateConversationAsync(new ConversationParameters
                        {
                            IsGroup = false,
                            TenantId = question.DirectoryTenantId,
                            Bot = activity.Recipient,
                            Members = [activity.From!]
                        }, new Uri(app.Services.GetRequiredService<TeamsChannelOptions>().ServiceUrl), cancellationToken: ct);
                        return created.Id;
                    }
                    catch (HttpRequestException exception)
                    {
                        app.Logger.LogWarning("A private Teams reply could not be established: {ErrorType}. An authorized web link will be used.",
                            exception.GetType().Name);
                        return null;
                    }
                });
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException)
            {
                app.Logger.LogWarning("Teams question rejected: {ErrorType}.", exception.GetType().Name);
                await context.SendAsync(new MessageActivityInput().WithText(
                    "DocIntel could not accept this question. Check your account's document access, " +
                    "and use a non-empty question of at most 8,000 characters."), cancellationToken);
                return;
            }
            if (accepted.Created)
            {
                var message = accepted.SharedRequest
                    ? accepted.PrivateReplyAvailable
                        ? $"{accepted.AgentName} is working on your question and will reply privately. " +
                            "Document details are never posted to this shared conversation. "
                        : $"{accepted.AgentName} accepted your question. Install/open the personal bot chat for future private replies. " +
                            "Use the sign-in-required link for this answer. "
                    : $"{accepted.AgentName} accepted your question and will reply here when it completes. ";
                await context.SendAsync(new MessageActivityInput().WithText(message +
                    "Private progress and answer: " + accepted.StatusUrl), cancellationToken);
            }
        });
        app.MapPost("/api/messages", async (HttpContext context, CancellationToken cancellationToken) =>
            {
                if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                    size.MaxRequestBodySize = 64 * 1024;
                await teams.ProcessAsync(context, cancellationToken);
            })
            .RequireAuthorization(new AuthorizationPolicyBuilder(TeamsChannelOptions.SectionName)
                .RequireAuthenticatedUser().Build())
            .RequireRateLimiting("DocIntelApi");
    }
}
