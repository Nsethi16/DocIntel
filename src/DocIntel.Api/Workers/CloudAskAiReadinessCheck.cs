using Azure;
using Azure.Identity;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Jobs;
using DocIntel.Core.Ocr;
using DocIntel.Core.Review;
using DocIntel.Core.Storage;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DocIntel.Api.Workers;

/// <summary>Fails readiness when shared execution storage or a dispatch queue is missing/inaccessible.</summary>
public sealed class CloudAskAiReadinessCheck(IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var clients = services.GetService<CloudAskAiClients>();
        if (clients is null) return HealthCheckResult.Healthy("Single-host execution is configured.");
        try
        {
            await clients.Runs.GetPropertiesAsync(cancellationToken: cancellationToken);
            await clients.Dispatch.GetPropertiesAsync(cancellationToken);
            await clients.Poison.GetPropertiesAsync(cancellationToken);
            var documents = services.GetRequiredService<CloudDocumentClients>();
            await documents.Dispatch.GetPropertiesAsync(cancellationToken);
            await documents.Poison.GetPropertiesAsync(cancellationToken);
            if (services.GetRequiredService<IJobStore>() is not CosmosJobStore jobs)
                return HealthCheckResult.Unhealthy("Shared execution requires shared document metadata.");
            await jobs.CheckReadinessAsync(cancellationToken);
            if (services.GetRequiredService<IOcrUsageStore>() is not CosmosOcrUsageStore usage)
                return HealthCheckResult.Unhealthy("Shared execution requires shared OCR allowance storage.");
            await usage.CheckReadinessAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is RequestFailedException or AuthenticationFailedException or
            HttpRequestException or CosmosException)
        {
            return HealthCheckResult.Unhealthy("Shared execution storage is missing or inaccessible.");
        }
    }
}
