using System.Text.Json;
using DocIntel.Core;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;
using DocIntel.Core.Ocr;
using DocIntel.Core.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DocIntel.Api.Workers;

/// <summary>Explicitly controls bounded startup model inference, never repeated by health polling.</summary>
public sealed class StartupReadinessOptions
{
    public bool ProbeModelsOnStartup { get; set; }
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Opts into a versioned upgrade of the original, unedited Fabrikam sample model default.</summary>
    public bool UpgradeLegacySampleDefaults { get; set; }
}

/// <summary>Validates active feature/provider routing and optionally proves model access before becoming ready.</summary>
public sealed class ProviderStartupCheck(
    IHarnessStore harnesses, IModelCatalog models, AzureIntegrationOptions azure,
    StartupReadinessOptions options, ILogger<ProviderStartupCheck> logger) : BackgroundService, IHealthCheck
{
    private sealed record Snapshot(HealthCheckResult Result);
    private Snapshot _snapshot = new(HealthCheckResult.Unhealthy("Startup provider verification is in progress."));

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Volatile.Read(ref _snapshot).Result);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var operation = AgentDiagnostics.Begin("startup.providers", logger);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var probed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var tenants = await harnesses.ListTenantsAsync(timeout.Token);
            if (options.UpgradeLegacySampleDefaults)
                tenants = await UpgradeLegacySampleDefaultsAsync(tenants, timeout.Token);
            foreach (var tenant in tenants.Where(tenant => tenant.IsActive))
            {
                timeout.Token.ThrowIfCancellationRequested();
                var processing = tenant.Capabilities.Any(capability => capability.Value.Enabled &&
                    capability.Key != FeatureHarness.AskAiKey && capability.Key != FeatureHarness.DocumentGraphKey);
                if (processing && String.IsNullOrWhiteSpace(azure.ContentUnderstandingEndpoint) &&
                    (!models.AllowsDevelopmentSimulation || tenant.Ocr.Mode != OcrMode.Never))
                    throw new InvalidOperationException("Document ingestion requires configured Content Understanding layout.");
                if (Enabled(tenant, FeatureHarness.DocumentTranslationKey) &&
                    String.IsNullOrWhiteSpace(azure.ResolveEffectiveTranslatorEndpoint()) && !models.AllowsDevelopmentSimulation)
                    throw new InvalidOperationException("Enabled translation requires a configured provider.");
                foreach (var key in new[]
                {
                    FeatureHarness.IdentifyDocumentsKey, FeatureHarness.DocumentInsightKey,
                    FeatureHarness.EntityExtractionKey, FeatureHarness.DataExtractionKey,
                    FeatureHarness.PromptColumnsKey, FeatureHarness.AskAiKey
                })
                {
                    if (!tenant.Capabilities.TryGetValue(key, out var feature) || !NeedsModel(key, feature)) continue;
                    var selected = key == FeatureHarness.IdentifyDocumentsKey
                        ? tenant.DecisionModelId : feature.ModelIdOverride;
                    var modelId = models.ResolveModelId(tenant, selected);
                    using var client = key == FeatureHarness.AskAiKey
                        ? models.ResolveRequiredChatClient(tenant, selected) : models.ResolveChatClient(tenant, selected);
                    if (key == FeatureHarness.PromptColumnsKey &&
                        feature.PromptColumns?.Columns.Any(column => column.IsActive &&
                            column.PromptInstructions.Contains("{{Document.Base64}}", StringComparison.OrdinalIgnoreCase)) == true &&
                        !models.SupportsPdfInputs(tenant, selected))
                        throw new InvalidOperationException("An enabled prompt column requires PDF model input.");
                    if (options.ProbeModelsOnStartup && probed.Add(modelId))
                    {
                        if (models.UsesDevelopmentSimulation(tenant, selected))
                            throw new InvalidOperationException("Live startup probes cannot use simulated responses.");
                        var response = await client.GetResponseAsync(
                            [new ChatMessage(ChatRole.User, "Reply OK.")],
                            new ChatOptions { MaxOutputTokens = 1 }, timeout.Token);
                        if (response.Messages.Count == 0)
                            throw new InvalidDataException("The startup model probe returned no response.");
                    }
                }
            }
            Volatile.Write(ref _snapshot, new Snapshot(HealthCheckResult.Healthy(options.ProbeModelsOnStartup
                ? "Enabled provider configuration and startup model inference were verified."
                : "Enabled provider configuration verified; live model inference probing is disabled.")));
            operation.Complete();
        }
        catch (OperationCanceledException)
        {
            operation.Cancel();
            Volatile.Write(ref _snapshot, new Snapshot(HealthCheckResult.Unhealthy(
                "Startup provider verification was interrupted or exceeded its deadline.")));
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            logger.LogError("Startup provider verification failed with {ErrorType}; no provider details were returned.",
                exception.GetBaseException().GetType().Name);
            Volatile.Write(ref _snapshot, new Snapshot(HealthCheckResult.Unhealthy(
                "An enabled provider is unconfigured or failed startup verification.")));
        }
    }

    private async Task<IReadOnlyList<TenantHarness>> UpgradeLegacySampleDefaultsAsync(
        IReadOnlyList<TenantHarness> tenants, CancellationToken cancellationToken)
    {
        var seed = FileHarnessStore.CreateDefaultTenants().Single(tenant => tenant.TenantId == "fabrikam-procurement");
        var result = tenants.ToList();
        for (var index = 0; index < result.Count; index++)
        {
            var tenant = result[index];
            if (tenant.TenantId != seed.TenantId || tenant.DisplayName != seed.DisplayName ||
                tenant.HarnessVersion != 1 || !tenant.IsActive || tenant.DefaultModelId != "deepseek-r1" ||
                tenant.DecisionModelId != seed.DecisionModelId ||
                !tenant.AllowedModels.SequenceEqual(seed.AllowedModels, StringComparer.Ordinal) ||
                JsonSerializer.Serialize(tenant.Agent) != JsonSerializer.Serialize(seed.Agent) ||
                tenant.Capabilities.Count != seed.Capabilities.Count ||
                tenant.Capabilities.Any(entry => entry.Value is null ||
                    !seed.Capabilities.TryGetValue(entry.Key, out var expected) ||
                    entry.Value.Enabled != expected.Enabled ||
                    entry.Value.ModelIdOverride != expected.ModelIdOverride ||
                    entry.Value.CustomSystemPrompt != expected.CustomSystemPrompt))
                continue;

            var upgraded = JsonSerializer.Deserialize<TenantHarness>(JsonSerializer.Serialize(tenant))
                ?? throw new InvalidDataException("The legacy sample profile could not be copied.");
            upgraded.DefaultModelId = seed.DefaultModelId;
            using var configuredClient = models.ResolveRequiredChatClient(upgraded);
            try
            {
                result[index] = await harnesses.UpsertTenantAsync(upgraded, cancellationToken);
                logger.LogInformation(
                    "Legacy Fabrikam sample model default upgraded from harness version 1 to {HarnessVersion}.",
                    result[index].HarnessVersion);
            }
            catch (HarnessConflictException)
            {
                result[index] = await harnesses.GetTenantAsync(tenant.TenantId, cancellationToken)
                    ?? throw new InvalidOperationException("The sample profile was removed during its upgrade.");
                logger.LogWarning("Concurrent sample profile edit detected; latest settings were not overwritten.");
            }
        }
        return result;
    }

    private static bool Enabled(TenantHarness tenant, string key) =>
        tenant.Capabilities.TryGetValue(key, out var feature) && feature.Enabled;

    private static bool NeedsModel(string key, FeatureHarness feature) => feature.Enabled && (key switch
    {
        FeatureHarness.IdentifyDocumentsKey => feature.IdentifyDocuments?.Rules.Any(rule => rule.IsActive &&
            !String.IsNullOrWhiteSpace(rule.DocumentName) && !String.IsNullOrWhiteSpace(rule.IdentificationPrompt)) == true,
        FeatureHarness.PromptColumnsKey => feature.PromptColumns?.Columns.Any(column => column.IsActive &&
            !String.IsNullOrWhiteSpace(column.ColumnName)) == true,
        FeatureHarness.DataExtractionKey => feature.DataExtraction?.Profiles.Any(profile =>
            !String.IsNullOrWhiteSpace(profile.DocumentName)) == true,
        _ => true
    });
}
