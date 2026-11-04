using System.Net;
using System.Text;
using DocIntel.Api.Channels;
using DocIntel.Api.Diagnostics;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Harness;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class EmailChannelDiagnosticTests
{
    [Theory]
    [InlineData(false, "tenant", "Disabled")]
    [InlineData(true, "another", "NotApplicable")]
    public async Task Disabled_Or_Different_Workspace_Does_Not_Read_State(
        bool enabled, string tenantId, string expected)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Options.Enabled = enabled;

        // Act
        var result = await fixture.ExecuteAsync(tenantId);

        // Assert
        Assert.Equal(0, result);
        Assert.Contains($"\"status\":\"{expected}\"", fixture.Output.ToString());
        fixture.Harness.VerifyNoOtherCalls();
        fixture.Store.VerifyNoOtherCalls();
        fixture.Mailbox.VerifyNoOtherCalls();
        fixture.Authorization.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("inactive", "Inactive")]
    [InlineData("email", "Disabled")]
    [InlineData("ask", "AskAiDisabled")]
    [InlineData("missing", "NotFound")]
    public async Task Workspace_Gates_Are_Reported_Without_Reading_Mail(string condition, string expected)
    {
        // Arrange
        using var fixture = new Fixture();
        if (condition == "inactive") fixture.Tenant!.IsActive = false;
        if (condition == "email") fixture.Tenant!.Agent = new AgentProfile();
        if (condition == "ask") fixture.Tenant!.Capabilities[FeatureHarness.AskAiKey].Enabled = false;
        if (condition == "missing") fixture.Tenant = null;

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(0, result);
        Assert.Contains($"\"stage\":\"WorkspaceEmail\",\"status\":\"{expected}\"", fixture.Output.ToString());
        fixture.Store.VerifyNoOtherCalls();
        fixture.Mailbox.VerifyNoOtherCalls();
        fixture.Authorization.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Eligible_Linked_Recipient_Is_Checked_Without_Printing_Identity_Or_Changing_State()
    {
        // Arrange
        using var fixture = new Fixture();
        var sender = fixture.State.Senders.Single();
        fixture.Authorization.Setup(service => service.ResolveAsync(
            "tenant", sender.Addresses[0], It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VerifiedEmailActor(sender.Actor, sender.Addresses[0]));

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(0, result);
        var text = fixture.Output.ToString();
        Assert.Contains("\"status\":\"ApprovedUsers\"", text);
        Assert.Contains("\"stage\":\"MailboxAuthorization\",\"status\":\"Authorized\"", text);
        Assert.Contains("\"status\":\"Eligible\"", text);
        Assert.Contains("\"inspections\":1,\"matches\":1", text);
        Assert.DoesNotContain("private", text);
        Assert.DoesNotContain(sender.Actor.ObjectId.ToString(), text);
        Assert.DoesNotContain(sender.Addresses[0], text);
        fixture.Store.Verify(store => store.GetAsync("tenant", It.IsAny<CancellationToken>()), Times.Once);
        fixture.Store.VerifyNoOtherCalls();
        fixture.Mailbox.Verify(mailbox => mailbox.GetConnectionProfileAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Mailbox.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("mailbox", "NotConnected")]
    [InlineData("reconnect", "ReconnectionRequired")]
    [InlineData("sender", "NotLinked")]
    [InlineData("recipient", "Ineligible")]
    public async Task Missing_Or_Rejected_Connections_Are_Explicit(string condition, string expected)
    {
        // Arrange
        using var fixture = new Fixture();
        if (condition == "mailbox") fixture.State = fixture.State with { Mailbox = null };
        if (condition == "reconnect")
            fixture.State = fixture.State with
            {
                Mailbox = fixture.State.Mailbox! with { Status = PersonalEmailConnectionStatus.ReconnectionRequired }
            };
        if (condition == "sender") fixture.State = fixture.State with { Senders = [] };

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(0, result);
        Assert.Contains($"\"status\":\"{expected}\"", fixture.Output.ToString());
        Assert.DoesNotContain("private", fixture.Output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_Confirmation_Does_Not_Perform_Automatic_Recipient_Checks(bool workspaceOverride)
    {
        // Arrange
        using var fixture = new Fixture();
        if (workspaceOverride)
            fixture.Tenant!.Agent = fixture.Tenant.Agent with { EmailConfirmationRequired = true };
        else fixture.Options.AuthorizationMode = EmailAuthorizationMode.ConfirmSender;

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(0, result);
        Assert.Contains("\"status\":\"AutomaticPolicyNotApplicable\"", fixture.Output.ToString());
        fixture.Authorization.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "GraphForbidden")]
    [InlineData(HttpStatusCode.Unauthorized, "GraphUnauthorized")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "GraphUnavailable")]
    public async Task Graph_Failures_Expose_Only_An_Allowlisted_Category(HttpStatusCode status, string code)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Authorization.Setup(service => service.ResolveAsync(
            "tenant", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("private-provider-body", null, status));

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, result);
        Assert.Contains($"\"status\":\"Failed\",\"failureCode\":\"{code}\"", fixture.Output.ToString());
        Assert.DoesNotContain("private", fixture.Output.ToString());
    }

    [Fact]
    public async Task Revoked_Mailbox_Owner_Is_Reported_Before_Recipient_Checks()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Mailbox.Setup(mailbox => mailbox.GetConnectionProfileAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("private-owner"));

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, result);
        Assert.Contains("\"stage\":\"MailboxAuthorization\",\"status\":\"Failed\"", fixture.Output.ToString());
        Assert.Contains("\"failureCode\":\"AccessRevoked\"", fixture.Output.ToString());
        Assert.DoesNotContain("private", fixture.Output.ToString());
        fixture.Authorization.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Recipient_Inspection_Is_Bounded_And_Truncation_Is_Explicit()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.State = fixture.State with
        {
            Senders = Enumerable.Range(0, 21).Select(index => Credential($"private-{index}@example.test")).ToList()
        };

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(0, result);
        Assert.Contains("\"status\":\"Partial\",\"failureCode\":\"InspectionLimit\"", fixture.Output.ToString());
        Assert.Contains("\"inspections\":20,\"matches\":0", fixture.Output.ToString());
        fixture.Authorization.Verify(service => service.ResolveAsync(
            "tenant", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(20));
        Assert.DoesNotContain("private", fixture.Output.ToString());
    }

    [Fact]
    public async Task Invalid_State_And_Cancellation_Are_Not_Success_Shaped()
    {
        // Arrange
        using var invalid = new Fixture();
        invalid.State = invalid.State with { TenantId = "another" };
        using var cancelled = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var invalidResult = await invalid.ExecuteAsync();
        var cancelledResult = await cancelled.ExecuteAsync(cancellationToken: cancellation.Token);

        // Assert
        Assert.Equal(1, invalidResult);
        Assert.Contains("\"failureCode\":\"InvalidState\"", invalid.Output.ToString());
        Assert.Equal(1, cancelledResult);
        Assert.Contains("\"failureCode\":\"TimedOut\"", cancelled.Output.ToString());
        cancelled.Harness.VerifyNoOtherCalls();
        cancelled.Store.VerifyNoOtherCalls();
    }

    private static PersonalEmailCredential Credential(string address) => new()
    {
        BindingId = Guid.NewGuid(),
        Actor = new ActorIdentity(Guid.NewGuid(), Guid.NewGuid()),
        HomeAccountId = "private-home-" + Guid.NewGuid().ToString("N"),
        Subject = "private-subject",
        GraphId = "private-graph-id",
        Addresses = [address],
        TokenCache = Encoding.UTF8.GetBytes("private-token-cache"),
        VerifiedAtUtc = DateTimeOffset.UtcNow
    };

    private sealed class Fixture : IDisposable
    {
        public EmailChannelOptions Options { get; } = new()
        {
            Enabled = true,
            ApplicationTenantId = "tenant",
            Provider = EmailMailboxProvider.OutlookCom,
            AuthorizationMode = EmailAuthorizationMode.ApprovedUsers
        };
        public TenantHarness? Tenant { get; set; } = new()
        {
            TenantId = "tenant", DisplayName = "Workspace", Agent = new AgentProfile { EmailEnabled = true }
        };
        public PersonalEmailState State { get; set; } = new()
        {
            TenantId = "tenant",
            Mailbox = Credential("private-vault@example.test"),
            Senders = [Credential("private-sender@example.test")]
        };
        public Mock<IHarnessStore> Harness { get; } = new(MockBehavior.Strict);
        public Mock<IPersonalEmailStateStore> Store { get; } = new(MockBehavior.Strict);
        public Mock<IConnectedMailboxClient> Mailbox { get; } = new(MockBehavior.Strict);
        public Mock<IAssignedEmailAuthorization> Authorization { get; } = new(MockBehavior.Strict);
        public StringWriter Output { get; } = new();

        public Fixture()
        {
            Harness.Setup(store => store.GetTenantAsync("tenant", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Tenant);
            Store.Setup(store => store.GetAsync("tenant", It.IsAny<CancellationToken>())).ReturnsAsync(() => State);
            Mailbox.Setup(mailbox => mailbox.GetConnectionProfileAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("private-profile");
            Authorization.Setup(service => service.ResolveAsync(
                "tenant", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((VerifiedEmailActor?)null);
        }

        public Task<int> ExecuteAsync(string tenantId = "tenant", CancellationToken cancellationToken = default) =>
            EmailChannelDiagnostic.ExecuteAsync(tenantId, Options, Harness.Object, Store.Object,
                Authorization.Object, Mailbox.Object, Output, cancellationToken);

        public void Dispose() => Output.Dispose();
    }
}
