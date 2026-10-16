using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// In non-Development (azd publish / deploy), provision an Azure Container App Environment.
// In Development, run zero-Docker locally with the Aspire Dashboard.
if (!builder.ExecutionContext.IsRunMode)
{
    builder.AddAzureContainerAppEnvironment("cae");
}

var api = builder.AddProject("docintel-api", @"..\DocIntel.Api\DocIntel.Api.csproj")
    .WithExternalHttpEndpoints()
    .WithEnvironment("AzureAI__FoundryInferenceEndpoint", builder.Configuration["AzureAI:FoundryInferenceEndpoint"] ?? string.Empty)
    .WithEnvironment("AzureAI__AzureOpenAIEndpoint", builder.Configuration["AzureAI:AzureOpenAIEndpoint"] ?? string.Empty)
    .WithEnvironment("AzureAI__TranslatorEndpoint", builder.Configuration["AzureAI:TranslatorEndpoint"] ?? string.Empty)
    .WithEnvironment("AzureAI__LocalOpenAIEndpoint", builder.Configuration["AzureAI:LocalOpenAIEndpoint"] ?? string.Empty);

builder.Build().Run();
