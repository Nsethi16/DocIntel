using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using DocIntel.Core.Models;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class MicrosoftDecisionClientTests
{
    private const string SensitiveMarker = "synthetic-personal-data-7b6f";
    private const string ValidResponse = """
        {
          "model": "microsoft-decision-1",
          "answers": {
            "document_type": {
              "type": "choice",
              "choice": "Invoice",
              "probabilities": { "Invoice": 0.81, "Receipt": 0.19 },
              "confidence": 0.99
            }
          },
          "usage": { "input_tokens": 10, "output_tokens": 2 }
        }
        """;

    [Fact]
    public async Task Uses_Native_Route_Exact_Deployment_Entra_Scope_And_Typed_Question()
    {
        // Arrange
        using var fixture = new Fixture();

        // Act
        var result = await fixture.ChooseAsync();

        // Assert
        Assert.Equal(HttpMethod.Post, fixture.Handler.Method);
        Assert.Equal(
            "https://decision.example.services.ai.azure.com/providers/microsoft/v1/systemone",
            fixture.Handler.Uri!.AbsoluteUri);
        Assert.Equal("Bearer", fixture.Handler.AuthorizationScheme);
        Assert.Equal(Fixture.TestToken, fixture.Handler.AuthorizationParameter);
        Assert.Equal("application/json", fixture.Handler.MediaType);
        Assert.Equal("application/json", fixture.Handler.Accept);
        fixture.Credential.Verify(c => c.GetTokenAsync(It.Is<TokenRequestContext>(context =>
            context.Scopes.SequenceEqual(new[] { "https://cognitiveservices.azure.com/.default" })),
            It.Is<CancellationToken>(token => token.CanBeCanceled)), Times.Once);
        using var body = JsonDocument.Parse(fixture.Handler.Body!);
        Assert.Equal(3, body.RootElement.EnumerateObject().Count());
        Assert.Equal("Decision-1", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(SensitiveMarker, body.RootElement.GetProperty("state").GetString());
        var question = body.RootElement.GetProperty("questions").GetProperty("document_type");
        Assert.Equal("choice", question.GetProperty("type").GetString());
        Assert.Equal("Identify the form.", question.GetProperty("instructions").GetString());
        Assert.Equal("An invoice form", question.GetProperty("criteria").GetProperty("Invoice").GetString());
        Assert.Equal("A receipt form", question.GetProperty("criteria").GetProperty("Receipt").GetString());
        Assert.Equal(new MicrosoftDecisionResult("Invoice", 0.81, "Decision-1"), result);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData("https://decision.example.services.ai.azure.com")]
    [InlineData("https://decision.example.services.ai.azure.com/")]
    [InlineData("http://localhost:41327/")]
    [InlineData("http://127.0.0.1:41327/")]
    [InlineData("http://[::1]:41327/")]
    public async Task Accepts_Only_Resource_Roots_And_Explicit_Loopback_Http(string endpoint)
    {
        // Arrange
        using var fixture = new Fixture(new Uri(endpoint));

        // Act
        await fixture.ChooseAsync();

        // Assert
        Assert.Equal("/providers/microsoft/v1/systemone", fixture.Handler.Uri!.AbsolutePath);
        Assert.Equal(new Uri(endpoint).Authority, fixture.Handler.Uri.Authority);
    }

    [Theory]
    [InlineData("http://decision.example.services.ai.azure.com/")]
    [InlineData("ftp://localhost/")]
    [InlineData("https://person:secret@decision.example.services.ai.azure.com/")]
    [InlineData("https://decision.example.services.ai.azure.com/?secret=value")]
    [InlineData("https://decision.example.services.ai.azure.com/#private")]
    [InlineData("https://decision.example.services.ai.azure.com/models")]
    [InlineData("https://decision.example.services.ai.azure.com/openai/")]
    [InlineData("https://decision.example.services.ai.azure.com/providers/microsoft/v1/systemone")]
    [InlineData("http://localhost:41327/models")]
    [InlineData("relative")]
    public void Rejects_Unsafe_Endpoints_Without_Echoing_Their_Contents(string endpoint)
    {
        // Arrange
        using var http = new HttpClient(new Handler());
        var credential = new Mock<TokenCredential>();

        // Act
        var error = Assert.Throws<ArgumentException>(() =>
            new MicrosoftDecisionClient(
                http, credential.Object, new Uri(endpoint, UriKind.RelativeOrAbsolute), "Decision-1"));

        // Assert
        Assert.DoesNotContain(endpoint, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Rejects_Empty_Deployment_Names(string? deployment)
    {
        // Arrange
        using var http = new HttpClient(new Handler());
        var credential = new Mock<TokenCredential>();

        // Act
        var error = Assert.ThrowsAny<ArgumentException>(() => new MicrosoftDecisionClient(
            http, credential.Object, new Uri("https://decision.example/"), deployment!));

        // Assert
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(null, "Instructions")]
    [InlineData("", "Instructions")]
    [InlineData(" \r\n", "Instructions")]
    [InlineData("State", null)]
    [InlineData("State", "")]
    [InlineData("State", "\t")]
    public async Task Rejects_Empty_State_Or_Instructions_Before_Authentication(
        string? state,
        string? instructions)
    {
        // Arrange
        using var fixture = new Fixture();

        // Act
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            fixture.Client.ChooseAsync(state!, instructions!, fixture.Criteria));

        // Assert
        Assert.Equal(0, fixture.Handler.Calls);
        fixture.Credential.Verify(c => c.GetTokenAsync(
            It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("single")]
    [InlineData("empty-label")]
    [InlineData("whitespace-label")]
    [InlineData("null-description")]
    [InlineData("empty-description")]
    [InlineData("whitespace-description")]
    [InlineData("too-many")]
    public async Task Rejects_Invalid_Criteria_Before_Authentication(string issue)
    {
        // Arrange
        using var fixture = new Fixture();
        IReadOnlyDictionary<string, string>? criteria = issue switch
        {
            "null" => null,
            "empty" => new Dictionary<string, string>(),
            "single" => new Dictionary<string, string> { ["Invoice"] = "An invoice" },
            "empty-label" => new Dictionary<string, string> { [""] = "An invoice", ["Receipt"] = "A receipt" },
            "whitespace-label" => new Dictionary<string, string> { [" "] = "Invoice", ["Receipt"] = "Receipt" },
            "null-description" => new Dictionary<string, string> { ["Invoice"] = null!, ["Receipt"] = "Receipt" },
            "empty-description" => new Dictionary<string, string> { ["Invoice"] = "", ["Receipt"] = "Receipt" },
            "whitespace-description" => new Dictionary<string, string> { ["Invoice"] = " ", ["Receipt"] = "Receipt" },
            _ => Enumerable.Range(0, 256).ToDictionary(index => $"label-{index}", _ => "Description")
        };

        // Act
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            fixture.Client.ChooseAsync(SensitiveMarker, "Instructions", criteria!));

        // Assert
        Assert.Equal(0, fixture.Handler.Calls);
        fixture.Credential.Verify(c => c.GetTokenAsync(
            It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Treats_Labels_As_Case_Sensitive_Without_Changing_The_Provider_Choice()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Handler.ResponseBody = """
            {"answers":{"document_type":{"type":"choice","choice":"invoice",
            "probabilities":{"Invoice":0.2,"invoice":0.8}}}}
            """;
        var criteria = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Invoice"] = "Uppercase label",
            ["invoice"] = "Lowercase label"
        };

        // Act
        var result = await fixture.Client.ChooseAsync(SensitiveMarker, "Instructions", criteria);

        // Assert
        Assert.Equal(new MicrosoftDecisionResult("invoice", 0.8, "Decision-1"), result);
        using var body = JsonDocument.Parse(fixture.Handler.Body!);
        Assert.Equal(2, body.RootElement.GetProperty("questions").GetProperty("document_type")
            .GetProperty("criteria").EnumerateObject().Count());
    }

    [Fact]
    public async Task Snapshots_Criteria_Before_Awaiting_Authentication()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Credential.Setup(c => c.GetTokenAsync(
                It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => fixture.Criteria.Clear())
            .ReturnsAsync(new AccessToken(Fixture.TestToken, DateTimeOffset.UtcNow.AddHours(1)));

        // Act
        var result = await fixture.ChooseAsync();

        // Assert
        Assert.Equal("Invoice", result.Choice);
        using var body = JsonDocument.Parse(fixture.Handler.Body!);
        Assert.Equal(2, body.RootElement.GetProperty("questions").GetProperty("document_type")
            .GetProperty("criteria").EnumerateObject().Count());
    }

    [Theory]
    [InlineData("0.81", "0.19", "0.81")]
    [InlineData("0.5", "0.5", "0.5")]
    [InlineData("0.666", "0.333", "0.666")]
    [InlineData("0.6665", "0.333", "0.6665")]
    [InlineData("0.6675", "0.333", "0.6675")]
    [InlineData("1", "0", "1")]
    public async Task Preserves_Selected_Probability_And_Accepts_Bounded_Rounding_Without_Normalizing(
        string selected,
        string other,
        string expected)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Handler.ResponseBody = JsonSerializer.Serialize(new
        {
            answers = new
            {
                document_type = new
                {
                    type = "choice",
                    choice = "Invoice",
                    probabilities = new Dictionary<string, double>
                    {
                        ["Invoice"] = Double.Parse(selected, System.Globalization.CultureInfo.InvariantCulture),
                        ["Receipt"] = Double.Parse(other, System.Globalization.CultureInfo.InvariantCulture)
                    },
                    confidence = 0.01
                }
            }
        });

        // Act
        var result = await fixture.ChooseAsync();

        // Assert
        Assert.Equal(Double.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result.Probability);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"provider text\"")]
    [InlineData("{")]
    [InlineData("""{"synthetic-personal-data-7b6f":""")]
    [InlineData("{}")]
    [InlineData("""{"answers":null}""")]
    [InlineData("""{"answers":[]}""")]
    [InlineData("""{"answers":{"team":{"type":"choice","choice":"Invoice"}}}""")]
    [InlineData("""{"error":{"message":"synthetic-personal-data-7b6f"}}""")]
    [InlineData("""{"choices":[{"message":{"content":"Invoice","refusal":null}}]}""")]
    public async Task Rejects_Invalid_Envelopes_Without_Using_Chat(string response)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Handler.ResponseBody = response;

        // Act
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ChooseAsync());

        // Assert
        Assert.DoesNotContain(SensitiveMarker, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"choice":"Invoice","probabilities":{"Invoice":0.8,"Receipt":0.2}}""")]
    [InlineData("""{"type":"Choice","choice":"Invoice","probabilities":{"Invoice":0.8,"Receipt":0.2}}""")]
    [InlineData("""{"type":"noul","choice":"Invoice","probabilities":{"Invoice":0.8,"Receipt":0.2}}""")]
    [InlineData("""{"type":"choice","choice":null,"probabilities":{"Invoice":0.8,"Receipt":0.2}}""")]
    [InlineData("""{"type":"choice","choice":1,"probabilities":{"Invoice":0.8,"Receipt":0.2}}""")]
    [InlineData("""{"type":"choice","probabilities":{"Invoice":0.8,"Receipt":0.2}}""")]
    [InlineData("""{"type":"choice","choice":"invoice","probabilities":{"Invoice":0.8,"Receipt":0.2}}""")]
    [InlineData("""{"type":"choice","choice":"Unknown","probabilities":{"Invoice":0.8,"Receipt":0.2}}""")]
    [InlineData("""{"type":"choice","choice":"Invoice"}""")]
    [InlineData("""{"type":"choice","choice":"Invoice","probabilities":null}""")]
    [InlineData("""{"type":"choice","choice":"Invoice","probabilities":[]}""")]
    [InlineData("""{"type":"refusal","refusal":"synthetic-personal-data-7b6f"}""")]
    public async Task Rejects_Invalid_Typed_Answers_Without_Default_Choices_Or_Scores(string answer)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Handler.ResponseBody = """{"answers":{"document_type":""" + answer + "}}";

        // Act
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ChooseAsync());

        // Assert
        Assert.DoesNotContain(SensitiveMarker, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData("""{"Invoice":1}""")]
    [InlineData("""{"Invoice":0.8,"Receipt":0.1,"Unknown":0.1}""")]
    [InlineData("""{"Invoice":0.8,"receipt":0.2}""")]
    [InlineData("""{"Invoice":"0.8","Receipt":0.2}""")]
    [InlineData("""{"Invoice":null,"Receipt":1}""")]
    [InlineData("""{"Invoice":true,"Receipt":0}""")]
    [InlineData("""{"Invoice":1e999,"Receipt":0}""")]
    [InlineData("""{"Invoice":"NaN","Receipt":0}""")]
    [InlineData("""{"Invoice":NaN,"Receipt":0}""")]
    [InlineData("""{"Invoice":1.1,"Receipt":-0.1}""")]
    [InlineData("""{"Invoice":0.8,"Receipt":-0.1}""")]
    [InlineData("""{"Invoice":0.4,"Receipt":0.4}""")]
    [InlineData("""{"Invoice":0,"Receipt":0}""")]
    [InlineData("""{"Invoice":0.7,"Receipt":0.31}""")]
    [InlineData("""{"Invoice":0.7,"Receipt":0.29}""")]
    [InlineData("""{"Invoice":0.2,"Receipt":0.8}""")]
    [InlineData("""{"Invoice":0.8,"Receipt":0.2,"Receipt":0.2}""")]
    public async Task Rejects_Incomplete_NonFinite_Unknown_Or_Inconsistent_Probabilities(string probabilities)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Handler.ResponseBody =
            """{"answers":{"document_type":{"type":"choice","choice":"Invoice","probabilities":""" +
            probabilities + "}}}";

        // Act
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ChooseAsync());

        // Assert
        Assert.DoesNotContain(SensitiveMarker, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData("root-refusal")]
    [InlineData("answer-refusal")]
    [InlineData("root-error")]
    [InlineData("answer-error")]
    [InlineData("extra-answer")]
    [InlineData("duplicate-answers")]
    [InlineData("duplicate-question")]
    [InlineData("duplicate-probabilities")]
    [InlineData("duplicate-choice")]
    [InlineData("duplicate-type")]
    public async Task Rejects_Refusals_And_Ambiguous_Envelopes_Even_Alongside_A_Valid_Choice(string issue)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Handler.ResponseBody = issue switch
        {
            "root-refusal" => ValidResponse.Replace(
                "\"model\":", $"\"refusal\":\"{SensitiveMarker}\",\"model\":", StringComparison.Ordinal),
            "answer-refusal" => ValidResponse.Replace(
                "\"type\":", $"\"refusal\":\"{SensitiveMarker}\",\"type\":", StringComparison.Ordinal),
            "root-error" => ValidResponse.Replace(
                "\"model\":", $"\"error_message\":\"{SensitiveMarker}\",\"model\":", StringComparison.Ordinal),
            "answer-error" => ValidResponse.Replace(
                "\"type\":", $"\"error\":\"{SensitiveMarker}\",\"type\":", StringComparison.Ordinal),
            "extra-answer" => ValidResponse.Replace(
                "\"document_type\":", "\"unexpected\":{},\"document_type\":", StringComparison.Ordinal),
            "duplicate-answers" => ValidResponse.Replace(
                "\"answers\":", "\"answers\":{},\"answers\":", StringComparison.Ordinal),
            "duplicate-question" => ValidResponse.Replace(
                "\"document_type\":", "\"document_type\":{},\"document_type\":", StringComparison.Ordinal),
            "duplicate-choice" => ValidResponse.Replace(
                "\"choice\":", "\"choice\":\"Receipt\",\"choice\":", StringComparison.Ordinal),
            "duplicate-type" => ValidResponse.Replace(
                "\"type\":", "\"type\":\"choice\",\"type\":", StringComparison.Ordinal),
            _ => ValidResponse.Replace(
                "\"probabilities\":", "\"probabilities\":{},\"probabilities\":", StringComparison.Ordinal)
        };

        // Act
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ChooseAsync());

        // Assert
        Assert.DoesNotContain(SensitiveMarker, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejects_Oversized_Responses_With_Or_Without_Content_Length(bool unknownLength)
    {
        // Arrange
        using var fixture = new Fixture();
        var bytes = Encoding.UTF8.GetBytes(new string(' ', 256 * 1024) + ValidResponse);
        fixture.Handler.ResponseContent = unknownLength
            ? new StreamContent(new NonSeekableReadStream(bytes))
            : new ByteArrayContent(bytes);

        // Act
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ChooseAsync());

        // Assert
        Assert.Null(error.InnerException);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Accepts_A_Valid_Response_At_The_Exact_Byte_Limit()
    {
        // Arrange
        using var fixture = new Fixture();
        var padding = 256 * 1024 - Encoding.UTF8.GetByteCount(ValidResponse);
        fixture.Handler.ResponseContent = new StreamContent(
            new NonSeekableReadStream(Encoding.UTF8.GetBytes(ValidResponse + new string(' ', padding))));

        // Act
        var result = await fixture.ChooseAsync();

        // Assert
        Assert.Equal(0.81, result.Probability);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Rejects_NonSuccess_Including_Redirects_Without_Leaking_Provider_Messages(int status)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Handler.Status = (HttpStatusCode)status;
        fixture.Handler.ResponseBody = SensitiveMarker;
        fixture.Handler.ReasonPhrase = SensitiveMarker;
        fixture.Handler.Location = new Uri("https://untrusted.example/" + SensitiveMarker);

        // Act
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.ChooseAsync());

        // Assert
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal($"Microsoft Decision returned HTTP {status}.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(SensitiveMarker, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Sanitizes_Transport_Exception_Chains_Without_Keeping_Sensitive_Inner_Exceptions()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Handler.Responder = (_, _) => throw new HttpRequestException(
            SensitiveMarker, new IOException(Fixture.TestToken), HttpStatusCode.BadGateway);

        // Act
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.ChooseAsync());

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(SensitiveMarker, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.TestToken, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sanitizes_Credential_Failures_Without_Attempting_Inference()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Credential.Setup(c => c.GetTokenAsync(
                It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(SensitiveMarker, new IOException(Fixture.TestToken)));

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ChooseAsync());

        // Assert
        Assert.Equal("Microsoft Decision authentication failed.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(SensitiveMarker, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.TestToken, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Sanitizes_Invalid_Provider_Token_Headers_Without_Attempting_Inference()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Credential.Setup(c => c.GetTokenAsync(
                It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken(SensitiveMarker + "\r\n" + Fixture.TestToken, DateTimeOffset.UtcNow));

        // Act
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ChooseAsync());

        // Assert
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(SensitiveMarker, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.TestToken, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Sanitizes_Response_Stream_Failures_And_Disposes_The_Stream()
    {
        // Arrange
        using var fixture = new Fixture();
        var stream = new FailingReadStream();
        fixture.Handler.ResponseContent = new StreamContent(stream);

        // Act
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ChooseAsync());

        // Assert
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(SensitiveMarker, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.TestToken, error.ToString(), StringComparison.Ordinal);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Sanitizes_Provider_Cancellation_Exception_Chains()
    {
        // Arrange
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Handler.Responder = (_, token) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(SensitiveMarker, new IOException(Fixture.TestToken), token);
        };

        // Act
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.ChooseAsync(cancellation.Token));

        // Assert
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(SensitiveMarker, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.TestToken, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Honors_PreCanceled_Requests_Before_Authentication_Or_Http()
    {
        // Arrange
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.ChooseAsync(cancellation.Token));

        // Assert
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, fixture.Handler.Calls);
        fixture.Credential.Verify(c => c.GetTokenAsync(
            It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancels_During_Authentication_Without_Attempting_Http()
    {
        // Arrange
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Credential.Setup(c => c.GetTokenAsync(
                It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .Returns((TokenRequestContext _, CancellationToken token) => new ValueTask<AccessToken>(
                WaitForTokenCancellationAsync(token)));

        // Act
        var pending = fixture.ChooseAsync(cancellation.Token);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        // Assert
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Null(error.InnerException);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Cancels_During_Http_And_Does_Not_Leak_Cancellation_Messages()
    {
        // Arrange
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Handler.Responder = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        // Act
        var pending = fixture.ChooseAsync(cancellation.Token);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        // Assert
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Null(error.InnerException);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Cancels_While_Reading_A_Response_Without_A_Content_Length()
    {
        // Arrange
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var stream = new BlockingReadStream();
        fixture.Handler.ResponseContent = new StreamContent(stream);

        // Act
        var pending = fixture.ChooseAsync(cancellation.Token);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        // Assert
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Null(error.InnerException);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Disposes_The_Owned_Http_Client_And_Rejects_Subsequent_Use()
    {
        // Arrange
        using var fixture = new Fixture();

        // Act
        fixture.Client.Dispose();
        fixture.Client.Dispose();

        // Assert
        Assert.True(fixture.Handler.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.ChooseAsync());
    }

    private static async Task<AccessToken> WaitForTokenCancellationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return default;
    }

    private sealed class Fixture : IDisposable
    {
        public const string TestToken = "isolated-decision-fixture-token";
        public Handler Handler { get; } = new();
        public Mock<TokenCredential> Credential { get; } = new();
        public Dictionary<string, string> Criteria { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Invoice"] = "An invoice form",
            ["Receipt"] = "A receipt form"
        };
        public MicrosoftDecisionClient Client { get; }

        public Fixture(Uri? endpoint = null)
        {
            Credential.Setup(c => c.GetTokenAsync(
                    It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AccessToken(TestToken, DateTimeOffset.UtcNow.AddHours(1)));
            Client = new MicrosoftDecisionClient(
                new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan },
                Credential.Object,
                endpoint ?? new Uri("https://decision.example.services.ai.azure.com/"),
                "Decision-1");
        }

        public Task<MicrosoftDecisionResult> ChooseAsync(CancellationToken cancellationToken = default) =>
            Client.ChooseAsync(SensitiveMarker, "Identify the form.", Criteria, cancellationToken);

        public void Dispose() => Client.Dispose();
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Responder { get; set; }
        public string ResponseBody { get; set; } = ValidResponse;
        public HttpContent? ResponseContent { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? ReasonPhrase { get; set; }
        public Uri? Location { get; set; }
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? MediaType { get; private set; }
        public string? Accept { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Method = request.Method;
            Uri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Accept = request.Headers.Accept.Single().MediaType;
            MediaType = request.Content?.Headers.ContentType?.MediaType;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (Responder is not null)
            {
                return await Responder(request, cancellationToken);
            }

            var response = new HttpResponseMessage(Status)
            {
                Content = ResponseContent ?? new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            };
            if (ReasonPhrase is not null) response.ReasonPhrase = ReasonPhrase;
            response.Headers.Location = Location;
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private class NonSeekableReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }

    private sealed class BlockingReadStream() : NonSeekableReadStream([])
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FailingReadStream() : NonSeekableReadStream([])
    {
        public bool Disposed { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException(SensitiveMarker, new IOException(Fixture.TestToken)));

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
