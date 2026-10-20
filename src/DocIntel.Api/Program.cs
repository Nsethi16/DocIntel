using System.Text.Json;
using System.Text.Json.Serialization;
using DocIntel.Api.Endpoints;
using DocIntel.Api.Identity;
using DocIntel.Api.Workers;
using DocIntel.Api.Channels;
using DocIntel.Core;
using DocIntel.Core.Ocr;
using DocIntel.Core.Review;
using DocIntel.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.AddDocIntelIdentity();
builder.AddDocIntelTeams();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var harnessFilePath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "agent-harness-settings.json");
builder.Services.AddDocIntelCore(configuration: builder.Configuration, harnessFilePath: harnessFilePath);
builder.Services.AddHostedService<DocumentJobWorker>();
builder.Services.AddHostedService<AskAiRunWorker>();
builder.Services.AddHealthChecks().AddCheck<CloudAskAiReadinessCheck>("execution-storage", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = exception => exception switch
    {
        BadHttpRequestException request => request.StatusCode,
        OcrBudgetExceededException => StatusCodes.Status429TooManyRequests,
        ReviewConflictException => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError
    }
});
app.Use(async (context, next) =>
{
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path == "/ask-run.html")
    {
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; " +
            "base-uri 'self'; object-src 'none'";
        context.Response.Headers["X-Frame-Options"] = "DENY";
    }
    if (context.Request.Path == "/review" || context.Request.Path == "/review.html")
    {
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self' https://cdnjs.cloudflare.com; style-src 'self'; " +
            "img-src 'self' data: blob:; connect-src 'self'; worker-src 'self' blob: https://cdnjs.cloudflare.com; " +
            "frame-ancestors 'none'; base-uri 'self'; object-src 'none'";
        context.Response.Headers["X-Frame-Options"] = "DENY";
    }
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/auth") ||
        context.Request.Path.StartsWithSegments("/signin-oidc"))
    {
        context.Response.Headers.CacheControl = "no-store";
    }
    await next();
});
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
    }
});

app.MapGet("/admin", () => Results.Redirect("/admin.html"));
app.MapDefaultEndpoints();
app.MapDocIntelIdentity();
app.MapDocIntelTeams();
app.MapHarnessEndpoints();
app.MapOcrUsageEndpoints();
app.MapReviewEndpoints();
app.MapExportEndpoints();
app.MapGet("/review", (HttpRequest request) => Results.Redirect(
    "/review.html?tenant=" + Uri.EscapeDataString(request.Query["tenant"].ToString()) +
    "&job=" + Uri.EscapeDataString(request.Query["job"].ToString())));

app.Run();

public partial class Program;
