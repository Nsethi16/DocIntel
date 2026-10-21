using System.Text.Json;
using System.Text.Json.Serialization;
using DocIntel.Api.Endpoints;
using DocIntel.Api.Identity;
using DocIntel.Api.Workers;
using DocIntel.Api.Channels;
using DocIntel.Api.Http;
using DocIntel.Core;
using DocIntel.ServiceDefaults;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

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
builder.AddDocIntelTeams();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var harnessFilePath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "agent-harness-settings.json");
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

app.UseDocIntelResponseHeaders();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    SuppressDiagnosticsCallback = _ => true
});
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
app.MapDocIntelTeams();
app.MapHarnessEndpoints();
app.MapOcrUsageEndpoints();
app.MapReviewEndpoints();
app.MapLifecycleEndpoints();
app.MapExportEndpoints();
app.MapGet("/review", (HttpRequest request) => Results.Redirect(
    "/review.html?tenant=" + Uri.EscapeDataString(request.Query["tenant"].ToString()) +
    "&job=" + Uri.EscapeDataString(request.Query["job"].ToString())));

app.Run();

public partial class Program;
