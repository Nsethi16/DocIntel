using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Azure.Core;
using Azure.Identity;
using DocIntel.Api.Diagnostics;
using DocIntel.Api.Identity;
using DocIntel.Core.Authorization;
using DocIntel.Core.Harness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class DirectoryConnectionDiagnosticTests
{
    [Fact]
    public void The_Probe_Capability_Marker_Is_Published_At_The_Application_Root()
    {
        // Arrange
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null &&
            !File.Exists(Path.Combine(root.FullName, "src", "DocIntel.Api", "DocIntel.Api.csproj")))
            root = root.Parent;
        Assert.NotNull(root);
        var projectDirectory = Path.Combine(root.FullName, "src", "DocIntel.Api");
        var project = XDocument.Load(Path.Combine(projectDirectory, "DocIntel.Api.csproj"));

        // Act
        var marker = Assert.Single(project.Descendants("None"), item =>
            item.Attribute("Update")?.Value == @"Diagnostics\docintel-directory-probe.v1");

        // Assert
        Assert.Equal("docintel-directory-probe.v1", marker.Attribute("TargetPath")?.Value);
        Assert.Equal("PreserveNewest", marker.Attribute("CopyToOutputDirectory")?.Value);
        Assert.Equal("PreserveNewest", marker.Attribute("CopyToPublishDirectory")?.Value);
        Assert.Equal("DOCINTEL_DIRECTORY_PROBE 1",
            File.ReadAllText(Path.Combine(projectDirectory, "Diagnostics", "docintel-directory-probe.v1")).Trim());
    }

    [Fact]
    public async Task Startup_Failures_Emit_One_Safe_Record_Without_Starting_Services()
    {
        // Arrange
        using var fixture = new Fixture();

        // Act
        var exitCode = await DirectoryConnectionDiagnostic.WriteUnavailableAsync(fixture.Output);

        // Assert
        Assert.NotEqual(0, exitCode);
        using var record = fixture.Record("ActionRequired", "DirectoryUnavailable", false, false, false);
        Assert.Empty(fixture.Handler.Requests);
        fixture.Credential.VerifyNoOtherCalls();
        fixture.HostedService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verifies_The_Runtime_Lookup_And_Picker_Without_Changing_The_Feature(bool enabled)
    {
        // Arrange
        using var fixture = new Fixture(enabled);
        var before = fixture.Configuration.AsEnumerable().ToArray();

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(0, exitCode);
        using var record = fixture.Record("Connected", "None", true, true, true);
        Assert.Equal(enabled, record.RootElement.GetProperty("directoryEnabled").GetBoolean());
        Assert.Equal(before, fixture.Configuration.AsEnumerable().ToArray());
        Assert.Equal(enabled, fixture.OriginalOptions.Enabled);
        Assert.Equal(enabled, fixture.OriginalDirectory.IsConfigured);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.All(fixture.Handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("graph.microsoft.com", request.Uri.Host);
            Assert.DoesNotContain("photo", request.Uri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal($"/v1.0/users/{fixture.OwnerId:D}", fixture.Handler.Requests[0].Uri.AbsolutePath);
        var search = Uri.UnescapeDataString(fixture.Handler.Requests[1].Uri.Query);
        Assert.Contains("$top=20", search);
        Assert.Contains("startswith(", search);
        Assert.Contains("'pr'", search);
        fixture.Credential.Verify(credential => credential.GetTokenAsync(
            It.Is<TokenRequestContext>(context =>
                context.Scopes.SequenceEqual(new[] { "https://graph.microsoft.com/.default" })),
            It.Is<CancellationToken>(token => token.CanBeCanceled)), Times.Exactly(2));
        fixture.Credential.VerifyNoOtherCalls();
        fixture.HostedService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("missing", "OwnerNotFound")]
    [InlineData("disabled", "OwnerDisabled")]
    [InlineData("guest", "OwnerNotMember")]
    [InlineData("address", "OwnerNoWorkAddress")]
    public async Task An_Ineligible_Owner_Never_Enables_The_Directory_Or_Searches(string issue, string code)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Handler.OwnerBody = fixture.Profile(
            enabled: issue != "disabled", member: issue != "guest",
            mail: issue == "address" ? "not-an-address" : Fixture.WorkAddress,
            upn: issue == "address" ? String.Empty : Fixture.WorkAddress);
        if (issue == "missing") fixture.Handler.OwnerStatus = HttpStatusCode.NotFound;

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.NotEqual(0, exitCode);
        using var record = fixture.Record("ActionRequired", code, true, false, false);
        Assert.Single(fixture.Handler.Requests);
        Assert.False(fixture.OriginalDirectory.IsConfigured);
    }

    [Fact]
    public async Task A_Valid_UPN_Is_Eligible_When_Mail_Is_Not_A_Work_Address()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Handler.OwnerBody = fixture.Profile(mail: "not-an-address");

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(0, exitCode);
        using var record = fixture.Record("Connected", "None", true, true, true);
    }

    [Fact]
    public async Task A_Different_Returned_Owner_Is_Never_Eligible()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Handler.OwnerBody = fixture.Profile(id: Guid.NewGuid());

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.NotEqual(0, exitCode);
        using var record = fixture.Record("ActionRequired", "DirectoryUnavailable", false, false, false);
        Assert.Single(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("tenant", "TenantMismatch")]
    [InlineData("identity-client", "RuntimeIdentityMismatch")]
    [InlineData("configured-client", "RuntimeIdentityMismatch")]
    [InlineData("process-client", "RuntimeIdentityMismatch")]
    [InlineData("missing-process-client", "RuntimeIdentityMismatch")]
    [InlineData("missing-identity-client", "RuntimeIdentityMismatch")]
    [InlineData("mode", "InvalidInput")]
    [InlineData("invalid-mode", "InvalidInput")]
    [InlineData("invalid-enabled", "InvalidInput")]
    public async Task Mismatched_Runtime_Identity_Or_Directory_Is_Rejected_Before_Network(string issue, string code)
    {
        // Arrange
        using var fixture = new Fixture();
        if (issue == "tenant") fixture.Configuration["Identity:DirectoryTenantId"] = Guid.NewGuid().ToString();
        if (issue == "identity-client")
            fixture.Configuration["Identity:ManagedIdentityClientId"] = Guid.NewGuid().ToString();
        if (issue == "configured-client") fixture.Configuration["AZURE_CLIENT_ID"] = Guid.NewGuid().ToString();
        if (issue == "process-client") fixture.RuntimeClientId = Guid.NewGuid().ToString();
        if (issue == "missing-process-client") fixture.RuntimeClientId = null;
        if (issue == "missing-identity-client") fixture.Configuration["Identity:ManagedIdentityClientId"] = null;
        if (issue == "mode") fixture.Configuration["Identity:Mode"] = "LocalDevelopment";
        if (issue == "invalid-mode") fixture.Configuration["Identity:Mode"] = "private-invalid-mode";
        if (issue == "invalid-enabled") fixture.Configuration["Identity:Directory:Enabled"] = "private-invalid-enabled";

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.NotEqual(0, exitCode);
        using var record = fixture.Record("ActionRequired", code, false, false, false);
        Assert.Empty(fixture.Handler.Requests);
        fixture.Credential.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("missing-value")]
    [InlineData("invalid-owner")]
    [InlineData("empty-owner")]
    [InlineData("invalid-tenant")]
    [InlineData("invalid-client")]
    [InlineData("duplicate")]
    [InlineData("dual-mode")]
    [InlineData("unknown")]
    public async Task Invalid_Or_Ambiguous_Flags_Are_Rejected_Before_Network(string issue)
    {
        // Arrange
        using var fixture = new Fixture();
        var arguments = fixture.Arguments;
        if (issue == "missing-value") arguments = arguments[..^1];
        if (issue == "invalid-owner") arguments[1] = "private-invalid-owner";
        if (issue == "empty-owner") arguments[1] = Guid.Empty.ToString();
        if (issue == "invalid-tenant") arguments[3] = "private-invalid-tenant";
        if (issue == "invalid-client") arguments[5] = "private-invalid-client";
        if (issue == "duplicate") arguments[2] = arguments[0];
        if (issue == "dual-mode") arguments = [.. arguments, "--diagnose-ask-run", "private-run"];
        if (issue == "unknown") arguments[4] = "--private-unknown";

        // Act
        var exitCode = await fixture.ExecuteAsync(arguments);

        // Assert
        Assert.NotEqual(0, exitCode);
        using var record = fixture.Record("ActionRequired", "InvalidInput", false, false, false);
        Assert.Empty(fixture.Handler.Requests);
        fixture.Credential.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("--diagnose-directory")]
    [InlineData("--diagnose-directory-tenant")]
    [InlineData("--diagnose-directory-client")]
    [InlineData("--diagnose-directory=private-invalid-owner")]
    public void Incomplete_Directory_Flags_Are_Recognized_Instead_Of_Starting_The_Host(string argument)
    {
        // Arrange
        string[] arguments = [argument];

        // Act
        var requested = DirectoryConnectionDiagnostic.IsRequested(arguments);

        // Assert
        Assert.True(requested);
        Assert.False(DirectoryConnectionDiagnostic.IsRequested(["--diagnose-ask-run", "private-run"]));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "AwaitingAdminApproval", "DirectoryForbidden")]
    [InlineData(HttpStatusCode.Unauthorized, "ActionRequired", "DirectoryUnauthorized")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ActionRequired", "DirectoryUnavailable")]
    public async Task Permission_And_Provider_Failures_Have_Only_Safe_Allowlisted_Output(
        HttpStatusCode status, string diagnosticStatus, string code)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Handler.OwnerStatus = status;
        fixture.Handler.OwnerBody = "private-provider-body\nprivate.person@example.com private-token";

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.NotEqual(0, exitCode);
        using var record = fixture.Record(diagnosticStatus, code, false, false, false);
        Assert.DoesNotContain("private-", fixture.Output.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "AwaitingAdminApproval", "DirectoryForbidden")]
    [InlineData(HttpStatusCode.Unauthorized, "ActionRequired", "DirectoryUnauthorized")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ActionRequired", "DirectoryUnavailable")]
    public async Task Successful_Owner_Lookup_Alone_Does_Not_Prove_Picker_Readiness(
        HttpStatusCode status, string diagnosticStatus, string code)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Handler.SearchStatus = status;

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.NotEqual(0, exitCode);
        using var record = fixture.Record(diagnosticStatus, code, true, true, false);
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task Credential_Failures_Do_Not_Print_Provider_Messages_Or_Exceptions()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Credential.Setup(credential =>
                credential.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthenticationFailedException("private-provider-details\nprivate-token"));

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.NotEqual(0, exitCode);
        using var record = fixture.Record("ActionRequired", "DirectoryUnavailable", false, false, false);
        Assert.Empty(fixture.Handler.Requests);
        Assert.DoesNotContain("private-", fixture.Output.ToString());
        Assert.DoesNotContain(nameof(AuthenticationFailedException), fixture.Output.ToString());
    }

    [Fact]
    public async Task A_Bounded_Deadline_Cancels_The_Runtime_Request_Without_Printing_Its_Input()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Handler.BlockRequests = true;
        var elapsed = Stopwatch.StartNew();

        // Act
        var exitCode = await fixture.ExecuteAsync(timeout: TimeSpan.FromMilliseconds(250));

        // Assert
        Assert.NotEqual(0, exitCode);
        using var record = fixture.Record("TimedOut", "TimedOut", false, false, false);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Single(fixture.Handler.Tokens);
        Assert.True(fixture.Handler.Tokens[0].IsCancellationRequested);
    }

    [Fact]
    public async Task Caller_Cancellation_Is_Propagated_Without_Starting_A_Request()
    {
        // Arrange
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var exitCode = await fixture.ExecuteAsync(cancellationToken: cancellation.Token);

        // Assert
        Assert.NotEqual(0, exitCode);
        using var record = fixture.Record("TimedOut", "TimedOut", false, false, false);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task A_Deadline_Cannot_Be_Extended_Beyond_Forty_Five_Seconds()
    {
        // Arrange
        using var fixture = new Fixture();

        // Act
        var exitCode = await fixture.ExecuteAsync(timeout: TimeSpan.FromSeconds(46));

        // Assert
        Assert.NotEqual(0, exitCode);
        using var record = fixture.Record("ActionRequired", "InvalidInput", false, false, false);
        Assert.Empty(fixture.Handler.Requests);
        fixture.Credential.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task An_Isolated_Development_Loopback_Adapter_Can_Be_Probed_While_The_Feature_Is_Off()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.UseLoopback();

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(0, exitCode);
        using var record = fixture.Record("Connected", "None", true, true, true);
        Assert.False(record.RootElement.GetProperty("directoryEnabled").GetBoolean());
        Assert.All(fixture.Handler.Requests, request => Assert.True(request.Uri.IsLoopback));
        fixture.Credential.VerifyNoOtherCalls();
        Assert.False(fixture.OriginalOptions.Enabled);
    }

    [Theory]
    [InlineData("production")]
    [InlineData("external-endpoint")]
    [InlineData("external-authority")]
    [InlineData("external-listener")]
    [InlineData("no-authority-opt-in")]
    public async Task Unsafe_Development_Endpoints_Are_Rejected_Before_Network(string issue)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.UseLoopback();
        if (issue == "production") fixture.EnvironmentName = Environments.Production;
        if (issue == "external-endpoint")
            fixture.Configuration["Identity:Directory:DevelopmentEndpoint"] = "https://untrusted.example/";
        if (issue == "external-authority")
            fixture.Configuration["Identity:LoopbackAuthority"] = "https://untrusted.example/";
        if (issue == "external-listener") fixture.Configuration["urls"] = "http://0.0.0.0:8080";
        if (issue == "no-authority-opt-in") fixture.Configuration["Identity:AllowLoopbackAuthority"] = "false";

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.NotEqual(0, exitCode);
        using var record = fixture.Record("ActionRequired", "InvalidInput", false, false, false);
        Assert.Empty(fixture.Handler.Requests);
        fixture.Credential.VerifyNoOtherCalls();
    }

    private sealed class Fixture : IDisposable
    {
        public const string WorkAddress = "private.person@example.com";
        public Guid OwnerId { get; } = Guid.NewGuid();
        public Guid TenantId { get; } = Guid.NewGuid();
        public Guid ClientId { get; } = Guid.NewGuid();
        public IConfigurationRoot Configuration { get; }
        public ServiceCollection Services { get; } = new();
        public Handler Handler { get; } = new();
        public Mock<TokenCredential> Credential { get; } = new(MockBehavior.Strict);
        public Mock<IHostedService> HostedService { get; } = new(MockBehavior.Strict);
        public StringWriter Output { get; } = new();
        public EnterpriseDirectoryOptions OriginalOptions { get; }
        public GraphEnterpriseDirectory OriginalDirectory { get; }
        public string? RuntimeClientId { get; set; }
        public string EnvironmentName { get; set; } = Environments.Production;
        public string[] Arguments =>
        [
            "--diagnose-directory", OwnerId.ToString(),
            "--diagnose-directory-tenant", TenantId.ToString(),
            "--diagnose-directory-client", ClientId.ToString()
        ];

        public Fixture(bool enabled = false)
        {
            RuntimeClientId = ClientId.ToString();
            OriginalOptions = new EnterpriseDirectoryOptions { Enabled = enabled };
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:Mode"] = "Entra",
                ["Identity:DirectoryTenantId"] = TenantId.ToString(),
                ["Identity:ManagedIdentityClientId"] = ClientId.ToString(),
                ["AZURE_CLIENT_ID"] = ClientId.ToString(),
                ["Identity:Directory:Enabled"] = enabled.ToString()
            }).Build();
            Credential.Setup(credential =>
                    credential.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AccessToken("private-runtime-token", DateTimeOffset.UtcNow.AddMinutes(5)));
            var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
            factory.Setup(clients => clients.CreateClient(GraphEnterpriseDirectory.HttpClientName))
                .Returns(() => new HttpClient(Handler, disposeHandler: false));
            OriginalDirectory = new GraphEnterpriseDirectory(factory.Object, Credential.Object, OriginalOptions,
                new IdentityOptions { Mode = IdentityMode.Entra, DirectoryTenantId = TenantId });
            Services.AddLogging();
            Services.AddSingleton(Credential.Object);
            Services.AddSingleton(factory.Object);
            Services.AddSingleton(OriginalOptions);
            Services.AddSingleton<IEnterpriseDirectory>(OriginalDirectory);
            Services.AddSingleton(HostedService.Object);
            Services.AddSingleton<IHarnessStore>(_ =>
                throw new InvalidOperationException("Harness must not be resolved."));
            Services.AddSingleton<ITenantUserStore>(_ =>
                throw new InvalidOperationException("Access store must not be resolved."));
            Handler.OwnerBody = Profile();
        }

        public Task<int> ExecuteAsync(
            string[]? arguments = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var environment = new Mock<IHostEnvironment>();
            environment.SetupGet(host => host.EnvironmentName).Returns(EnvironmentName);
            return DirectoryConnectionDiagnostic.ExecuteAsync(arguments ?? Arguments, Configuration, environment.Object,
                Services, RuntimeClientId, Output, cancellationToken, timeout);
        }

        public void UseLoopback()
        {
            EnvironmentName = Environments.Development;
            Configuration["Identity:Directory:DevelopmentEndpoint"] = "http://127.0.0.1:19090/v1.0/";
            Configuration["Identity:AllowLoopbackAuthority"] = "true";
            Configuration["Identity:LoopbackAuthority"] = "http://127.0.0.1:19091/";
            Configuration["urls"] = "http://127.0.0.1:19092";
        }

        public string Profile(Guid? id = null, bool enabled = true, bool member = true,
            string mail = WorkAddress, string upn = WorkAddress) => JsonSerializer.Serialize(new
        {
            id = id ?? OwnerId,
            displayName = "private-display-name",
            mail,
            userPrincipalName = upn,
            accountEnabled = enabled,
            userType = member ? "Member" : "Guest"
        });

        public JsonDocument Record(
            string status, string code, bool runtimeVerified, bool ownerEligible, bool searchVerified)
        {
            var line = Assert.Single(
                Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            const string prefix = "DOCINTEL_DIRECTORY_DIAGNOSTIC ";
            Assert.StartsWith(prefix, line);
            Assert.DoesNotContain("private-", line);
            Assert.DoesNotContain(OwnerId.ToString(), line);
            Assert.DoesNotContain(TenantId.ToString(), line);
            Assert.DoesNotContain(ClientId.ToString(), line);
            Assert.DoesNotContain(WorkAddress, line);
            var document = JsonDocument.Parse(line[prefix.Length..]);
            var root = document.RootElement;
            Assert.Equal(new[]
            {
                "schemaVersion", "status", "failureCode", "directoryEnabled",
                "runtimeIdentityVerified", "ownerEligible", "searchVerified"
            }, root.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(status, root.GetProperty("status").GetString());
            Assert.Equal(code, root.GetProperty("failureCode").GetString());
            Assert.Equal(runtimeVerified, root.GetProperty("runtimeIdentityVerified").GetBoolean());
            Assert.Equal(ownerEligible, root.GetProperty("ownerEligible").GetBoolean());
            Assert.Equal(searchVerified, root.GetProperty("searchVerified").GetBoolean());
            return document;
        }

        public void Dispose()
        {
            Output.Dispose();
            Handler.Dispose();
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public string OwnerBody { get; set; } = "{}";
        public string SearchBody { get; set; } = """{"value":[]}""";
        public HttpStatusCode OwnerStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode SearchStatus { get; set; } = HttpStatusCode.OK;
        public bool BlockRequests { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((request.Method, request.RequestUri!));
            Tokens.Add(cancellationToken);
            if (BlockRequests) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var ownerRequest = Requests.Count == 1;
            return new HttpResponseMessage(ownerRequest ? OwnerStatus : SearchStatus)
            {
                Content = new StringContent(ownerRequest ? OwnerBody : SearchBody,
                    System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
