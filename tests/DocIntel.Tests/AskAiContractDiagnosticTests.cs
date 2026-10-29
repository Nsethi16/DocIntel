using System.ClientModel;
using System.ClientModel.Primitives;
using DocIntel.Api.Diagnostics;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class AskAiContractDiagnosticTests
{
    [Fact]
    public async Task Disabled_Live_Probing_Refuses_Before_Reading_Settings_Or_Calling_A_Model()
    {
        // Arrange
        var harness = new Mock<IHarnessStore>(MockBehavior.Strict);
        var models = new Mock<IModelCatalog>(MockBehavior.Strict);
        using var output = new StringWriter();

        // Act
        var code = await AskAiContractDiagnostic.ExecuteAsync(
            "workspace", false, harness.Object, models.Object, output);

        // Assert
        Assert.Equal(2, code);
        Assert.Contains("LiveProbingDisabled", output.ToString());
        harness.VerifyNoOtherCalls();
        models.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Contract_Check_Uses_One_Token_And_Disabled_Tools_Without_Printing_Generated_Content()
    {
        // Arrange
        var fixture = Fixture();
        fixture.Client.Setup(client => client.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> messages, ChatOptions options, CancellationToken _) =>
            {
                Assert.Single(messages);
                Assert.Equal(1, options.MaxOutputTokens);
                if (options.Tools is not null)
                {
                    Assert.False(options.AllowMultipleToolCalls);
                    Assert.Equal(ChatToolMode.None, options.ToolMode);
                    Assert.Equal(4, options.Tools.Count);
                }
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "PRIVATE_GENERATED_TEXT")));
            });
        using var output = new StringWriter();

        // Act
        var code = await AskAiContractDiagnostic.ExecuteAsync(
            "workspace", true, fixture.Harness.Object, fixture.Models.Object, output);

        // Assert
        Assert.Equal(0, code);
        Assert.Contains("\"status\":\"Accepted\"", output.ToString());
        Assert.DoesNotContain("PRIVATE", output.ToString());
        fixture.Client.Verify(client => client.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    [Fact]
    public async Task Actual_Provider_Rejects_Are_Reported_Only_Through_Redacted_Diagnostics()
    {
        // Arrange
        var fixture = Fixture();
        var response = new Mock<PipelineResponse>();
        response.SetupGet(value => value.Status).Returns(400);
        response.SetupGet(value => value.Content).Returns(BinaryData.FromString("""
            {"error":{"code":"invalid_function_parameters","param":"tools[3].function.parameters",
            "message":"PRIVATE_DATA: additionalProperties must be false"}}
            """));
        fixture.Client.Setup(client => client.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ClientResultException("PRIVATE_EXCEPTION", response.Object));
        using var output = new StringWriter();

        // Act
        var code = await AskAiContractDiagnostic.ExecuteAsync(
            "workspace", true, fixture.Harness.Object, fixture.Models.Object, output);

        // Assert
        Assert.Equal(1, code);
        Assert.Contains("\"httpStatus\":400", output.ToString());
        Assert.Contains("additionalProperties", output.ToString());
        Assert.Contains("ProviderSchemaRejected", output.ToString());
        Assert.DoesNotContain("PRIVATE", output.ToString());
    }

    private static (Mock<IHarnessStore> Harness, Mock<IModelCatalog> Models, Mock<IChatClient> Client) Fixture()
    {
        var tenant = new TenantHarness { TenantId = "workspace", DisplayName = "Workspace" };
        var harness = new Mock<IHarnessStore>(MockBehavior.Strict);
        harness.Setup(store => store.GetTenantAsync("workspace", It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        var client = new Mock<IChatClient>();
        var models = new Mock<IModelCatalog>(MockBehavior.Strict);
        models.Setup(catalog => catalog.ResolveRequiredChatClient(tenant, It.IsAny<string?>())).Returns(client.Object);
        models.Setup(catalog => catalog.ResolveModelId(tenant, It.IsAny<string?>())).Returns(tenant.DefaultModelId);
        models.Setup(catalog => catalog.ListModels()).Returns(ModelCatalog.CreateDefaultDescriptors());
        return (harness, models, client);
    }
}
