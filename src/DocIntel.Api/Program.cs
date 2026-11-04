using System.Text.Json;
using System.Text.Json.Serialization;
using DocIntel.Api.Endpoints;
using DocIntel.Api.Identity;
using DocIntel.Api.Workers;
using DocIntel.Api.Channels;
using DocIntel.Api.Http;
using DocIntel.Api.Diagnostics;
using DocIntel.Core;
using DocIntel.Core.Models;
using DocIntel.ServiceDefaults;
using Microsoft.AspNetCore.Http.Features;

var directoryDiagnosticMode = DirectoryConnectionDiagnostic.IsRequested(args);
var nativeDecisionDiagnosticMode = NativeDecisionDiagnostic.IsRequested(args);
var isolatedDiagnosticMode = directoryDiagnosticMode || nativeDecisionDiagnosticMode;
var askContractMode = args.Contains("--diagnose-ask-contract", StringComparer.Ordinal);
var askSyntheticMode = args.Contains("--diagnose-ask-synthetic", StringComparer.Ordinal);
var askDataMode = args.Contains("--diagnose-ask-data", StringComparer.Ordinal);
var emailPreviewMode = args.Contains("--diagnose-email-preview", StringComparer.Ordinal);
var documentDiagnosticMode = args.Contains("--diagnose-document", StringComparer.Ordinal);
var documentReprocessMode = args.Contains("--reprocess-document", StringComparer.Ordinal);
var processingSyntheticMode = args.Contains("--diagnose-processing-synthetic", StringComparer.Ordinal);
var translationSyntheticMode = args.Contains("--diagnose-translation-synthetic", StringComparer.Ordinal);
var customerAcceptanceMode = args.Contains("--diagnose-customer-acceptance", StringComparer.Ordinal);
var diagnosticMode = args.Contains("--diagnose-ask-run", StringComparer.Ordinal) ||
    askContractMode || askSyntheticMode || askDataMode || emailPreviewMode || documentDiagnosticMode ||
    documentReprocessMode || processingSyntheticMode || translationSyntheticMode || customerAcceptanceMode;
WebApplicationBuilder builder;
try
{
    builder = WebApplication.CreateBuilder(isolatedDiagnosticMode ? [] : args);
}
catch (Exception) when (isolatedDiagnosticMode)
{
    Environment.ExitCode = nativeDecisionDiagnosticMode
        ? await NativeDecisionDiagnostic.WriteUnavailableAsync(Console.Out)
        : await DirectoryConnectionDiagnostic.WriteUnavailableAsync(Console.Out);
    return;
}
if (diagnosticMode || isolatedDiagnosticMode)
{
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.None);
}

if (nativeDecisionDiagnosticMode)
{
    Environment.ExitCode = await NativeDecisionDiagnostic.ExecuteConfiguredAsync(
        args, builder.Configuration, builder.Environment, builder.Services,
        Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"), Console.Out);
    return;
}

if (directoryDiagnosticMode)
{
    Environment.ExitCode = await DirectoryConnectionDiagnostic.ExecuteAsync(
        args, builder.Configuration, builder.Environment, builder.Services,
        Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"), Console.Out);
    return;
}

builder.AddServiceDefaults();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = HttpSafetyExtensions.MaximumRequestBodyBytes);
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = HttpSafetyExtensions.MaximumRequestBodyBytes;
    options.ValueLengthLimit = 64 * 1024;
    options.ValueCountLimit = 16;
});
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<DocIntelExceptionHandler>();
builder.AddDocIntelIdentity();
builder.AddDocIntelTenantUsers();
builder.AddDocIntelTeams();
builder.AddDocIntelEmail();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var harnessFilePath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "agent-harness-settings.json");
builder.Services.AddHttpClient(MicrosoftDecisionClient.HttpClientName, client =>
    client.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<MicrosoftDecisionHttpClientFactory>(services =>
    () => services.GetRequiredService<IHttpClientFactory>().CreateClient(MicrosoftDecisionClient.HttpClientName));
builder.Services.AddHttpClient("DocumentLanguage", client => client.Timeout = TimeSpan.FromSeconds(45))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<DocIntel.Core.Features.DocumentTranslation.DocumentLanguageHttpClientFactory>(services =>
    () => services.GetRequiredService<IHttpClientFactory>().CreateClient("DocumentLanguage"));
builder.Services.AddDocIntelCore(configuration: builder.Configuration, harnessFilePath: harnessFilePath);
var startupReadiness = new StartupReadinessOptions();
builder.Configuration.GetSection("Readiness").Bind(startupReadiness);
if (startupReadiness.TimeoutSeconds is < 1 or > 300)
    throw new InvalidOperationException("Startup readiness timeout must be between 1 and 300 seconds.");
builder.Services.AddSingleton(startupReadiness);
builder.Services.AddSingleton<ProviderStartupCheck>();
builder.Services.AddHostedService(services => services.GetRequiredService<ProviderStartupCheck>());
builder.Services.AddHealthChecks().AddCheck<ProviderStartupCheck>("providers", tags: ["ready"]);
builder.Services.AddHostedService<DocumentJobWorker>();
builder.Services.AddHostedService<AskAiRunWorker>();
builder.Services.AddHealthChecks().AddCheck<CloudAskAiReadinessCheck>("execution-storage", tags: ["ready"]);

var app = builder.Build();
if (emailPreviewMode)
{
    Environment.ExitCode = await AskAiRunDiagnostic.ExecuteEmailPreviewAsync(
        builder.Configuration["diagnose-tenant"] ?? String.Empty,
        builder.Configuration["diagnose-email-preview"] ?? String.Empty,
        builder.Configuration["diagnose-confirmation"] == "PREVIEW", startupReadiness.ProbeModelsOnStartup,
        app.Services.GetRequiredService<DocIntel.Core.Features.AskAi.IAskAiRunStore>(),
        app.Services.GetRequiredService<DocIntel.Core.Features.AskAi.AskAiOrchestrator>(), Console.Out);
    await app.DisposeAsync();
    return;
}
if (customerAcceptanceMode)
{
    Environment.ExitCode = await CustomerAcceptanceDiagnostic.ExecuteAsync(
        builder.Configuration["diagnose-tenant"] ?? String.Empty,
        builder.Configuration["diagnose-customer-acceptance"] ?? String.Empty,
        builder.Configuration["diagnose-customer-case"] ?? String.Empty,
        startupReadiness.ProbeModelsOnStartup &&
            String.Equals(builder.Configuration["diagnose-customer-live"], "true", StringComparison.Ordinal),
        app.Services.GetRequiredService<DocIntel.Core.Harness.IHarnessStore>(),
        app.Services.GetRequiredService<DocIntel.Core.Models.IModelCatalog>(),
        app.Services.GetRequiredService<DocIntel.Core.Jobs.IJobStore>(),
        app.Services.GetRequiredService<DocIntel.Core.Authorization.ITenantAccessResolver>(),
        app.Services.GetRequiredService<DocIntel.Core.Storage.IArtifactStore>(),
        app.Services.GetRequiredService<DocIntel.Core.Features.DocumentSplitting.IPdfSlicer>(),
        app.Services.GetRequiredService<DocIntel.Core.Features.AskAi.IAskAiRunStore>(), Console.Out,
        executionOptions: app.Services.GetRequiredService<DocIntel.Core.Features.AskAi.AskAiExecutionOptions>(),
        queue: app.Services.GetRequiredService<DocIntel.Core.Jobs.IJobQueue>());
    await app.DisposeAsync();
    return;
}
if (documentReprocessMode)
{
    Environment.ExitCode = await ProcessingAcceptanceDiagnostic.ReprocessAsync(
        builder.Configuration["diagnose-tenant"] ?? String.Empty,
        builder.Configuration["reprocess-document"] ?? String.Empty, startupReadiness.ProbeModelsOnStartup,
        app.Services.GetRequiredService<DocIntel.Core.Jobs.IJobStore>(),
        app.Services.GetRequiredService<DocIntel.Core.Jobs.IJobQueue>(),
        app.Services.GetRequiredService<DocIntel.Core.Authorization.ITenantAccessResolver>(),
        app.Services.GetRequiredService<DocIntel.Core.Storage.IArtifactStore>(), Console.Out);
    await app.DisposeAsync();
    return;
}
if (translationSyntheticMode)
{
    Environment.ExitCode = await ProcessingAcceptanceDiagnostic.TranslationAsync(
        builder.Configuration["diagnose-tenant"] ?? String.Empty, startupReadiness.ProbeModelsOnStartup,
        app.Services.GetRequiredService<DocIntel.Core.Features.DocumentTranslation.IDocumentTranslationService>(),
        app.Services.GetRequiredService<DocIntel.Core.Features.DocumentTranslation.IDocumentLanguageDetector>(),
        app.Services.GetRequiredService<DocIntel.Core.AzureIntegrationOptions>(), Console.Out);
    await app.DisposeAsync();
    return;
}
if (processingSyntheticMode)
{
    Environment.ExitCode = await ProcessingAcceptanceDiagnostic.PartialAsync(
        builder.Configuration["diagnose-tenant"] ?? String.Empty, startupReadiness.ProbeModelsOnStartup,
        app.Services.GetRequiredService<DocIntel.Core.Harness.IHarnessStore>(),
        app.Services.GetRequiredService<DocIntel.Core.Models.IModelCatalog>(), Console.Out);
    await app.DisposeAsync();
    return;
}
if (documentDiagnosticMode)
{
    Environment.ExitCode = await DocumentJobDiagnostic.ExecuteAsync(
        builder.Configuration["diagnose-tenant"] ?? String.Empty,
        builder.Configuration["diagnose-document"] ?? String.Empty,
        String.Equals(builder.Configuration["diagnose-translation-probe"], "true", StringComparison.Ordinal),
        startupReadiness.ProbeModelsOnStartup,
        app.Services.GetRequiredService<DocIntel.Core.Jobs.IJobStore>(),
        app.Services.GetRequiredService<DocIntel.Core.Authorization.ITenantAccessResolver>(),
        app.Services.GetRequiredService<DocIntel.Core.Storage.IArtifactStore>(),
        app.Services.GetRequiredService<DocIntel.Core.Features.DocumentSplitting.IPdfSlicer>(),
        app.Services.GetRequiredService<DocIntel.Core.Features.DocumentTranslation.IDocumentTranslationService>(),
        app.Services.GetRequiredService<DocIntel.Core.AzureIntegrationOptions>(), Console.Out,
        variant: builder.Configuration["diagnose-translation-variant"] ?? "Configured");
    await app.DisposeAsync();
    return;
}
if (askDataMode)
{
    Environment.ExitCode = await AskAiDataDiagnostic.ExecuteAsync(
        builder.Configuration["diagnose-tenant"] ?? String.Empty,
        builder.Configuration["diagnose-ask-data"] ?? String.Empty,
        app.Services.GetRequiredService<DocIntel.Core.Features.AskAi.IAskAiRunStore>(),
        app.Services.GetRequiredService<DocIntel.Core.Authorization.ITenantAccessResolver>(),
        app.Services.GetRequiredService<DocIntel.Core.Jobs.IJobStore>(), Console.Out);
    await app.DisposeAsync();
    return;
}
if (askSyntheticMode)
{
    Environment.ExitCode = await AskAiSyntheticDiagnostic.ExecuteAsync(
        builder.Configuration["diagnose-tenant"] ?? String.Empty, startupReadiness.ProbeModelsOnStartup,
        app.Services.GetRequiredService<DocIntel.Core.Harness.IHarnessStore>(),
        app.Services.GetRequiredService<DocIntel.Core.Models.IModelCatalog>(), Console.Out,
        executionOptions: app.Services.GetRequiredService<DocIntel.Core.Features.AskAi.AskAiExecutionOptions>());
    await app.DisposeAsync();
    return;
}
if (askContractMode)
{
    Environment.ExitCode = await AskAiContractDiagnostic.ExecuteAsync(
        builder.Configuration["diagnose-tenant"] ?? String.Empty,
        startupReadiness.ProbeModelsOnStartup,
        app.Services.GetRequiredService<DocIntel.Core.Harness.IHarnessStore>(),
        app.Services.GetRequiredService<DocIntel.Core.Models.IModelCatalog>(), Console.Out,
        executionOptions: app.Services.GetRequiredService<DocIntel.Core.Features.AskAi.AskAiExecutionOptions>());
    await app.DisposeAsync();
    return;
}
if (diagnosticMode)
{
    var tenantId = builder.Configuration["diagnose-tenant"] ?? String.Empty;
    var runId = builder.Configuration["diagnose-ask-run"] ?? String.Empty;
    var replay = String.Equals(builder.Configuration["diagnose-replay"], "true", StringComparison.Ordinal);
    var emailResult = runId == "recent" && !replay
        ? await EmailChannelDiagnostic.ExecuteAsync(
            tenantId, app.Services.GetRequiredService<DocIntel.Core.Channels.EmailChannelOptions>(),
            app.Services.GetRequiredService<DocIntel.Core.Harness.IHarnessStore>(),
            app.Services.GetService<IPersonalEmailStateStore>(),
            app.Services.GetService<DocIntel.Core.Authorization.IAssignedEmailAuthorization>(),
            app.Services.GetService<DocIntel.Core.Channels.IMailboxClient>(), Console.Out)
        : 0;
    Environment.ExitCode = await AskAiRunDiagnostic.ExecuteAsync(
        tenantId, runId, replay,
        app.Services.GetRequiredService<DocIntel.Core.Features.AskAi.IAskAiRunStore>(),
        app.Services.GetRequiredService<DocIntel.Core.Features.AskAi.AskAiOrchestrator>(),
        Console.Out);
    if (emailResult != 0) Environment.ExitCode = emailResult;
    await app.DisposeAsync();
    return;
}

app.UseDocIntelResponseHeaders();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    SuppressDiagnosticsCallback = _ => true
});
app.UseDocIntelIngress(builder.Configuration, app.Environment);
app.UseStatusCodePages(async status =>
    await DocIntelExceptionHandler.CreateProblem(status.HttpContext, status.HttpContext.Response.StatusCode)
        .ExecuteAsync(status.HttpContext));
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true && context.Request.Path.StartsWithSegments("/api"))
    {
        var access = context.RequestServices.GetRequiredService<IdentityAccessService>();
        context.Response.Headers["X-DocIntel-Identity"] = access.GetChatPartition(context.User);
    }
    await next();
});
app.UseRateLimiter();
app.UseAuthorization();
app.UseDocIntelUiAccess();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers.Pragma = "no-cache";
            ctx.Context.Response.Headers.Expires = "0";
        }
        else if (ctx.File.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                 ctx.File.Name.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    }
});

app.MapGet("/admin", () => Results.Redirect("/admin.html"));
app.MapDefaultEndpoints();
app.MapDocIntelIdentity();
app.MapDocIntelTenantUsers();
app.MapDocIntelTeams();
app.MapDocIntelEmail();
app.MapHarnessEndpoints();
app.MapOcrUsageEndpoints();
app.MapGet("/api/harness/tenants/{tenantId}/reviews", () =>
    Results.Problem(statusCode: 410, title: "Human review has been retired.")).RequireAuthorization("TenantAccess");
app.MapMethods("/api/harness/tenants/{tenantId}/jobs/{jobId}/review/{**action}", ["GET", "POST"], () =>
    Results.Problem(statusCode: 410, title: "Human review has been retired.")).RequireAuthorization("TenantAccess");
app.MapLifecycleEndpoints();
app.MapExportEndpoints();
app.MapGet("/review", () => Results.Redirect("/"));

app.Run();

public partial class Program;
