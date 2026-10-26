using System.Text.Json;
using DocIntel.Api.Workers;
using DocIntel.Core;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class ProviderStartupCheckTests
{
    [Fact]
    public async Task Legacy_Ask_Ai_Remains_Unhealthy_Without_Explicit_Repair_Opt_In()
    {
        // Arrange
        var tenant = LegacySample();
        var store = Store(tenant);

        // Act
        var result = await VerifyAsync(store, Catalog(), repair: false);

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        store.Verify(s => s.UpsertTenantAsync(It.IsAny<TenantHarness>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("deepseek-r1", tenant.DefaultModelId);
    }

    [Fact]
    public async Task Explicit_Repair_Upgrades_Only_The_Original_Sample_Default_Through_The_Versioned_Store()
    {
        // Arrange
        var tenant = LegacySample();
        var original = JsonSerializer.Serialize(tenant);
        var store = Store(tenant);
        TenantHarness? proposed = null;
        var saved = Copy(tenant);
        saved.DefaultModelId = "gpt-5-mini";
        saved.HarnessVersion = 2;
        store.Setup(s => s.UpsertTenantAsync(It.IsAny<TenantHarness>(), It.IsAny<CancellationToken>()))
            .Callback<TenantHarness, CancellationToken>((candidate, _) => proposed = candidate)
            .ReturnsAsync(saved);

        // Act
        var result = await VerifyAsync(store, Catalog(), repair: true);

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.NotNull(proposed);
        Assert.Equal(1, proposed.HarnessVersion);
        Assert.Equal("gpt-5-mini", proposed.DefaultModelId);
        proposed.DefaultModelId = "deepseek-r1";
        Assert.Equal(original, JsonSerializer.Serialize(proposed));
        Assert.Equal(original, JsonSerializer.Serialize(tenant));
        store.Verify(s => s.UpsertTenantAsync(It.IsAny<TenantHarness>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("tenant")]
    [InlineData("name")]
    [InlineData("inactive")]
    [InlineData("default")]
    [InlineData("decision")]
    [InlineData("allowed")]
    [InlineData("feature-disabled")]
    [InlineData("feature-override")]
    [InlineData("feature-instructions")]
    [InlineData("personality")]
    public async Task Edited_Or_Custom_Profiles_Are_Never_Repaired(string customization)
    {
        // Arrange
        var tenant = LegacySample();
        switch (customization)
        {
            case "version": tenant.HarnessVersion = 2; break;
            case "tenant": tenant.TenantId = "custom-tenant"; break;
            case "name": tenant.DisplayName = "Custom profile"; break;
            case "inactive": tenant.IsActive = false; break;
            case "default": tenant.DefaultModelId = "gpt-5-mini"; break;
            case "decision": tenant.DecisionModelId = "gpt-5-mini"; break;
            case "allowed": tenant.AllowedModels.Add("custom-model"); break;
            case "feature-disabled": tenant.Capabilities[FeatureHarness.DocumentInsightKey].Enabled = false; break;
            case "feature-override": tenant.Capabilities[FeatureHarness.AskAiKey].ModelIdOverride = "gpt-5-mini"; break;
            case "feature-instructions": tenant.Capabilities[FeatureHarness.AskAiKey].CustomSystemPrompt = "Custom"; break;
            case "personality": tenant.Agent.Instructions = "Custom guidance"; break;
            default: throw new InvalidOperationException("Unknown test customization.");
        }
        var original = JsonSerializer.Serialize(tenant);
        var store = Store(tenant);

        // Act
        await VerifyAsync(store, Catalog(), repair: true);

        // Assert
        Assert.Equal(original, JsonSerializer.Serialize(tenant));
        store.Verify(s => s.UpsertTenantAsync(It.IsAny<TenantHarness>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_Unconfigured_Replacement_Is_Rejected_Before_Any_Profile_Write()
    {
        // Arrange
        var tenant = LegacySample();
        var store = Store(tenant);
        var models = new ModelCatalog(descriptors: []);

        // Act
        var result = await VerifyAsync(store, models, repair: true);

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("deepseek-r1", tenant.DefaultModelId);
        store.Verify(s => s.UpsertTenantAsync(It.IsAny<TenantHarness>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("gpt-5-mini", HealthStatus.Healthy)]
    [InlineData("deepseek-r1", HealthStatus.Unhealthy)]
    public async Task Concurrent_Profile_Edits_Are_Reloaded_And_Never_Overwritten(
        string concurrentModel, HealthStatus expectedStatus)
    {
        // Arrange
        var tenant = LegacySample();
        var store = Store(tenant);
        var concurrent = Copy(tenant);
        concurrent.HarnessVersion = 2;
        concurrent.DefaultModelId = concurrentModel;
        concurrent.Agent.Instructions = "Concurrent operator choice";
        store.Setup(s => s.UpsertTenantAsync(It.IsAny<TenantHarness>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HarnessConflictException());
        store.Setup(s => s.GetTenantAsync(tenant.TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(concurrent);
        var original = JsonSerializer.Serialize(concurrent);

        // Act
        var result = await VerifyAsync(store, Catalog(), repair: true);

        // Assert
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(original, JsonSerializer.Serialize(concurrent));
        store.Verify(s => s.UpsertTenantAsync(It.IsAny<TenantHarness>(), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.GetTenantAsync(tenant.TenantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_Concurrent_Deletion_Does_Not_Create_A_Replacement_Profile()
    {
        // Arrange
        var tenant = LegacySample();
        var store = Store(tenant);
        store.Setup(s => s.UpsertTenantAsync(It.IsAny<TenantHarness>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HarnessConflictException());
        store.Setup(s => s.GetTenantAsync(tenant.TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantHarness?)null);

        // Act
        var result = await VerifyAsync(store, Catalog(), repair: true);

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        store.Verify(s => s.UpsertTenantAsync(It.IsAny<TenantHarness>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_Previously_Repaired_Profile_Is_Healthy_Without_Another_Write()
    {
        // Arrange
        var tenant = LegacySample();
        tenant.DefaultModelId = "gpt-5-mini";
        tenant.HarnessVersion = 2;
        var store = Store(tenant);

        // Act
        var result = await VerifyAsync(store, Catalog(), repair: true);

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
        store.Verify(s => s.UpsertTenantAsync(It.IsAny<TenantHarness>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static TenantHarness LegacySample()
    {
        var tenant = FileHarnessStore.CreateDefaultTenants().Single(t => t.TenantId == "fabrikam-procurement");
        tenant.DefaultModelId = "deepseek-r1";
        return tenant;
    }

    private static TenantHarness Copy(TenantHarness tenant) =>
        JsonSerializer.Deserialize<TenantHarness>(JsonSerializer.Serialize(tenant))
        ?? throw new InvalidDataException("Test profile could not be copied.");

    private static Mock<IHarnessStore> Store(TenantHarness tenant)
    {
        var store = new Mock<IHarnessStore>();
        IReadOnlyList<TenantHarness> tenants = [tenant];
        store.Setup(s => s.ListTenantsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(tenants);
        return store;
    }

    private static ModelCatalog Catalog() => new(ModelCatalog.CreateDefaultDescriptors(
        foundryInferenceEndpoint: "https://foundry.example/models", azureOpenAiEndpoint: "https://openai.example/"));

    private static async Task<HealthCheckResult> VerifyAsync(
        Mock<IHarnessStore> store, IModelCatalog models, bool repair)
    {
        using var check = new ProviderStartupCheck(store.Object, models,
            new AzureIntegrationOptions
            {
                ContentUnderstandingEndpoint = "https://layout.example/",
                TranslatorEndpoint = "https://translator.example/"
            },
            new StartupReadinessOptions { UpgradeLegacySampleDefaults = repair },
            NullLogger<ProviderStartupCheck>.Instance);
        await check.StartAsync(CancellationToken.None);
        await check.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        return await check.CheckHealthAsync(new HealthCheckContext());
    }
}
