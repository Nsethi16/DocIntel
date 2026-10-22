using DocIntel.Api.Identity;
using DocIntel.Core.Channels;

namespace DocIntel.Api.Channels;

/// <summary>Mail requires explicit operator configuration, server-side identity binding and tenant channel enablement.</summary>
public static class EmailHostingExtensions
{
    public static void AddDocIntelEmail(this WebApplicationBuilder builder)
    {
        var settings = new EmailChannelOptions();
        builder.Configuration.GetSection(EmailChannelOptions.SectionName).Bind(settings);
        settings.Validate();
        builder.Services.AddSingleton(settings);
        if (!settings.Enabled) return;
        var identity = new IdentityOptions();
        builder.Configuration.GetSection(IdentityOptions.SectionName).Bind(identity);
        if (identity.Mode != IdentityMode.Entra || identity.DirectoryTenantId != settings.TenantId ||
            identity.PublicOrigin?.TrimEnd('/') != settings.PublicOrigin.TrimEnd('/'))
            throw new InvalidOperationException("Email delivery requires matching production Entra identity and public origin.");
        builder.Services.AddHttpClient(GraphMailboxClient.HttpClientName, client =>
            client.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton<IMailboxClient, GraphMailboxClient>();
        builder.Services.AddSingleton<EmailQuestionReceiver>();
        builder.Services.AddSingleton<EmailQuestionConfirmation>();
        builder.Services.AddSingleton<IChannelTransport, EmailAnswerTransport>();
        builder.Services.AddHostedService<MailboxQuestionWorker>();
    }

    public static void MapDocIntelEmail(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<EmailChannelOptions>().Enabled) return;
        var group = app.MapGroup("/api/harness/tenants/{tenantId}/ask/email/runs/{runId}")
            .RequireAuthorization("TenantAccess").RequireRateLimiting("DocIntelApi");
        group.AddEndpointFilter<CsrfEndpointFilter>();
        group.MapGet("", async (string tenantId, string runId, HttpContext context,
            IdentityAccessService identity, EmailQuestionConfirmation confirmation, CancellationToken ct) =>
        {
            var actor = IdentityAccessService.GetActor(context.User);
            if (actor is null || identity.IsServicePrincipal(actor)) return Results.Forbid();
            try
            {
                var run = await confirmation.ReadAsync(actor, tenantId, runId, ct);
                return Results.Ok(new
                {
                    run.RunId,
                    run.Question,
                    status = run.Status.ToString(),
                    destination = run.PendingEmail!.Sender,
                    expiresAtUtc = run.PendingEmail.ExpiresAtUtc
                });
            }
            catch (UnauthorizedAccessException) { return Results.NotFound(); }
        });
        group.MapPost("/confirm", async (string tenantId, string runId, HttpContext context,
            IdentityAccessService identity, EmailQuestionConfirmation confirmation, CancellationToken ct) =>
        {
            var actor = IdentityAccessService.GetActor(context.User);
            if (actor is null || identity.IsServicePrincipal(actor)) return Results.Forbid();
            try
            {
                var run = await confirmation.ConfirmAsync(actor, tenantId, runId, ct);
                return Results.Accepted($"/api/harness/tenants/{Uri.EscapeDataString(tenantId)}/ask/runs/{Uri.EscapeDataString(runId)}",
                    new { run.RunId, status = run.Status.ToString() });
            }
            catch (UnauthorizedAccessException) { return Results.NotFound(); }
            catch (InvalidOperationException)
            {
                return Results.Conflict(new { error = "This email question is busy or unavailable. Refresh before trying again." });
            }
        });
    }
}
