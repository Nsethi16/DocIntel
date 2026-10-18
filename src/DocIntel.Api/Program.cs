using System.Text.Json;
using System.Text.Json.Serialization;
using DocIntel.Api.Endpoints;
using DocIntel.Api.Workers;
using DocIntel.Core;
using DocIntel.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var harnessFilePath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "agent-harness-settings.json");
builder.Services.AddDocIntelCore(configuration: builder.Configuration, harnessFilePath: harnessFilePath);
builder.Services.AddHostedService<DocumentJobWorker>();

var app = builder.Build();

app.UseExceptionHandler();
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
app.MapHarnessEndpoints();

app.Run();

public partial class Program;
