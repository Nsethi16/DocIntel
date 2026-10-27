using DocIntel.Api.Workers;
using DocIntel.Core;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class NativeDecisionStartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_Probes_Native_Decision_Without_Using_Chat_Or_Falling_Back(bool providerFails)
    {
        // Arrange
        var tenant = new TenantHarness
        {
            TenantId = "decision-startup", DisplayName = "Decision Startup", DecisionModelId = "decision-1",
            Capabilities = new Dictionary<string, FeatureHarness>(StringComparer.OrdinalIgnoreCase)
            {
                [FeatureHarness.IdentifyDocumentsKey] = new()
                {
                    Enabled = true,
                    IdentifyDocuments = new DocumentIdentificationConfig
                    {
                        Rules = [new DocumentIdentificationRule
                        {
                            DocumentName = "Form", IdentificationPrompt = "Identify this form."
                        }]
                    }
                }
            }
        };
        var store = new Mock<IHarnessStore>();
        store.Setup(value => value.ListTenantsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TenantHarness>)[tenant]);
        var native = new Mock<IMicrosoftDecisionClient>(MockBehavior.Strict);
        var request = native.Setup(value => value.ChooseAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.Is<IReadOnlyDictionary<string, string>>(choices => choices.ContainsKey("ready")),
            It.IsAny<CancellationToken>()));
        if (providerFails) request.ThrowsAsync(new HttpRequestException("Native service unavailable."));
        else request.ReturnsAsync(new MicrosoftDecisionResult("ready", 0.99, "Decision-1"));
        native.Setup(value => value.Dispose());
        var models = new Mock<IModelCatalog>(MockBehavior.Strict);
        models.Setup(value => value.ResolveModelId(tenant, "decision-1")).Returns("decision-1");
        models.Setup(value => value.ResolveDecisionClient(tenant, "decision-1")).Returns(native.Object);
        using var check = new ProviderStartupCheck(store.Object, models.Object,
            new AzureIntegrationOptions { ContentUnderstandingEndpoint = "https://layout.example/" },
            new StartupReadinessOptions { ProbeModelsOnStartup = true, TimeoutSeconds = 10 },
            NullLogger<ProviderStartupCheck>.Instance);

        // Act
        await check.StartAsync(CancellationToken.None);
        await check.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        var health = await check.CheckHealthAsync(new HealthCheckContext());

        // Assert
        Assert.Equal(providerFails ? HealthStatus.Unhealthy : HealthStatus.Healthy, health.Status);
        native.Verify(value => value.ChooseAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Once);
        native.Verify(value => value.Dispose(), Times.Once);
        models.Verify(value => value.ResolveModelId(tenant, "decision-1"), Times.Once);
        models.Verify(value => value.ResolveDecisionClient(tenant, "decision-1"), Times.Once);
        models.VerifyNoOtherCalls();
    }
}
