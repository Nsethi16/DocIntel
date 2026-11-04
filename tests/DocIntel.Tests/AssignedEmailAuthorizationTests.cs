using System.Text;
using DocIntel.Api.Channels;
using DocIntel.Api.Identity;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Harness;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class AssignedEmailAuthorizationTests
{
    [Theory]
    [InlineData(TenantUserRole.User)]
    [InlineData(TenantUserRole.TenantAdmin)]
    public async Task Admin_Added_Active_Readers_And_Admins_Are_Eligible_For_Automatic_Replies(TenantUserRole role)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Assign(fixture.Actor, role);

        // Act
        var result = await fixture.Service.ResolveAsync("tenant", Fixture.WorkAddress, CancellationToken.None);

        // Assert
        Assert.Equal(fixture.Actor, result!.Actor);
        Assert.Equal(Fixture.WorkAddress, result.Address);
        fixture.Personal.Verify(value => value.OwnsSenderAsync(It.IsAny<ActorIdentity>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(TenantUserStatus.Disabled)]
    [InlineData(TenantUserStatus.Removed)]
    public async Task Disabled_And_Removed_Assignments_Do_Not_Authorize_Their_Verified_Personal_Address(
        TenantUserStatus status)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Link(fixture.Actor);
        fixture.Assign(fixture.Actor, status: status);

        // Act
        var result = await fixture.Service.ResolveAsync("tenant", Fixture.PersonalAddress, CancellationToken.None);

        // Assert
        Assert.Null(result);
        fixture.Personal.Verify(value => value.OwnsSenderAsync(It.IsAny<ActorIdentity>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_Personal_Address_Requires_An_Existing_Assignment_And_Fresh_Profile_Ownership()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Link(fixture.Actor);

        // Act
        var unassigned = await fixture.Service.ResolveAsync("tenant", Fixture.PersonalAddress, CancellationToken.None);
        fixture.Assign(fixture.Actor);
        var accepted = await fixture.Service.ResolveAsync("tenant", Fixture.PersonalAddress, CancellationToken.None);
        fixture.Personal.Setup(value => value.OwnsSenderAsync(
            fixture.Actor, Fixture.PersonalAddress, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PersonalEmailConnectionRequiredException());
        var revoked = await fixture.Service.ResolveAsync("tenant", Fixture.PersonalAddress, CancellationToken.None);

        // Assert
        Assert.Null(unassigned);
        Assert.Equal(fixture.Actor, accepted!.Actor);
        Assert.Equal(Fixture.PersonalAddress, accepted.Address);
        Assert.Null(revoked);
    }

    [Fact]
    public async Task Conflicting_Directory_And_Linked_Recipient_Identities_Are_Rejected()
    {
        // Arrange
        var fixture = new Fixture();
        var other = new ActorIdentity(fixture.Actor.DirectoryTenantId, Guid.NewGuid());
        fixture.Profiles[other.ObjectId] = fixture.Profile(other, Fixture.PersonalAddress);
        fixture.Assign(fixture.Actor);
        fixture.Assign(other);
        fixture.Link(fixture.Actor);

        // Act
        var result = await fixture.Service.ResolveAsync("tenant", Fixture.PersonalAddress, CancellationToken.None);

        // Assert
        Assert.Null(result);
        fixture.Personal.Verify(value => value.OwnsSenderAsync(It.IsAny<ActorIdentity>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ambiguous_Directory_Matches_Cannot_Fall_Back_To_A_Personal_Link()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Assign(fixture.Actor);
        fixture.Link(fixture.Actor);
        fixture.Directory.Setup(value => value.FindByEmailAsync(
            Fixture.PersonalAddress, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmbiguousEmailAddressException());

        // Act
        var result = await fixture.Service.ResolveAsync("tenant", Fixture.PersonalAddress, CancellationToken.None);

        // Assert
        Assert.Null(result);
        fixture.Personal.Verify(value => value.OwnsSenderAsync(It.IsAny<ActorIdentity>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("disabled")]
    [InlineData("guest")]
    [InlineData("service-principal")]
    [InlineData("outside-directory")]
    [InlineData("workspace-disabled")]
    [InlineData("email-disabled")]
    [InlineData("confirmation-required")]
    [InlineData("operator-not-opted-in")]
    public async Task Ineligible_Recipients_And_Disabled_Policies_Are_Not_Automatically_Admitted(string issue)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Assign(fixture.Actor);
        fixture.Link(fixture.Actor);
        switch (issue)
        {
            case "unknown": fixture.Snapshot = fixture.Snapshot with { Users = [] }; break;
            case "disabled":
                fixture.Profiles[fixture.Actor.ObjectId] = fixture.Profiles[fixture.Actor.ObjectId] with { AccountEnabled = false };
                break;
            case "guest":
                fixture.Profiles[fixture.Actor.ObjectId] = fixture.Profiles[fixture.Actor.ObjectId] with { Member = false };
                break;
            case "service-principal":
                fixture.Identity.Memberships.Add(new()
                {
                    DirectoryTenantId = fixture.Actor.DirectoryTenantId, ObjectId = fixture.Actor.ObjectId,
                    ApplicationTenantId = "tenant", ServicePrincipal = true
                });
                break;
            case "outside-directory":
                fixture.Directory.SetupGet(value => value.DirectoryTenantId).Returns(Guid.NewGuid());
                break;
            case "workspace-disabled": fixture.Tenant.IsActive = false; break;
            case "email-disabled": fixture.Tenant.Agent = fixture.Tenant.Agent with { EmailEnabled = false }; break;
            case "confirmation-required":
                fixture.Tenant.Agent = fixture.Tenant.Agent with { EmailConfirmationRequired = true }; break;
            case "operator-not-opted-in":
                fixture.Options.AuthorizationMode = EmailAuthorizationMode.ConfirmSender; break;
        }

        // Act
        var result = await fixture.Service.ResolveAsync("tenant", Fixture.PersonalAddress, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task Directory_Lookup_Failures_Are_Not_Silently_Treated_As_Unassigned_Users()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Directory.Setup(value => value.FindByEmailAsync(Fixture.WorkAddress, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Directory temporarily unavailable."));

        // Act / Assert
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Service.ResolveAsync("tenant", Fixture.WorkAddress, CancellationToken.None));
    }

    [Fact]
    public async Task A_Verified_Operator_Bootstrap_User_Can_Work_Without_An_Enterprise_Directory()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Directory.SetupGet(value => value.IsConfigured).Returns(false);
        fixture.Identity.Memberships.Add(new()
        {
            DirectoryTenantId = fixture.Actor.DirectoryTenantId, ObjectId = fixture.Actor.ObjectId,
            ApplicationTenantId = "tenant", Role = TenantRole.Reader, AllDocuments = false, DocumentIds = ["allowed"]
        });
        fixture.Link(fixture.Actor);

        // Act
        var result = await fixture.Service.ResolveAsync("tenant", Fixture.PersonalAddress, CancellationToken.None);
        var scope = await fixture.Users.GetAccessAsync(fixture.Actor, "tenant", CancellationToken.None);

        // Assert
        Assert.Equal(fixture.Actor, result!.Actor);
        Assert.True(scope.CanReadDocument("allowed"));
        Assert.False(scope.CanReadDocument("other"));
    }

    private sealed class Fixture
    {
        internal const string WorkAddress = "reader@example.com";
        internal const string PersonalAddress = "reader-test@hotmail.com";
        internal ActorIdentity Actor { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        internal EmailChannelOptions Options { get; }
        internal IdentityOptions Identity { get; }
        internal TenantHarness Tenant { get; } = new()
        {
            TenantId = "tenant", DisplayName = "Tenant", Agent = new() { EmailEnabled = true }
        };
        internal TenantUsersSnapshot Snapshot { get; set; } = new() { TenantId = "tenant" };
        internal PersonalEmailState PersonalState { get; set; } = new() { TenantId = "tenant" };
        internal Dictionary<Guid, DirectoryPerson> Profiles { get; } = [];
        internal Mock<IEnterpriseDirectory> Directory { get; } = new();
        internal Mock<IPersonalEmailConnections> Personal { get; } = new();
        internal TenantUserService Users { get; }
        internal AssignedEmailAuthorization Service { get; }

        internal Fixture()
        {
            Options = new EmailChannelOptions
            {
                Enabled = true, Provider = EmailMailboxProvider.OutlookCom,
                AuthorizationMode = EmailAuthorizationMode.ApprovedUsers,
                TenantId = Actor.DirectoryTenantId, ApplicationTenantId = "tenant",
                MailboxAddress = "vault-test@outlook.com", PublicOrigin = "https://docintel.example",
                Personal = new() { ClientId = Guid.NewGuid(), ClientSecret = "isolated-fixture-credential" }
            };
            Identity = new IdentityOptions { DirectoryTenantId = Actor.DirectoryTenantId };
            Profiles[Actor.ObjectId] = Profile(Actor, WorkAddress);
            Directory.SetupGet(value => value.IsConfigured).Returns(true);
            Directory.SetupGet(value => value.DirectoryTenantId).Returns(Actor.DirectoryTenantId);
            Directory.Setup(value => value.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => Profiles.GetValueOrDefault(id));
            Directory.Setup(value => value.FindByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string address, CancellationToken _) => Profiles.Values.SingleOrDefault(person =>
                    String.Equals(person.Email, address, StringComparison.OrdinalIgnoreCase)));
            var membership = new Mock<ITenantUserStore>();
            membership.Setup(value => value.GetAsync("tenant", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Snapshot);
            var configuration = new Mock<IOptionsMonitor<IdentityOptions>>();
            configuration.SetupGet(value => value.CurrentValue).Returns(Identity);
            var harness = new Mock<IHarnessStore>();
            harness.Setup(value => value.GetTenantAsync("tenant", It.IsAny<CancellationToken>())).ReturnsAsync(Tenant);
            Users = new TenantUserService(membership.Object, configuration.Object, Directory.Object,
                harness.Object, NullLogger<TenantUserService>.Instance);
            var personalStore = new Mock<IPersonalEmailStateStore>();
            personalStore.Setup(value => value.GetAsync("tenant", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => PersonalState);
            Personal.Setup(value => value.OwnsSenderAsync(It.IsAny<ActorIdentity>(),
                PersonalAddress, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Service = new AssignedEmailAuthorization(Options, Users, harness.Object,
                NullLogger<AssignedEmailAuthorization>.Instance, personalStore.Object, Personal.Object);
        }

        internal DirectoryPerson Profile(ActorIdentity actor, string address) =>
            new(actor.ObjectId, "Fixture user", address, address, null, null, null, true, true);

        internal void Assign(ActorIdentity actor, TenantUserRole role = TenantUserRole.User,
            TenantUserStatus status = TenantUserStatus.Active) =>
            Snapshot = Snapshot with
            {
                Users = Snapshot.Users.Where(user => user.ObjectId != actor.ObjectId).Append(
                    new TenantUserAssignment(actor.DirectoryTenantId, actor.ObjectId, role, status)).ToList()
            };

        internal void Link(ActorIdentity actor) => PersonalState = PersonalState with
        {
            Senders = [new PersonalEmailCredential
            {
                BindingId = Guid.NewGuid(), Actor = actor, HomeAccountId = "fixture-home",
                Subject = "fixture-subject", GraphId = "fixture-graph", Addresses = [PersonalAddress],
                TokenCache = Encoding.UTF8.GetBytes("fixture-private-cache"), VerifiedAtUtc = DateTimeOffset.UtcNow
            }]
        };
    }
}
