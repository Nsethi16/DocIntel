using System.Text.Json;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.EntityExtraction;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class EntityExtractionContractTests
{
    [Fact]
    public async Task The_Requested_Schema_Matches_The_Nonempty_Value_And_Confidence_Validation_Contract()
    {
        // Arrange
        ChatOptions? settings = null;
        var client = Client("""{"entities":[]}""", options => settings = options);
        var state = State();
        state.Tenant.Capabilities[FeatureHarness.EntityExtractionKey].CustomSystemPrompt =
            "Extract Customer Name, Contract Number and Total.";

        // Act
        await Executor(client).HandleAsync(state, new Mock<IWorkflowContext>().Object);

        // Assert
        var format = Assert.IsType<ChatResponseFormatJson>(settings!.ResponseFormat);
        var schema = format.Schema!.Value;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("entities", schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        var entities = schema.GetProperty("properties").GetProperty("entities");
        Assert.Equal("array", entities.GetProperty("type").GetString());
        var item = entities.GetProperty("items");
        Assert.False(item.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["name", "value", "category", "confidence"],
            item.GetProperty("required").EnumerateArray().Select(value => Assert.IsType<string>(value.GetString())).ToArray());
        var properties = item.GetProperty("properties");
        foreach (var field in new[] { "name", "value", "category" })
        {
            Assert.Equal("string", properties.GetProperty(field).GetProperty("type").GetString());
            Assert.Equal(1, properties.GetProperty(field).GetProperty("minLength").GetInt32());
        }
        var confidence = properties.GetProperty("confidence");
        Assert.Equal(0, confidence.GetProperty("minimum").GetInt32());
        Assert.Equal(1, confidence.GetProperty("maximum").GetInt32());
        Assert.Contains("Omit requested entities that are absent", settings.Instructions);
        Assert.Contains("""{"entities":[]}""", settings.Instructions);
        Assert.Contains("never a percentage such as 95", settings.Instructions);
        Assert.Contains(DocumentPromptBoundary.Policy, settings.Instructions);
        Assert.Contains("Extract Customer Name, Contract Number and Total.", settings.Instructions);
    }

    [Fact]
    public async Task An_Invoice_With_No_Requested_Contract_Fields_Can_Return_An_Empty_Entity_List()
    {
        // Arrange
        var state = State();
        state.Slices[0].BodyText = "Invoice\nLine item: stationery\nAmount: USD 42";
        var client = Client("""{"entities":[]}""");

        // Act
        await Executor(client).HandleAsync(state, new Mock<IWorkflowContext>().Object);

        // Assert
        Assert.Empty(state.ExtractedEntities);
        Assert.Empty(state.Slices[0].ExtractedEntities);
        Assert.True(Assert.Single(state.ExecutionTrace).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.95)]
    [InlineData(1)]
    public async Task Valid_Source_Values_And_Confidence_Are_Preserved_Without_Invention(double confidence)
    {
        // Arrange
        var state = State();
        var client = Client(JsonSerializer.Serialize(new
        {
            entities = new[] { new { name = "Invoice Total", value = "USD 42", category = "Financial", confidence } }
        }));

        // Act
        await Executor(client).HandleAsync(state, new Mock<IWorkflowContext>().Object);

        // Assert
        var entity = Assert.Single(state.ExtractedEntities);
        Assert.Equal("Invoice Total", entity.Name);
        Assert.Equal("USD 42", entity.Value);
        Assert.Equal(confidence, entity.Confidence);
        Assert.Equal("Financial", entity.Category);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"entities":null}""")]
    [InlineData("""{"entities":[null]}""")]
    [InlineData("""{"entities":[{"name":null,"value":"USD 42","confidence":0.9}]}""")]
    [InlineData("""{"entities":[{"name":"Total","value":null,"confidence":0.9}]}""")]
    [InlineData("""{"entities":[{"name":"Total","value":"  ","confidence":0.9}]}""")]
    [InlineData("""{"entities":[{"name":"Total","value":"USD 42","confidence":95}]}""")]
    [InlineData("""{"entities":[{"name":"Total","value":"USD 42","confidence":-0.1}]}""")]
    public async Task Invalid_Provider_Output_Is_Not_Dropped_Or_Normalized_Into_Success(string response)
    {
        // Arrange
        var state = State();
        var client = Client(response);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Executor(client).HandleAsync(state, new Mock<IWorkflowContext>().Object));
        Assert.Empty(state.ExtractedEntities);
        Assert.Empty(state.Slices[0].ExtractedEntities);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Later_Slice_Failures_Or_Cancellation_Do_Not_Publish_Partial_Entity_Results(bool cancel)
    {
        // Arrange
        var state = State();
        state.Slices.Add(new DocumentSlice { SegmentIndex = 1, BodyText = "Second invoice" });
        var original = new List<ExtractedEntity> { new("Original", "Preserve", "General", 0) };
        state.Slices[0].ExtractedEntities = original;
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var client = new Mock<IChatClient>();
        client.Setup(value => value.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                calls++;
                if (calls == 2 && cancel)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    calls == 1
                        ? """{"entities":[{"name":"Total","value":"USD 42","confidence":0.9}]}"""
                        : """{"entities":[{"name":"Total","value":null,"confidence":0.9}]}""")));
            });

        // Act
        var failure = await Record.ExceptionAsync(async () =>
            await Executor(client).HandleAsync(state, new Mock<IWorkflowContext>().Object, cancellation.Token));

        // Assert
        Assert.NotNull(failure);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.IsType<InvalidDataException>(failure);
        Assert.Equal(2, calls);
        Assert.Same(original, state.Slices[0].ExtractedEntities);
        Assert.Empty(state.Slices[1].ExtractedEntities);
        Assert.Empty(state.ExtractedEntities);
    }

    private static Mock<IChatClient> Client(string response, Action<ChatOptions>? capture = null)
    {
        var client = new Mock<IChatClient>();
        client.Setup(value => value.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((_, options, _) => capture?.Invoke(options))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        return client;
    }

    private static ExtractEntitiesExecutor Executor(Mock<IChatClient> client) => new(new ModelCatalog(
        customClients: new Dictionary<string, IChatClient> { ["gpt-5-mini"] = client.Object }));

    private static SplittingState State() => new()
    {
        Tenant = new TenantHarness { TenantId = "tenant", DisplayName = "Tenant", DefaultModelId = "gpt-5-mini" },
        FileName = "sample.pdf",
        Slices = [new DocumentSlice { SegmentIndex = 0, BodyText = "Invoice Total: USD 42" }]
    };
}
