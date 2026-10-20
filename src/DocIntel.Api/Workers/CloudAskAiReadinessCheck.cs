using Azure;
using Azure.Identity;
using DocIntel.Core.Features.AskAi;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DocIntel.Api.Workers;

/// <summary>Fails readiness when shared run storage or either dispatch queue is missing/inaccessible.</summary>
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
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is RequestFailedException or AuthenticationFailedException or HttpRequestException)
        {
            return HealthCheckResult.Unhealthy("Shared Ask AI storage is missing or inaccessible.");
        }
    }
}
