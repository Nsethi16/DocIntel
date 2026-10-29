using System.Text.Json;
using DocIntel.Api.Diagnostics;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class AskAiSyntheticDiagnosticTests
{
    [Fact]
    public async Task Synthetic_Analysis_Refuses_When_Live_Model_Probing_Is_Disabled()
    {
        // Arrange
        var harness = new Mock<IHarnessStore>(MockBehavior.Strict);
        var models = new Mock<IModelCatalog>(MockBehavior.Strict);
        using var output = new StringWriter();

        // Act
        var result = await AskAiSyntheticDiagnostic.ExecuteAsync(
            "workspace", false, harness.Object, models.Object, output);

        // Assert
        Assert.Equal(2, result);
        Assert.Contains("LiveProbingDisabled", output.ToString());
        harness.VerifyNoOtherCalls();
        models.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Synthetic_Analysis_Validates_Exact_Totals_And_Citations_Without_A_Production_Store()
    {
        // Arrange
        var tenant = new TenantHarness { TenantId = "workspace", DisplayName = "Workspace" };
        var harness = new Mock<IHarnessStore>();
        harness.Setup(store => store.GetTenantAsync("workspace", It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<ChatMessage> _, ChatOptions options, CancellationToken token) =>
            {
                async Task<object?> Invoke(string name, AIFunctionArguments arguments)
                {
                    var tool = Assert.IsAssignableFrom<AIFunction>(
                        Assert.Single(options.Tools!, candidate => candidate.Name == name));
                    return await tool.InvokeAsync(arguments, token);
                }
                await Invoke("search_documents", new()
                    { ["query"] = JsonSerializer.SerializeToElement(new DocumentQuery()) });
                var rows = new List<AskAiAnalysisInputRow>();
                var customers = new[]
                {
                    "Northwind Demo", "Northwind Demo", "Fabrikam Demo", "Fabrikam Demo",
                    "Adventure Demo", "Adventure Demo", "Northwind Demo", "Euro Demo", "Unknown Demo"
                };
                string?[] amounts = ["1,000.00", "4,000.00", "2,500.00", "7,500.00",
                    "5,000.00", "5,000.00", "1,000.00", "9,000.00", null];
                for (var index = 1; index <= 9; index++)
                {
                    using var inspected = JsonSerializer.SerializeToDocument(await Invoke("inspect_document_slice",
                        new() { ["jobId"] = $"synthetic-quote-{index}", ["sliceIndex"] = 0, ["textOffset"] = 0 }));
                    if (index == 8) continue;
                    rows.Add(new()
                    {
                        EvidenceId = inspected.RootElement.GetProperty("evidenceId").GetString()!,
                        SourceQuote = inspected.RootElement.GetProperty("sourceText").GetString()!,
                        Unit = "USD",
                        Cells = new()
                        {
                            ["customer"] = customers[index - 1],
                            ["amount"] = amounts[index - 1]
                        }
                    });
                }
                using var analysis = JsonSerializer.SerializeToDocument(await Invoke("analyze_evidence", new()
                {
                    ["request"] = JsonSerializer.SerializeToElement(new AskAiAnalysisRequest
                    {
                        Title = "Synthetic totals", Operation = "sum", Visualization = "bar",
                        GroupBy = "customer", ValueColumn = "amount", Rows = rows,
                        Columns =
                        [
                            new() { Key = "customer", Label = "Customer", Kind = "text" },
                            new() { Key = "amount", Label = "Amount", Kind = "number" }
                        ]
                    })
                }));
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    JsonSerializer.Serialize(new
                    {
                        kind = "DocumentAnswer", answer = "Synthetic totals.",
                        evidenceIds = analysis.RootElement.GetProperty("evidenceIds"),
                        analysisIds = new[] { "a1" }
                    })));
            });
        var models = new Mock<IModelCatalog>();
        models.Setup(catalog => catalog.ResolveModelId(tenant, It.IsAny<string?>())).Returns(tenant.DefaultModelId);
        models.Setup(catalog => catalog.ResolveRequiredChatClient(tenant, It.IsAny<string?>())).Returns(client.Object);
        models.Setup(catalog => catalog.ListModels()).Returns(ModelCatalog.CreateDefaultDescriptors());
        using var output = new StringWriter();

        // Act
        var result = await AskAiSyntheticDiagnostic.ExecuteAsync(
            "workspace", true, harness.Object, models.Object, output);

        // Assert
        Assert.Equal(0, result);
        Assert.Contains("\"status\":\"Completed\"", output.ToString());
        Assert.Contains("\"citations\":7", output.ToString());
        Assert.DoesNotContain("Northwind", output.ToString());
        Assert.DoesNotContain("Fabrikam", output.ToString());
    }
}
