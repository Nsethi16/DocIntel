using System.Net;
using System.Text.Json;
using Azure.Core;
using DocIntel.Api.Channels;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class GraphMailboxTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("missing", false)]
    [InlineData("wrong", false)]
    [InlineData("duplicate", false)]
    [InlineData("different-sender", false)]
    public async Task Automatic_Sender_Trust_Requires_One_Exact_Transport_Attestation_And_Matching_Sender(string mode, bool expected)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Options.AuthorizationMode = EmailAuthorizationMode.TrustedInternal;
        fixture.Options.CorporateSenderProof = new String('c', 43);
        var headers = mode == "missing" ? Array.Empty<object>() : Enumerable.Range(0, mode == "duplicate" ? 2 : 1)
            .Select(_ => (object)new
            {
                name = EmailChannelOptions.CorporateSenderProofHeader,
                value = mode == "wrong" ? new String('d', 43) : fixture.Options.CorporateSenderProof
            }).ToArray();
        fixture.Handler.ResponseFactory = request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/messages/one", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new
                {
                    id = "one",
                    subject = "Question",
                    receivedDateTime = "2026-10-22T00:00:00Z",
                    from = new { emailAddress = new { address = "reader@example.com" } },
                    sender = new { emailAddress = new { address = mode == "different-sender" ? "other@example.com" : "reader@example.com" } },
                    body = new { contentType = "text", content = "Hello" },
                    internetMessageHeaders = headers
                })
                : JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["value"] = new[] { new { id = "one", receivedDateTime = "2026-10-22T00:00:00Z" } },
                    ["@odata.deltaLink"] = fixture.MailboxRoot + "/mailFolders/inbox/messages/delta?$deltatoken=next"
                }))
        };

        // Act
        var page = await fixture.Client.ReadChangesAsync(null, CancellationToken.None);

        // Assert
        Assert.Equal(expected, Assert.Single(page.Messages).TrustedInternalSender);
    }

    [Fact]
    public async Task Mailbox_Requests_Are_Fixed_To_The_Operator_Binding_And_Only_Use_Graph_Audience_Tokens()
    {
        // Arrange
        var fixture = new Fixture();
        var message = new
        {
            id = "message",
            subject = "Question",
            receivedDateTime = "2026-10-22T00:00:00Z",
            from = new { emailAddress = new { address = "sender@example.com" } },
            body = new { contentType = "text", content = "Question text" },
            internetMessageHeaders = Array.Empty<object>()
        };
        fixture.Handler.ResponseFactory = request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/messages/message", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(message) : JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["value"] = new[] { new { id = "message", receivedDateTime = "2026-10-22T00:00:00Z" } },
                    ["@odata.deltaLink"] = fixture.MailboxRoot + "/mailFolders/inbox/messages/delta?$deltatoken=opaque"
                }))
        };

        // Act
        var changes = await fixture.Client.ReadChangesAsync(null, CancellationToken.None);

        // Assert
        Assert.Equal("sender@example.com", Assert.Single(changes.Messages).Sender);
        Assert.StartsWith(fixture.MailboxRoot + "/mailFolders/inbox/messages/delta?", fixture.Handler.Urls[0]);
        Assert.Contains("outlook.body-content-type=\"text\"", fixture.Handler.Prefer);
        fixture.Credential.Verify(c => c.GetTokenAsync(
            It.Is<TokenRequestContext>(request => request.Scopes.SequenceEqual(new[] { "https://graph.microsoft.com/.default" })),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData("https://attacker.example/v1.0/users/")]
    [InlineData("http://graph.microsoft.com/v1.0/users/")]
    [InlineData("https://graph.microsoft.com/v1.0/users/other/mailFolders/inbox/messages/delta")]
    [InlineData("https://graph.microsoft.com/v1.0/me/messages")]
    public async Task Arbitrary_Continuation_Destinations_Are_Rejected_Before_Acquiring_Credentials(string url)
    {
        // Arrange
        var fixture = new Fixture();

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.ReadChangesAsync(url, CancellationToken.None));
        fixture.Credential.Verify(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task A_Message_Send_Uses_Only_One_Destination_And_Does_Not_Retry_Unknown_Responses()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Handler.Response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("PRIVATE_PROVIDER_DIAGNOSTIC")
        };

        // Act
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Client.SendAsync("verified@example.com", "Answer", "PRIVATE_ANSWER", CancellationToken.None));

        // Assert
        Assert.Equal(1, fixture.Handler.Calls);
        Assert.DoesNotContain("PRIVATE_", failure.Message);
        using var body = JsonDocument.Parse(fixture.Handler.Body!);
        var recipients = body.RootElement.GetProperty("message").GetProperty("toRecipients");
        Assert.Equal(1, recipients.GetArrayLength());
        Assert.Equal("verified@example.com", recipients[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.False(body.RootElement.GetProperty("message").TryGetProperty("replyTo", out _));
    }

    [Fact]
    public async Task Reply_Address_Verification_Requires_The_Exact_Directory_Object_Returned_By_Graph()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = Guid.NewGuid(),
                mail = "verified@example.com",
                userPrincipalName = "verified@example.com"
            }))
        };

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.GetActorAddressesAsync(
            new ActorIdentity(fixture.Options.TenantId, Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task DocIntel_Confirmation_Messages_Are_Automatic_And_Cannot_Start_Another_Challenge_Loop()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Handler.ResponseFactory = request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/messages/generated", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new
                {
                    id = "generated",
                    subject = "Confirm your question",
                    receivedDateTime = "2026-10-22T00:00:00Z",
                    from = new { emailAddress = new { address = "another-agent@example.com" } },
                    body = new { contentType = "text", content = "Sign in and confirm" },
                    internetMessageHeaders = new[] { new { name = "X-DocIntel-Automated-Message", value = "true" } }
                })
                : JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["value"] = new[] { new { id = "generated", receivedDateTime = "2026-10-22T00:00:00Z" } },
                    ["@odata.deltaLink"] = fixture.MailboxRoot + "/mailFolders/inbox/messages/delta?$deltatoken=one"
                }))
        };

        // Act
        var changes = await fixture.Client.ReadChangesAsync(null, CancellationToken.None);

        // Assert
        Assert.True(Assert.Single(changes.Messages).Automatic);
    }

    private sealed class Fixture
    {
        public EmailChannelOptions Options { get; } = new()
        {
            Enabled = true,
            TenantId = Guid.NewGuid(),
            MailboxObjectId = Guid.NewGuid(),
            ApplicationTenantId = "tenant",
            MailboxAddress = "agent@example.com",
            PublicOrigin = "https://docintel.example"
        };
        public Mock<TokenCredential> Credential { get; } = new();
        public Handler Handler { get; } = new();
        public GraphMailboxClient Client { get; }
        public string MailboxRoot => $"https://graph.microsoft.com/v1.0/users/{Options.MailboxObjectId:D}";
        public Fixture()
        {
            Credential.Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AccessToken("isolated-fixture-not-a-real-token", DateTimeOffset.UtcNow.AddHours(1)));
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(GraphMailboxClient.HttpClientName)).Returns(() => new HttpClient(Handler, disposeHandler: false));
            Client = new GraphMailboxClient(factory.Object, Credential.Object, Options);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.Accepted);
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }
        public int Calls { get; private set; }
        public string? Url { get; private set; }
        public List<string> Urls { get; } = [];
        public string? Body { get; private set; }
        public string? Prefer { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Url = request.RequestUri!.AbsoluteUri;
            Urls.Add(Url);
            Prefer = request.Headers.TryGetValues("Prefer", out var values) ? String.Join(",", values) : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return ResponseFactory?.Invoke(request) ?? Response;
        }
    }
}
