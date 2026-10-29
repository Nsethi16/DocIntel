using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using DocIntel.Core.Features.AskAi;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class AskAiRateLimitTests
{
    [Theory]
    [InlineData(1023)]
    [InlineData(65537)]
    public void Invalid_Output_Token_Budgets_Are_Rejected(int limit)
    {
        // Arrange
        var options = new AskAiExecutionOptions { MaxOutputTokens = limit };

        // Act
        var exception = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);

        // Assert
        Assert.Equal(nameof(AskAiExecutionOptions.MaxOutputTokens), exception.ParamName);
    }

    [Fact]
    public void Contract_Probe_Uses_The_Configured_Application_Output_Budget()
    {
        // Arrange
        var options = new AskAiExecutionOptions { MaxOutputTokens = 8192 };

        // Act
        var contract = AskAiOrchestrator.CreateProviderContractOptions(options);

        // Assert
        Assert.Equal(8192, contract.MaxOutputTokens);
    }

    [Fact]
    public async Task Token_Exhaustion_Never_Publishes_A_Partial_Answer()
    {
        // Arrange
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Partial private content"))
                { FinishReason = ChatFinishReason.Length });
        using var resilient = new AskAiRateLimitChatClient(client.Object, NullLogger.Instance);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resilient.GetResponseAsync([new(ChatRole.User, "Question")]));

        // Assert
        Assert.Equal("ResponseTokenLimit", AskAiFailureDiagnostics.GetCode(exception));
        Assert.DoesNotContain("private", exception.Message);
    }

    [Theory]
    [InlineData("retry-after-ms", "2500", 2.5)]
    [InlineData("x-ms-retry-after-ms", "3000", 3)]
    [InlineData("Retry-After", "4", 4)]
    public async Task Rejected_Model_Request_Respects_Provider_Delay_And_Reuses_The_Same_Conversation(
        string header, string value, double seconds)
    {
        // Arrange
        var client = new Mock<IChatClient>();
        var request = new[] { new ChatMessage(ChatRole.User, "Question") };
        var options = new ChatOptions();
        client.SetupSequence(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                options, It.IsAny<CancellationToken>()))
            .ThrowsAsync(Failure(429, "rate_limit_exceeded", header, value))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Answer")));
        var delays = new List<TimeSpan>();
        using var resilient = new AskAiRateLimitChatClient(client.Object, NullLogger.Instance,
            delayAsync: (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        // Act
        var response = await resilient.GetResponseAsync(request, options);

        // Assert
        Assert.Equal("Answer", response.Text);
        Assert.Equal(TimeSpan.FromSeconds(seconds), Assert.Single(delays));
        client.Verify(chat => chat.GetResponseAsync(
            It.Is<IEnumerable<ChatMessage>>(messages => messages.Single() == request[0]),
            options, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task HTTP_Date_Retry_After_Is_Respected_Without_Wall_Clock_Sleeps()
    {
        // Arrange
        var now = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
        var clock = new Mock<TimeProvider>();
        clock.Setup(provider => provider.GetUtcNow()).Returns(now);
        var client = new Mock<IChatClient>();
        client.SetupSequence(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Failure(429, "rate_limit_exceeded", "Retry-After",
                now.AddSeconds(60).ToString("r", System.Globalization.CultureInfo.InvariantCulture)))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Answer")));
        var delays = new List<TimeSpan>();
        using var resilient = new AskAiRateLimitChatClient(client.Object, NullLogger.Instance,
            delayAsync: (delay, _) => { delays.Add(delay); return Task.CompletedTask; }, clock: clock.Object);

        // Act
        await resilient.GetResponseAsync([new(ChatRole.User, "Question")]);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(delays));
    }

    [Fact]
    public async Task Provider_No_Retry_Header_Is_Not_Overridden()
    {
        // Arrange
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Failure(429, "rate_limit_exceeded", "x-should-retry", "false"));
        using var resilient = new AskAiRateLimitChatClient(client.Object, NullLogger.Instance,
            delayAsync: (_, _) => throw new InvalidOperationException("No retry was expected."));

        // Act
        await Assert.ThrowsAsync<ClientResultException>(() =>
            resilient.GetResponseAsync([new(ChatRole.User, "Question")]));

        // Assert
        client.Verify(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(429, "insufficient_quota", "1")]
    [InlineData(400, "invalid_request_error", "1")]
    [InlineData(503, "Unknown", "1")]
    [InlineData(429, "rate_limit_exceeded", "181")]
    public async Task Permanent_Errors_And_Provider_Waits_Outside_The_Budget_Are_Not_Retried(
        int status, string code, string seconds)
    {
        // Arrange
        var failure = Failure(status, code, "Retry-After", seconds);
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        using var resilient = new AskAiRateLimitChatClient(client.Object, NullLogger.Instance,
            delayAsync: (_, _) => throw new InvalidOperationException("No retry was expected."));

        // Act
        var exception = await Assert.ThrowsAsync<ClientResultException>(() =>
            resilient.GetResponseAsync([new(ChatRole.User, "Question")]));

        // Assert
        Assert.Same(failure, exception);
        client.Verify(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Repeated_Throttling_Is_Bounded_And_Cancellation_Stops_The_Wait()
    {
        // Arrange
        var client = new Mock<IChatClient>();
        client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Failure(429, "rate_limit_exceeded", "Retry-After", "60"));
        var delays = new List<TimeSpan>();
        using var resilient = new AskAiRateLimitChatClient(client.Object, NullLogger.Instance,
            delayAsync: (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        // Act
        await Assert.ThrowsAsync<ClientResultException>(() =>
            resilient.GetResponseAsync([new(ChatRole.User, "Question")]));

        // Assert
        Assert.Equal(3, delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(180), TimeSpan.FromTicks(delays.Sum(delay => delay.Ticks)));
        client.Verify(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()), Times.Exactly(4));

        // Arrange
        client.Invocations.Clear();
        using var cancellation = new CancellationTokenSource();
        using var cancellable = new AskAiRateLimitChatClient(client.Object, NullLogger.Instance,
            delayAsync: (_, token) => { cancellation.Cancel(); return Task.Delay(Timeout.Infinite, token); });

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellable.GetResponseAsync(
            [new(ChatRole.User, "Question")], cancellationToken: cancellation.Token));

        // Assert
        client.Verify(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ClientResultException Failure(int status, string code, string header, string value)
    {
        var headers = new Mock<PipelineResponseHeaders>();
        string? text = value;
        headers.Setup(collection => collection.TryGetValue(header, out text)).Returns(true);
        var response = new Mock<PipelineResponse>();
        response.SetupGet(result => result.Status).Returns(status);
        response.SetupGet(result => result.Headers).Returns(headers.Object);
        response.SetupGet(result => result.Content).Returns(BinaryData.FromObjectAsJson(
            new { error = new { code, message = "Private provider content" } }));
        return new ClientResultException("Private provider exception", response.Object);
    }
}
