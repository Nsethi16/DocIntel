using System.Net;
using System.Text.Json;
using Azure.Core;
using DocIntel.Api.Identity;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class GraphEnterpriseDirectoryTests
{
    [Fact]
    public async Task Search_Uses_The_Fixed_Graph_Host_ReadOnly_Scope_And_Escaped_Bounded_Filters()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Handler.Body = JsonSerializer.Serialize(new { value = new[] { fixture.Profile() } });

        // Act
        var people = await fixture.Directory.SearchAsync("O'Brien", CancellationToken.None);

        // Assert
        Assert.Equal(fixture.Id, Assert.Single(people).ObjectId);
        Assert.Equal("graph.microsoft.com", fixture.Handler.Uri!.Host);
        Assert.Contains("O''Brien", Uri.UnescapeDataString(fixture.Handler.Uri.Query));
        Assert.Contains("$top=20", fixture.Handler.Uri.Query);
        fixture.Credential.Verify(c => c.GetTokenAsync(It.Is<TokenRequestContext>(context =>
            context.Scopes.SequenceEqual(new[] { "https://graph.microsoft.com/.default" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_Directory_Profile_With_A_Different_Object_ID_Is_Rejected_And_Never_Cached()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Handler.Body = JsonSerializer.Serialize(fixture.Profile(Guid.NewGuid()));

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Directory.GetAsync(fixture.Id, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Directory.GetAsync(fixture.Id, CancellationToken.None));
        Assert.Equal(2, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Ambiguous_Directory_Address_Ownership_Cannot_Be_Used_For_Automatic_Email()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Handler.Body = JsonSerializer.Serialize(new
        {
            value = new[]
        { fixture.Profile(), fixture.Profile(Guid.NewGuid()) }
        });

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Directory.FindByEmailAsync("person@example.com", CancellationToken.None));
    }

    [Fact]
    public async Task Photos_Reject_Executable_Formats_And_Missing_Photos_Are_Explicit()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Handler.ContentType = "image/svg+xml";
        fixture.Handler.Body = "<svg onload='unsafe()'/>";

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Directory.GetPhotoAsync(fixture.Id, CancellationToken.None));
        fixture.Handler.Status = HttpStatusCode.NotFound;
        Assert.Null(await fixture.Directory.GetPhotoAsync(fixture.Id, CancellationToken.None));
    }

    private sealed class Fixture
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Handler Handler { get; } = new();
        public Mock<TokenCredential> Credential { get; } = new();
        public GraphEnterpriseDirectory Directory { get; }
        public Fixture()
        {
            Credential.Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AccessToken("isolated-fixture-token", DateTimeOffset.UtcNow.AddHours(1)));
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(GraphEnterpriseDirectory.HttpClientName))
                .Returns(() => new HttpClient(Handler, disposeHandler: false));
            Directory = new(factory.Object, Credential.Object, new() { Enabled = true },
                new() { Mode = IdentityMode.Entra, DirectoryTenantId = Guid.NewGuid() });
        }
        public object Profile(Guid? id = null) => new
        {
            id = id ?? Id,
            displayName = "Directory Person",
            mail = "person@example.com",
            userPrincipalName = "person@example.com",
            jobTitle = "Analyst",
            department = "Operations",
            officeLocation = "Office",
            accountEnabled = true,
            userType = "Member"
        };
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Body { get; set; } = "{}";
        public string ContentType { get; set; } = "application/json";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Uri? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body,
                System.Text.Encoding.UTF8, ContentType)
            });
        }
    }
}
