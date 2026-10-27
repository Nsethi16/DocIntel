using System.Globalization;
using System.Net;
using System.Text.Json;
using DocIntel.Api.Diagnostics;
using DocIntel.Core;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class NativeDecisionDiagnosticTests
{
    private const string PrivateMarker = "synthetic-private-provider-token-or-tenant";
    private const string Prefix = "DOCINTEL_DECISION_DIAGNOSTIC ";

    [Theory]
    [InlineData("--diagnose-decision")]
    [InlineData("--diagnose-decision=unexpected")]
    [InlineData("--diagnose-decision-private")]
    [InlineData("--diagnose-decisionanything")]
    [InlineData("--DIAGNOSE-DECISION")]
    public void Recognizes_Malformed_Diagnostic_Prefixes_Before_Host_Startup(string argument)
    {
        // Arrange
        var arguments = new[] { "other", argument };

        // Act
        var requested = NativeDecisionDiagnostic.IsRequested(arguments);

        // Assert
        Assert.True(requested);
    }

    [Theory]
    [InlineData("--diagnose-directory")]
    [InlineData("--diagnose")]
    [InlineData("ordinary")]
    public void Does_Not_Claim_Unrelated_Commands(string argument)
    {
        // Arrange
        var arguments = new[] { argument };

        // Act
        var requested = NativeDecisionDiagnostic.IsRequested(arguments);

        // Assert
        Assert.False(requested);
        Assert.False(NativeDecisionDiagnostic.IsRequested([]));
    }

    [Fact]
    public async Task Initialization_Failure_Writes_Exactly_One_Fixed_Unavailable_Record()
    {
        // Arrange
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        // Act
        var exitCode = await NativeDecisionDiagnostic.WriteUnavailableAsync(output);

        // Assert
        Assert.Equal(1, exitCode);
        AssertRecord(output, 0, success: false);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("undefined")]
    public async Task Resolves_Only_The_Native_Client_And_Performs_One_Synthetic_Check(string choice)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Client.Setup(client => client.ChooseAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MicrosoftDecisionResult(choice, 0.9, "Decision-1"));

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(0, exitCode);
        AssertRecord(fixture.Output, 200, success: true);
        fixture.Models.VerifyGet(models => models.AllowsDevelopmentSimulation, Times.Once);
        fixture.Models.Verify(models => models.ResolveDecisionClient(
            It.Is<TenantHarness>(tenant =>
                tenant.DecisionModelId == "decision-1" && tenant.DefaultModelId == "decision-1" &&
                tenant.AllowedModels.SequenceEqual(new[] { "decision-1" }) && tenant.Capabilities.Count == 0),
            "decision-1"), Times.Once);
        fixture.Client.Verify(client => client.ChooseAsync(
            "Synthetic connectivity check: ready.",
            It.Is<string>(instructions => instructions.Contains("ready") && instructions.Contains("undefined")),
            It.Is<IReadOnlyDictionary<string, string>>(criteria =>
                criteria.Count == 2 && criteria.ContainsKey("ready") && criteria.ContainsKey("undefined")),
            It.Is<CancellationToken>(token => token.CanBeCanceled)), Times.Once);
        fixture.Client.Verify(client => client.Dispose(), Times.Once);
        fixture.Client.VerifyNoOtherCalls();
        fixture.Models.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(429)]
    [InlineData(307)]
    [InlineData(0)]
    public async Task Preserves_Only_The_Numeric_Http_Status_And_Never_Provider_Text(int status)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Client.Setup(client => client.ChooseAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(PrivateMarker, new IOException(PrivateMarker),
                status == 0 ? null : (HttpStatusCode)status));

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, exitCode);
        AssertRecord(fixture.Output, status, success: false);
        Assert.DoesNotContain(PrivateMarker, fixture.Output.ToString(), StringComparison.Ordinal);
        fixture.Client.Verify(client => client.Dispose(), Times.Once);
    }

    [Theory]
    [InlineData("BadRequest")]
    [InlineData("InvalidRequest")]
    [InlineData("InvalidRequestContent")]
    [InlineData("MissingApiVersionParameter")]
    [InlineData("InvalidApiVersionParameter")]
    [InlineData("DeploymentNotFound")]
    [InlineData("ModelNotFound")]
    [InlineData("unknown_model")]
    [InlineData("UnsupportedTransferEncoding")]
    [InlineData("ContentLengthRequired")]
    public async Task Publishes_Only_Allowlisted_Native_Error_Codes_And_Not_Other_Exception_Data(string code)
    {
        // Arrange
        using var fixture = new Fixture();
        var error = new HttpRequestException(PrivateMarker, new IOException(PrivateMarker),
            HttpStatusCode.BadRequest);
        error.Data["NativeDecisionErrorCode"] = code;
        error.Data["token"] = PrivateMarker;
        fixture.Client.Setup(client => client.ChooseAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(error);

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, exitCode);
        AssertRecord(fixture.Output, 400, success: false, code);
        Assert.DoesNotContain(PrivateMarker, fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("injection")]
    [InlineData("wrong-case")]
    [InlineData("not-string")]
    public async Task Rejects_Arbitrary_Or_NonString_Native_Error_Metadata(string issue)
    {
        // Arrange
        using var fixture = new Fixture();
        var error = new HttpRequestException(PrivateMarker, null, HttpStatusCode.BadRequest);
        error.Data["NativeDecisionErrorCode"] = issue switch
        {
            "injection" => "BadRequest\n" + PrivateMarker,
            "wrong-case" => "badrequest",
            "not-string" => new object(),
            _ => PrivateMarker
        };
        fixture.Client.Setup(client => client.ChooseAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(error);

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, exitCode);
        AssertRecord(fixture.Output, 400, success: false);
        Assert.DoesNotContain(PrivateMarker, fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("suffix")]
    [InlineData("equals")]
    [InlineData("uppercase")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("null")]
    public async Task Rejects_Invalid_Arguments_Without_Accessing_The_Model_Catalog(string issue)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Arguments = issue switch
        {
            "empty" => [],
            "suffix" => ["--diagnose-decision-" + PrivateMarker],
            "equals" => ["--diagnose-decision=" + PrivateMarker],
            "uppercase" => ["--DIAGNOSE-DECISION"],
            "duplicate" => ["--diagnose-decision", "--diagnose-decision"],
            "extra" => ["--diagnose-decision", PrivateMarker],
            _ => null!
        };

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, exitCode);
        AssertRecord(fixture.Output, 0, success: false);
        fixture.Models.VerifyNoOtherCalls();
        fixture.Client.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("")]
    [InlineData("private-environment")]
    public async Task Rejects_NonProduction_Environments_Without_Accessing_The_Model_Catalog(string environment)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Environment.SetupGet(host => host.EnvironmentName).Returns(environment);

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, exitCode);
        AssertRecord(fixture.Output, 0, success: false);
        fixture.Models.VerifyNoOtherCalls();
        fixture.Client.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Rejects_Missing_Or_Invalid_Runtime_Client_Ids_Before_Model_Access(string? clientId)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.RuntimeClientId = clientId;

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, exitCode);
        AssertRecord(fixture.Output, 0, success: false);
        fixture.Models.VerifyNoOtherCalls();
        fixture.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Forbids_Simulation_Without_Resolving_Any_Client()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Models.SetupGet(models => models.AllowsDevelopmentSimulation).Returns(true);

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, exitCode);
        AssertRecord(fixture.Output, 0, success: false);
        fixture.Models.VerifyGet(models => models.AllowsDevelopmentSimulation, Times.Once);
        fixture.Models.VerifyNoOtherCalls();
        fixture.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Missing_Native_Client_Does_Not_Fall_Back_To_Chat()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Models.Setup(models => models.ResolveDecisionClient(It.IsAny<TenantHarness>(), "decision-1"))
            .Returns((IMicrosoftDecisionClient?)null);

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, exitCode);
        AssertRecord(fixture.Output, 0, success: false);
        fixture.Models.Verify(models => models.ResolveChatClient(
            It.IsAny<TenantHarness>(), It.IsAny<string?>()), Times.Never);
        fixture.Models.Verify(models => models.ResolveRequiredChatClient(
            It.IsAny<TenantHarness>(), It.IsAny<string?>()), Times.Never);
        fixture.Client.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("invalid-response")]
    [InlineData("unexpected")]
    public async Task Other_Explicit_Failures_Emit_Only_The_Unavailable_Record(string issue)
    {
        // Arrange
        using var fixture = new Fixture();
        if (issue == "configuration")
        {
            fixture.Models.Setup(models => models.ResolveDecisionClient(It.IsAny<TenantHarness>(), "decision-1"))
                .Throws(new InvalidOperationException(PrivateMarker));
        }
        else
        {
            Exception error = issue == "invalid-response"
                ? new InvalidDataException(PrivateMarker)
                : new ArgumentException(PrivateMarker);
            fixture.Client.Setup(client => client.ChooseAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(error);
        }

        // Act
        var exitCode = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, exitCode);
        AssertRecord(fixture.Output, 0, success: false);
        Assert.DoesNotContain(PrivateMarker, fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreCanceled_Probes_Do_Not_Access_The_Model_Catalog()
    {
        // Arrange
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var exitCode = await fixture.ExecuteAsync(cancellation.Token);

        // Assert
        Assert.Equal(1, exitCode);
        AssertRecord(fixture.Output, 0, success: false);
        fixture.Models.VerifyNoOtherCalls();
        fixture.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Cancellation_Bounds_Even_A_NonCooperative_Native_Client_And_Disposes_It()
    {
        // Arrange
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource<MicrosoftDecisionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Setup(client => client.ChooseAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => started.TrySetResult())
            .Returns(blocked.Task);

        // Act
        var pending = fixture.ExecuteAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var exitCode = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        blocked.SetResult(new MicrosoftDecisionResult("ready", 1, "Decision-1"));

        // Assert
        Assert.Equal(1, exitCode);
        AssertRecord(fixture.Output, 0, success: false);
        fixture.Client.Verify(client => client.Dispose(), Times.Once);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task Configured_Probe_Owns_And_Disposes_The_Isolated_Provider_Without_Starting_Other_Services(
        int status)
    {
        // Arrange
        using var fixture = new Fixture();
        var ownedCatalog = fixture.Models.As<IAsyncDisposable>();
        ownedCatalog.Setup(catalog => catalog.DisposeAsync()).Returns(ValueTask.CompletedTask);
        if (status != 200)
        {
            fixture.Client.Setup(client => client.ChooseAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException(PrivateMarker, null, (HttpStatusCode)status));
        }
        var services = new ServiceCollection();
        services.AddSingleton<IModelCatalog>(_ => fixture.Models.Object);
        var unrelatedCalls = 0;
        services.AddSingleton<IHostedService>(_ =>
        {
            unrelatedCalls++;
            throw new InvalidOperationException(PrivateMarker);
        });
        services.AddSingleton<IHarnessStore>(_ =>
        {
            unrelatedCalls++;
            throw new InvalidOperationException(PrivateMarker);
        });
        var configuration = new ConfigurationBuilder().Build();

        // Act
        var exitCode = await NativeDecisionDiagnostic.ExecuteConfiguredAsync(
            fixture.Arguments, configuration, fixture.Environment.Object, services,
            fixture.RuntimeClientId, fixture.Output);

        // Assert
        Assert.Equal(status == 200 ? 0 : 1, exitCode);
        AssertRecord(fixture.Output, status, success: status == 200);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AzureIntegrationOptions));
        Assert.Equal(0, unrelatedCalls);
        ownedCatalog.Verify(catalog => catalog.DisposeAsync(), Times.Once);
        fixture.Client.Verify(client => client.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Configured_Registration_Failures_Emit_One_Fixed_Record_Without_Provider_Text()
    {
        // Arrange
        using var fixture = new Fixture();
        var services = new ServiceCollection();
        services.AddSingleton<IModelCatalog>(_ => fixture.Models.Object);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Models:AllowDevelopmentSimulation"] = PrivateMarker }).Build();

        // Act
        var exitCode = await NativeDecisionDiagnostic.ExecuteConfiguredAsync(
            fixture.Arguments, configuration, fixture.Environment.Object, services,
            fixture.RuntimeClientId, fixture.Output);

        // Assert
        Assert.Equal(1, exitCode);
        AssertRecord(fixture.Output, 0, success: false);
        Assert.DoesNotContain(PrivateMarker, fixture.Output.ToString(), StringComparison.Ordinal);
        fixture.Models.VerifyNoOtherCalls();
        fixture.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Configured_Resolution_Failures_Still_Dispose_Activated_Dependencies()
    {
        // Arrange
        using var fixture = new Fixture();
        var owned = new DisposalProbe();
        var services = new ServiceCollection();
        services.AddSingleton(_ => owned);
        services.AddSingleton<IModelCatalog>(provider =>
        {
            provider.GetRequiredService<DisposalProbe>();
            throw new InvalidOperationException(PrivateMarker);
        });
        var configuration = new ConfigurationBuilder().Build();

        // Act
        var exitCode = await NativeDecisionDiagnostic.ExecuteConfiguredAsync(
            fixture.Arguments, configuration, fixture.Environment.Object, services,
            fixture.RuntimeClientId, fixture.Output);

        // Assert
        Assert.Equal(1, exitCode);
        AssertRecord(fixture.Output, 0, success: false);
        Assert.True(owned.Disposed);
        Assert.DoesNotContain(PrivateMarker, fixture.Output.ToString(), StringComparison.Ordinal);
        fixture.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Configured_Disposal_Failures_Replace_The_Buffered_Success_With_One_Unavailable_Record()
    {
        // Arrange
        using var fixture = new Fixture();
        var ownedCatalog = fixture.Models.As<IAsyncDisposable>();
        ownedCatalog.Setup(catalog => catalog.DisposeAsync())
            .Throws(new InvalidOperationException(PrivateMarker));
        var services = new ServiceCollection();
        services.AddSingleton<IModelCatalog>(_ => fixture.Models.Object);
        var configuration = new ConfigurationBuilder().Build();

        // Act
        var exitCode = await NativeDecisionDiagnostic.ExecuteConfiguredAsync(
            fixture.Arguments, configuration, fixture.Environment.Object, services,
            fixture.RuntimeClientId, fixture.Output);

        // Assert
        Assert.Equal(1, exitCode);
        AssertRecord(fixture.Output, 0, success: false);
        Assert.DoesNotContain(PrivateMarker, fixture.Output.ToString(), StringComparison.Ordinal);
        ownedCatalog.Verify(catalog => catalog.DisposeAsync(), Times.Once);
        fixture.Client.Verify(client => client.Dispose(), Times.Once);
    }

    private static void AssertRecord(
        StringWriter output, int httpStatus, bool success, string? providerCode = null)
    {
        var line = Assert.Single(output.ToString().Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith(Prefix, line, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(line[Prefix.Length..]);
        var record = json.RootElement;
        Assert.Equal(5, record.EnumerateObject().Count());
        Assert.Equal(1, record.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(httpStatus, record.GetProperty("httpStatus").GetInt32());
        Assert.Equal(providerCode ?? (success ? "None" : "UnclassifiedError"),
            record.GetProperty("providerCode").GetString());
        Assert.Equal(success, record.GetProperty("answerPresent").GetBoolean());
        Assert.Equal(success ? "Object" : "None", record.GetProperty("probabilityShape").GetString());
    }

    private sealed class Fixture : IDisposable
    {
        public Mock<IModelCatalog> Models { get; } = new(MockBehavior.Strict);
        public Mock<IMicrosoftDecisionClient> Client { get; } = new(MockBehavior.Strict);
        public Mock<IHostEnvironment> Environment { get; } = new();
        public StringWriter Output { get; } = new(CultureInfo.InvariantCulture);
        public string[] Arguments { get; set; } = ["--diagnose-decision"];
        public string? RuntimeClientId { get; set; } = "93c95720-84ed-49bc-8ab1-07f14c7f5d14";

        public Fixture()
        {
            Environment.SetupGet(environment => environment.EnvironmentName).Returns(Environments.Production);
            Models.SetupGet(models => models.AllowsDevelopmentSimulation).Returns(false);
            Models.Setup(models => models.ResolveDecisionClient(It.IsAny<TenantHarness>(), "decision-1"))
                .Returns(Client.Object);
            Client.Setup(client => client.ChooseAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MicrosoftDecisionResult("ready", 0.95, "Decision-1"));
            Client.Setup(client => client.Dispose());
        }

        public Task<int> ExecuteAsync(CancellationToken cancellationToken = default) =>
            NativeDecisionDiagnostic.ExecuteAsync(
                Arguments, Models.Object, Environment.Object, RuntimeClientId, Output, cancellationToken);

        public void Dispose() => Output.Dispose();
    }

    private sealed class DisposalProbe : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
