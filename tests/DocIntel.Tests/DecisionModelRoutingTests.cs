using DocIntel.Core;
using DocIntel.Core.Features.DocumentIdentification;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class DecisionModelRoutingTests
{
    [Fact]
    public void Microsoft_Decision_Is_A_Native_Text_Only_Deployment_Not_A_Gpt_Alias()
    {
        // Arrange
        var catalog = new ModelCatalog(ModelCatalog.CreateDefaultDescriptors(
            "https://primary.services.ai.azure.com/models", "https://primary.openai.azure.com/"));
        var tenant = Tenant();

        // Act
        var decision = Assert.Single(catalog.ListModels(), model => model.ModelId == "decision-1");

        // Assert
        Assert.Equal(ModelProvider.MicrosoftDecision, decision.Provider);
        Assert.Equal("Decision-1", decision.DeploymentName);
        Assert.Equal("https://primary.services.ai.azure.com/", decision.Endpoint);
        Assert.False(decision.SupportsPdfInputs);
        Assert.Throws<NotSupportedException>(() => catalog.ResolveChatClient(tenant, "decision-1"));
        Assert.Throws<NotSupportedException>(() => catalog.ResolveRequiredChatClient(tenant, "decision-1"));
        Assert.Equal("gpt-5-mini", Assert.Single(catalog.ListModels(),
            model => model.ModelId == "gpt-5-mini").DeploymentName);
    }

    [Fact]
    public void A_Separate_Configured_Decision_Resource_Does_Not_Repoint_Language_Models()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAI:FoundryInferenceEndpoint"] = "https://primary.services.ai.azure.com/models",
            ["AzureAI:AzureOpenAIEndpoint"] = "https://primary.openai.azure.com/",
            ["AzureAI:DecisionEndpoint"] = "https://decisions.services.ai.azure.com/",
            ["AzureAI:DecisionDeploymentName"] = "Customer-Decision-1"
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddDocIntelCore(configuration);
        using var provider = services.BuildServiceProvider();

        // Act
        var models = provider.GetRequiredService<IModelCatalog>().ListModels();

        // Assert
        var decision = Assert.Single(models, model => model.ModelId == "decision-1");
        Assert.Equal("https://decisions.services.ai.azure.com/", decision.Endpoint);
        Assert.Equal("Customer-Decision-1", decision.DeploymentName);
        Assert.Equal("https://primary.openai.azure.com/",
            Assert.Single(models, model => model.ModelId == "gpt-5-mini").Endpoint);
    }

    [Fact]
    public void Missing_Decision_Configuration_Never_Uses_The_Azure_OpenAi_Language_Endpoint()
    {
        // Arrange
        var catalog = new ModelCatalog(ModelCatalog.CreateDefaultDescriptors(
            azureOpenAiEndpoint: "https://language.openai.azure.com/"));

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => catalog.ResolveDecisionClient(Tenant(), "decision-1"));
        Assert.False(catalog.UsesDevelopmentSimulation(Tenant(), "decision-1"));
    }

    [Fact]
    public async Task Identification_Uses_The_Tenant_Decision_Model_And_Native_Choices_Not_Chat()
    {
        // Arrange
        var state = State();
        state.Tenant.Capabilities[FeatureHarness.IdentifyDocumentsKey].ModelIdOverride = "gpt-5-mini";
        state.Tenant.Capabilities[FeatureHarness.IdentifyDocumentsKey].IdentifyDocuments!.DecisionModelId = "gpt-5-mini";
        var native = new Mock<IMicrosoftDecisionClient>(MockBehavior.Strict);
        native.Setup(client => client.ChooseAsync(
                It.Is<string>(text => text.Contains("Source Form", StringComparison.Ordinal)),
                It.IsAny<string>(), It.Is<IReadOnlyDictionary<string, string>>(choices =>
                    choices.ContainsKey("form_0") && choices.ContainsKey("undefined")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MicrosoftDecisionResult("form_0", 0.94, "Customer-Decision-1"));
        native.Setup(client => client.Dispose());
        var catalog = Catalog(native.Object);

        // Act
        await new IdentifyDocumentsExecutor(catalog.Object).HandleAsync(
            state, Mock.Of<IWorkflowContext>(), CancellationToken.None);

        // Assert
        Assert.Equal("Signature Form", state.Slices[0].IdentifiedDocumentName);
        Assert.Equal(0.94, state.Slices[0].IdentificationConfidence);
        Assert.Equal("decision-1", state.ModelIdUsed);
        Assert.Contains("Customer-Decision-1", Assert.Single(state.ExecutionTrace).Notes);
        catalog.Verify(model => model.ResolveChatClient(It.IsAny<TenantHarness>(), It.IsAny<string?>()), Times.Never);
        native.Verify(client => client.ChooseAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_Later_Native_Failure_Does_Not_Publish_Partial_Identification()
    {
        // Arrange
        var state = State();
        state.Slices.Add(new DocumentSlice { BodyText = "Second Form", StartPage = 2, EndPage = 2 });
        var native = new Mock<IMicrosoftDecisionClient>();
        native.SetupSequence(client => client.ChooseAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MicrosoftDecisionResult("form_0", 0.9, "Decision-1"))
            .ThrowsAsync(new HttpRequestException("Native provider unavailable."));

        // Act / Assert
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new IdentifyDocumentsExecutor(Catalog(native.Object).Object)
                .HandleAsync(state, Mock.Of<IWorkflowContext>()).AsTask());
        Assert.All(state.Slices, slice => Assert.Null(slice.IdentifiedDocumentName));
        Assert.Empty(state.ExecutionTrace);
    }

    private static Mock<IModelCatalog> Catalog(IMicrosoftDecisionClient client)
    {
        var catalog = new Mock<IModelCatalog>(MockBehavior.Strict);
        catalog.Setup(model => model.ResolveModelId(It.IsAny<TenantHarness>(), "decision-1")).Returns("decision-1");
        catalog.Setup(model => model.ResolveDecisionClient(It.IsAny<TenantHarness>(), "decision-1")).Returns(client);
        catalog.Setup(model => model.UsesDevelopmentSimulation(It.IsAny<TenantHarness>(), "decision-1")).Returns(false);
        return catalog;
    }

    private static TenantHarness Tenant() => new()
    {
        TenantId = "decision-test", DisplayName = "Decision Test", DecisionModelId = "decision-1",
        DefaultModelId = "gpt-5-mini", AllowedModels = ["decision-1", "gpt-5-mini"]
    };

    private static SplittingState State()
    {
        var tenant = Tenant();
        tenant.Capabilities[FeatureHarness.IdentifyDocumentsKey] = new FeatureHarness
        {
            Enabled = true,
            IdentifyDocuments = new DocumentIdentificationConfig
            {
                Rules = [new DocumentIdentificationRule
                {
                    DocumentName = "Signature Form", IdentificationPrompt = "The title identifies Source Form."
                }]
            }
        };
        return new SplittingState
        {
            Tenant = tenant,
            FileName = "forms.pdf",
            Slices = [new DocumentSlice { BodyText = "Source Form", StartPage = 1, EndPage = 1 }]
        };
    }
}
