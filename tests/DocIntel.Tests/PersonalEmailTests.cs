using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Azure.Core;
using DocIntel.Api.Channels;
using DocIntel.Api.Identity;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class PersonalEmailTests
{
    [Fact]
    public void Native_Microsoft_User_Caches_Support_Protected_Serialization_Without_A_Server_Login()
    {
        // Arrange
        var application = ConfidentialClientApplicationBuilder.Create(Guid.NewGuid().ToString("D"))
            .WithClientSecret("isolated-fixture-credential")
            .WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.PersonalMicrosoftAccount)
            .Build();
        var serializer = Assert.IsAssignableFrom<ITokenCacheSerializer>(application.UserTokenCache);

        // Act
        var bytes = serializer.SerializeMsalV3();
        serializer.DeserializeMsalV3(bytes);

        // Assert
        Assert.NotEmpty(bytes);
    }
    [Fact]
    public async Task Custom_Code_Redemption_Requires_The_Browser_Nonce_Not_Just_A_Token_Nonce()
    {
        // Arrange
        var options = new OpenIdConnectOptions { ClientId = Guid.NewGuid().ToString("D") };
        var nonce = options.ProtocolValidator.GenerateNonce();
        var jwt = new JwtSecurityToken(PersonalEmailOAuthClient.Issuer, options.ClientId,
            [new Claim(JwtRegisteredClaimNames.Sub, "validated-fixture-subject"),
             new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                 ClaimValueTypes.Integer64),
             new Claim("nonce", nonce)],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10));
        var response = new OpenIdConnectMessage
        {
            IdToken = new JwtSecurityTokenHandler().WriteToken(jwt), AccessToken = "fixture-access-token"
        };
        var context = new TokenValidatedContext(new DefaultHttpContext(),
            new AuthenticationScheme("personal", null, typeof(OpenIdConnectHandler)), options,
            new ClaimsPrincipal(new ClaimsIdentity("personal")), new AuthenticationProperties())
        {
            SecurityToken = jwt,
            TokenEndpointResponse = response
        };

        // Act / Assert
        await Assert.ThrowsAsync<OpenIdConnectProtocolInvalidNonceException>(() =>
            PersonalEmailOAuthClient.ValidateTokenResponseAsync(context));
        context.Nonce = options.ProtocolValidator.GenerateNonce();
        await Assert.ThrowsAsync<OpenIdConnectProtocolInvalidNonceException>(() =>
            PersonalEmailOAuthClient.ValidateTokenResponseAsync(context));
        context.Nonce = nonce;
        await PersonalEmailOAuthClient.ValidateTokenResponseAsync(context);
        context.TokenEndpointResponse = null;
        await Assert.ThrowsAsync<OpenIdConnectProtocolException>(() =>
            PersonalEmailOAuthClient.ValidateTokenResponseAsync(context));
    }
    [Fact]
    public void Personal_Mail_Requires_Delegated_Consent_And_Never_Allows_Trusted_Internal_Mode()
    {
        // Arrange
        var fixture = new Fixture();

        // Act / Assert
        fixture.Options.Validate();
        Assert.Equal(Guid.Empty, fixture.Options.MailboxObjectId);
        Assert.Equal(["User.Read"], PersonalEmailOAuthClient.Scopes(PersonalEmailPurpose.Sender));
        Assert.Equal(["User.Read", "Mail.Read", "Mail.Send"],
            PersonalEmailOAuthClient.Scopes(PersonalEmailPurpose.Mailbox));
        fixture.Options.AuthorizationMode = EmailAuthorizationMode.TrustedInternal;
        fixture.Options.CorporateSenderProof = new String('x', 43);
        Assert.Throws<InvalidOperationException>(fixture.Options.Validate);
        fixture.Options.AuthorizationMode = EmailAuthorizationMode.ConfirmSender;
        fixture.Options.Personal.ClientSecret = String.Empty;
        Assert.Throws<InvalidOperationException>(fixture.Options.Validate);
    }

    [Fact]
    public async Task A_Reader_Can_Verify_Their_Own_Sender_But_Cannot_Connect_The_Workspace_Mailbox()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Access.Setup(access => access.ResolveAsync(fixture.Actor, "tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentAccessScope(TenantRole.Reader, null));

        // Act
        var request = await fixture.Connections.BeginAsync(
            fixture.Actor, "tenant", PersonalEmailPurpose.Sender, null, CancellationToken.None);

        // Assert
        Assert.True(PersonalEmailState.ValidRequest(request));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Connections.BeginAsync(
            fixture.Actor, "tenant", PersonalEmailPurpose.Mailbox, null, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Connections.BeginAsync(
            fixture.Actor, "other-workspace", PersonalEmailPurpose.Sender, null, CancellationToken.None));
    }

    [Fact]
    public async Task Application_Identities_And_Outside_Directory_Users_Cannot_Create_Personal_Connections()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Identity.Memberships.Add(new TenantMembership
        {
            DirectoryTenantId = fixture.Actor.DirectoryTenantId,
            ObjectId = fixture.Actor.ObjectId,
            ApplicationTenantId = "tenant",
            ServicePrincipal = true
        });
        var outside = new ActorIdentity(Guid.NewGuid(), Guid.NewGuid());

        // Act / Assert
        foreach (var actor in new[] { fixture.Actor, outside })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Connections.BeginAsync(
                actor, "tenant", PersonalEmailPurpose.Sender, null, CancellationToken.None));
        Assert.Empty((await fixture.Store.GetAsync("tenant", CancellationToken.None)).Pending);
    }

    [Fact]
    public async Task Callback_Is_One_Use_And_Bound_To_The_Original_DocIntel_Actor()
    {
        // Arrange
        var fixture = new Fixture();
        var request = await fixture.Connections.BeginAsync(
            fixture.Actor, "tenant", PersonalEmailPurpose.Mailbox, null, CancellationToken.None);
        var other = new ActorIdentity(fixture.Actor.DirectoryTenantId, Guid.NewGuid());

        // Act / Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Connections.CompleteAsync(
            other, request, "subject", fixture.MailboxToken, CancellationToken.None));
        Assert.Null((await fixture.Store.GetAsync("tenant", CancellationToken.None)).Mailbox);
        await fixture.Connections.CompleteAsync(
            fixture.Actor, request, "mailbox-subject", fixture.MailboxToken, CancellationToken.None);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Connections.CompleteAsync(
            fixture.Actor, request, "mailbox-subject", fixture.MailboxToken, CancellationToken.None));
        Assert.Empty((await fixture.Store.GetAsync("tenant", CancellationToken.None)).Pending);
    }

    [Fact]
    public async Task Connecting_Another_Account_Does_Not_Replace_The_Configured_Mailbox()
    {
        // Arrange
        var fixture = new Fixture();
        var request = await fixture.Connections.BeginAsync(
            fixture.Actor, "tenant", PersonalEmailPurpose.Mailbox, null, CancellationToken.None);

        // Act / Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Connections.CompleteAsync(
            fixture.Actor, request, "sender-subject", fixture.SenderToken, CancellationToken.None));
        Assert.Null((await fixture.Store.GetAsync("tenant", CancellationToken.None)).Mailbox);
    }

    [Fact]
    public async Task Membership_Revocation_During_Consent_Prevents_Saving_The_Connection()
    {
        // Arrange
        var fixture = new Fixture();
        var request = await fixture.Connections.BeginAsync(
            fixture.Actor, "tenant", PersonalEmailPurpose.Mailbox, null, CancellationToken.None);
        fixture.Access.Setup(access => access.ResolveAsync(fixture.Actor, "tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentAccessScope.Denied);

        // Act / Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Connections.CompleteAsync(
            fixture.Actor, request, "subject", fixture.MailboxToken, CancellationToken.None));
        Assert.Null((await fixture.Store.GetAsync("tenant", CancellationToken.None)).Mailbox);
        fixture.OAuth.Verify(client => client.GetProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Disconnect_Invalidates_Pending_Consent_As_Well_As_Stored_Tokens()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.ConnectAsync(PersonalEmailPurpose.Mailbox);
        var request = await fixture.Connections.BeginAsync(
            fixture.Actor, "tenant", PersonalEmailPurpose.Mailbox, null, CancellationToken.None);

        // Act
        await fixture.Connections.DisconnectAsync(
            fixture.Actor, "tenant", PersonalEmailPurpose.Mailbox, CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Connections.CompleteAsync(
            fixture.Actor, request, "subject", fixture.MailboxToken, CancellationToken.None));
        await Assert.ThrowsAsync<PersonalEmailConnectionRequiredException>(() =>
            fixture.Connections.GetMailboxAsync(CancellationToken.None));
        Assert.Empty((await fixture.Store.GetAsync("tenant", CancellationToken.None)).Pending);
    }

    [Fact]
    public async Task Expired_Connection_Requests_And_Arbitrary_Return_Urls_Are_Rejected()
    {
        // Arrange
        var fixture = new Fixture();
        var request = await fixture.Connections.BeginAsync(
            fixture.Actor, "tenant", PersonalEmailPurpose.Sender, "mail-" + new String('A', 64), CancellationToken.None);
        var state = await fixture.Store.GetAsync("tenant", CancellationToken.None);
        await fixture.Store.SaveAsync(state with
        {
            Version = state.Version + 1,
            Pending = state.Pending.Select(item => item with
                { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }).ToList()
        }, state.Version, CancellationToken.None);

        // Act / Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Connections.CompleteAsync(
            fixture.Actor, request, "subject", fixture.SenderToken, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Connections.BeginAsync(
            fixture.Actor, "tenant", PersonalEmailPurpose.Sender, "https://untrusted.example", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Connections.BeginAsync(
            fixture.Actor, "tenant", PersonalEmailPurpose.Mailbox, "mail-" + new String('A', 64), CancellationToken.None));
    }

    [Fact]
    public async Task Personal_Sender_Ownership_Cannot_Be_Linked_To_Two_DocIntel_Actors()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.ConnectAsync(PersonalEmailPurpose.Sender);
        var other = new ActorIdentity(fixture.Actor.DirectoryTenantId, Guid.NewGuid());
        var request = await fixture.Connections.BeginAsync(
            other, "tenant", PersonalEmailPurpose.Sender, null, CancellationToken.None);

        // Act / Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Connections.CompleteAsync(
            other, request, "subject", fixture.SenderToken, CancellationToken.None));
        Assert.True(await fixture.Connections.OwnsSenderAsync(
            fixture.Actor, "sender-test@hotmail.com", CancellationToken.None));
        Assert.Null(await fixture.Connections.OwnsSenderAsync(
            other, "sender-test@hotmail.com", CancellationToken.None));
    }

    [Fact]
    public async Task Refresh_Rotates_Protected_Tokens_Without_Changing_The_Mailbox_Binding()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.ConnectAsync(PersonalEmailPurpose.Mailbox);
        var mailbox = await fixture.Connections.GetMailboxAsync(CancellationToken.None);
        var profile = mailbox.Profile(fixture.Options.ConfiguredProfile);
        var refreshed = fixture.MailboxToken with { TokenCache = Encoding.UTF8.GetBytes("rotated-fixture-cache") };
        fixture.OAuth.Setup(client => client.AcquireAsync(
            It.IsAny<PersonalEmailCredential>(), PersonalEmailPurpose.Mailbox, It.IsAny<CancellationToken>()))
            .ReturnsAsync(refreshed);

        // Act
        await fixture.Connections.AcquireAsync(mailbox, PersonalEmailPurpose.Mailbox, CancellationToken.None);
        var current = await fixture.Connections.GetMailboxAsync(CancellationToken.None);

        // Assert
        Assert.Equal(refreshed.TokenCache, current.TokenCache);
        Assert.Equal(profile, current.Profile(fixture.Options.ConfiguredProfile));
    }

    [Fact]
    public async Task Revoked_Microsoft_Consent_Is_Visible_And_Blocks_Further_Use()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.ConnectAsync(PersonalEmailPurpose.Mailbox);
        var mailbox = await fixture.Connections.GetMailboxAsync(CancellationToken.None);
        fixture.OAuth.Setup(client => client.AcquireAsync(
            It.IsAny<PersonalEmailCredential>(), PersonalEmailPurpose.Mailbox, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MsalUiRequiredException("invalid_grant", "fixture consent revoked"));

        // Act / Assert
        await Assert.ThrowsAsync<PersonalEmailConnectionRequiredException>(() =>
            fixture.Connections.AcquireAsync(mailbox, PersonalEmailPurpose.Mailbox, CancellationToken.None));
        var status = await fixture.Connections.GetStatusAsync(
            fixture.Actor, "tenant", PersonalEmailPurpose.Mailbox, CancellationToken.None);
        Assert.False(status.Connected);
        Assert.Equal("ReconnectionRequired", status.State);
        await Assert.ThrowsAsync<PersonalEmailConnectionRequiredException>(() =>
            fixture.Connections.GetMailboxAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Changed_Personal_Address_Or_Revoked_Workspace_Access_Cannot_Keep_Sender_Ownership()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.ConnectAsync(PersonalEmailPurpose.Sender);
        fixture.OAuth.Setup(client => client.GetProfileAsync(
            fixture.SenderToken.AccessToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalEmailProfile("2222222222222222", ["changed-test@hotmail.com"]));

        // Act / Assert
        await Assert.ThrowsAsync<PersonalEmailConnectionRequiredException>(() => fixture.Connections.OwnsSenderAsync(
            fixture.Actor, "sender-test@hotmail.com", CancellationToken.None));
        fixture.Access.Setup(access => access.ResolveAsync(fixture.Actor, "tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentAccessScope.Denied);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Connections.OwnsSenderAsync(
            fixture.Actor, "sender-test@hotmail.com", CancellationToken.None));
    }

    [Fact]
    public async Task File_Tokens_Are_Encrypted_Workspace_Bound_And_Survive_A_New_Store_Instance()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "DocIntel.PersonalMail." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var fixture = new Fixture();
            await fixture.ConnectAsync(PersonalEmailPurpose.Mailbox);
            var initial = await fixture.Store.GetAsync("tenant", CancellationToken.None);
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys")));
            var root = Path.Combine(directory, "state");
            var store = new FilePersonalEmailStateStore(root, protection);

            // Act
            await store.SaveAsync(initial with { Version = 1 }, 0, CancellationToken.None);
            var restart = new FilePersonalEmailStateStore(root,
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys"))));
            var restored = await restart.GetAsync("tenant", CancellationToken.None);

            // Assert
            Assert.Equal(initial.Mailbox!.TokenCache, restored.Mailbox!.TokenCache);
            Assert.Equal(initial.Mailbox.BindingId, restored.Mailbox.BindingId);
            var file = Assert.Single(Directory.GetFiles(root, "*.protected"));
            var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
            Assert.DoesNotContain("vault-test@outlook.com", raw);
            Assert.DoesNotContain("mailbox-fixture-cache", raw);
            var other = Path.Combine(root, PersonalEmailSerialization.Key("other") + ".protected");
            File.Copy(file, other);
            await Assert.ThrowsAsync<CryptographicException>(() => restart.GetAsync("other", CancellationToken.None));
            await Assert.ThrowsAsync<PersonalEmailStateConflictException>(() =>
                store.SaveAsync(initial with { Version = 1 }, 0, CancellationToken.None));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Delegated_Mail_Uses_Me_Immutable_Ids_And_Rejects_Foreign_Mailbox_Cursors()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.ConnectAsync(PersonalEmailPurpose.Mailbox);
        var handler = new MailHandler();
        var client = fixture.MailboxClient(handler);

        // Act
        var changes = await client.ReadChangesAsync(null, CancellationToken.None);

        // Assert
        Assert.StartsWith("https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages/delta", handler.Urls[0]);
        Assert.Contains("IdType=\"ImmutableId\"", handler.Preferences[0]);
        Assert.Equal(await client.GetConnectionProfileAsync(fixture.Options.ConfiguredProfile, CancellationToken.None),
            Assert.Single(changes.Messages).ConnectionProfile);
        foreach (var cursor in new[]
        {
            "https://untrusted.example/v1.0/me/mailFolders/inbox/messages/delta",
            "https://graph.microsoft.com/v1.0/users/another-account/mailFolders/inbox/messages/delta"
        })
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                client.ReadChangesAsync(cursor, CancellationToken.None));
        Assert.Equal(2, handler.Urls.Count);
    }

    [Fact]
    public async Task Personal_Mail_Still_Requires_Explicit_Confirmation_And_Rechecks_Reply_Ownership()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.ConnectAsync(PersonalEmailPurpose.Mailbox);
        await fixture.ConnectAsync(PersonalEmailPurpose.Sender);
        var handler = new MailHandler();
        var mailbox = fixture.MailboxClient(handler);
        var runs = new InMemoryAskAiRunStore();
        var queue = new Mock<IAskAiQueue>();
        var receiver = new EmailQuestionReceiver(fixture.Options, runs, fixture.Harness.Object, mailbox,
            NullLogger<EmailQuestionReceiver>.Instance);
        var confirmation = new EmailQuestionConfirmation(
            fixture.Options, mailbox, fixture.Access.Object, runs, queue.Object, fixture.Harness.Object);
        var message = Assert.Single((await mailbox.ReadChangesAsync(null, CancellationToken.None)).Messages);

        // Act
        await receiver.ReceiveAsync(message);
        await receiver.ReceiveAsync(message);
        var run = Assert.Single(await runs.ListRunsByTenantAsync("tenant"));
        Assert.Equal(JobStatus.AwaitingConfirmation, run.Status);
        Assert.Null(run.Actor);
        await confirmation.ReadAsync(fixture.Actor, "tenant", run.RunId, CancellationToken.None);
        queue.Verify(value => value.TryEnqueueAsync(It.IsAny<AskAiQueueTicket>(),
            It.IsAny<CancellationToken>()), Times.Never);
        var confirmed = await confirmation.ConfirmAsync(fixture.Actor, "tenant", run.RunId, CancellationToken.None);
        await confirmation.ConfirmAsync(fixture.Actor, "tenant", run.RunId, CancellationToken.None);
        confirmed.Answer = "PRIVATE_DOCUMENT_ANSWER";
        await fixture.Connections.DisconnectAsync(
            fixture.Actor, "tenant", PersonalEmailPurpose.Sender, CancellationToken.None);

        // Assert
        queue.Verify(value => value.TryEnqueueAsync(It.IsAny<AskAiQueueTicket>(),
            It.IsAny<CancellationToken>()), Times.Once);
        var challenge = Assert.Single(handler.SentBodies);
        Assert.DoesNotContain(message.Text, challenge);
        Assert.DoesNotContain("PRIVATE_DOCUMENT_ANSWER", challenge);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new EmailAnswerTransport(fixture.Options, mailbox).SendAsync(confirmed, confirmed.ChannelDelivery!));
        Assert.Single(handler.SentBodies);
    }

    [Fact]
    public async Task A_Personal_Answer_Uses_The_Confirmed_Immutable_Original_Message_And_Native_Reply()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.ConnectAsync(PersonalEmailPurpose.Mailbox);
        await fixture.ConnectAsync(PersonalEmailPurpose.Sender);
        var handler = new MailHandler();
        var mailbox = fixture.MailboxClient(handler);
        var runs = new InMemoryAskAiRunStore();
        var receiver = new EmailQuestionReceiver(fixture.Options, runs, fixture.Harness.Object, mailbox,
            NullLogger<EmailQuestionReceiver>.Instance);
        var confirmation = new EmailQuestionConfirmation(fixture.Options, mailbox, fixture.Access.Object, runs,
            Mock.Of<IAskAiQueue>(), fixture.Harness.Object);
        var message = Assert.Single((await mailbox.ReadChangesAsync(null, CancellationToken.None)).Messages);
        await receiver.ReceiveAsync(message);
        var pending = Assert.Single(await runs.ListRunsByTenantAsync("tenant"));
        var run = await confirmation.ConfirmAsync(fixture.Actor, "tenant", pending.RunId, CancellationToken.None);
        run.Answer = "CITED_DOCUMENT_ANSWER";

        // Act
        await new EmailAnswerTransport(fixture.Options, mailbox).SendAsync(run, run.ChannelDelivery!);

        // Assert
        Assert.EndsWith("/v1.0/me/messages/immutable-message/reply", handler.Urls[^1]);
        Assert.Contains("IdType=\"ImmutableId\"", handler.Preferences[^1]);
        Assert.Equal(2, handler.SentBodies.Count);
        using var reply = JsonDocument.Parse(handler.SentBodies[^1]);
        var content = reply.RootElement.GetProperty("message");
        Assert.Contains("CITED_DOCUMENT_ANSWER", content.GetProperty("body").GetProperty("content").GetString());
        Assert.Equal("sender-test@hotmail.com",
            content.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal(1, content.GetProperty("toRecipients").GetArrayLength());
        Assert.Empty(content.GetProperty("ccRecipients").EnumerateArray());
        Assert.Empty(content.GetProperty("bccRecipients").EnumerateArray());
        Assert.False(content.TryGetProperty("subject", out _));
    }

    [Fact]
    public async Task Approved_Personal_Users_Are_Queued_And_Replied_To_Without_Any_Confirmation_Email()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.ConnectAsync(PersonalEmailPurpose.Mailbox);
        await fixture.ConnectAsync(PersonalEmailPurpose.Sender);
        var originalProfile = fixture.Options.ConfiguredProfile;
        fixture.Options.AuthorizationMode = EmailAuthorizationMode.ApprovedUsers;
        fixture.Options.Validate();
        fixture.Identity.Memberships.Add(new()
        {
            DirectoryTenantId = fixture.Actor.DirectoryTenantId, ObjectId = fixture.Actor.ObjectId,
            ApplicationTenantId = "tenant", Role = TenantRole.Admin
        });
        fixture.Harness.Setup(value => value.GetTenantAsync("tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantHarness
                { TenantId = "tenant", DisplayName = "Tenant", Agent = new() { EmailEnabled = true } });
        var directory = new Mock<IEnterpriseDirectory>();
        var membership = new Mock<ITenantUserStore>();
        membership.Setup(value => value.GetAsync("tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantUsersSnapshot { TenantId = "tenant" });
        var identity = new Mock<IOptionsMonitor<IdentityOptions>>();
        identity.SetupGet(value => value.CurrentValue).Returns(fixture.Identity);
        var users = new TenantUserService(membership.Object, identity.Object, directory.Object,
            fixture.Harness.Object, NullLogger<TenantUserService>.Instance);
        var assigned = new AssignedEmailAuthorization(fixture.Options, users, fixture.Harness.Object,
            NullLogger<AssignedEmailAuthorization>.Instance, fixture.Store, fixture.Connections);
        var handler = new MailHandler();
        var mailbox = fixture.MailboxClient(handler);
        var runs = new InMemoryAskAiRunStore();
        var queue = new Mock<IAskAiQueue>();
        var receiver = new EmailQuestionReceiver(fixture.Options, runs, fixture.Harness.Object, mailbox,
            NullLogger<EmailQuestionReceiver>.Instance, queue: queue.Object, assignedAuthorization: assigned);
        var message = Assert.Single((await mailbox.ReadChangesAsync(null, CancellationToken.None)).Messages);

        // Act
        await receiver.ReceiveAsync(message);
        await receiver.ReceiveAsync(message);
        var run = Assert.Single((await runs.ListRunsByTenantAsync("tenant")).Where(item => item.PendingEmail is not null));
        Assert.Empty(handler.SentBodies);
        run.Answer = "AUTOMATIC_CITED_ANSWER";
        await new EmailAnswerTransport(fixture.Options, mailbox, assigned).SendAsync(run, run.ChannelDelivery!);

        // Assert
        Assert.Equal(originalProfile, fixture.Options.ConfiguredProfile);
        Assert.Equal(JobStatus.Queued, run.Status);
        Assert.Equal(fixture.Actor, run.Actor);
        Assert.Equal(EmailAuthorizationMode.ApprovedUsers, run.PendingEmail!.AuthorizationMode);
        Assert.NotNull(run.PendingEmail.AssignedAtUtc);
        Assert.Null(run.PendingEmail.ConfirmedAtUtc);
        Assert.Null(run.PendingEmail.AuthenticatedAtUtc);
        Assert.Equal(["User.Read"], PersonalEmailOAuthClient.Scopes(PersonalEmailPurpose.Sender));
        Assert.EndsWith("/v1.0/me/messages/immutable-message/reply", handler.Urls[^1]);
        using var reply = JsonDocument.Parse(Assert.Single(handler.SentBodies));
        var content = reply.RootElement.GetProperty("message");
        var text = content.GetProperty("body").GetProperty("content").GetString();
        Assert.Contains("AUTOMATIC_CITED_ANSWER", text);
        Assert.Contains(message.Text.Trim(), text);
        Assert.Contains("If you did not send the question", text);
        Assert.Single(content.GetProperty("toRecipients").EnumerateArray());
        Assert.Empty(content.GetProperty("ccRecipients").EnumerateArray());
        Assert.Empty(content.GetProperty("bccRecipients").EnumerateArray());
        queue.Verify(value => value.TryEnqueueAsync(It.IsAny<AskAiQueueTicket>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Disconnect_During_Original_Message_Verification_Prevents_The_Reply_Post()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.ConnectAsync(PersonalEmailPurpose.Mailbox);
        var handler = new MailHandler
        {
            BeforeResponse = () => fixture.Connections.DisconnectAsync(
                fixture.Actor, "tenant", PersonalEmailPurpose.Mailbox, CancellationToken.None)
        };
        var mailbox = fixture.MailboxClient(handler);
        var profile = await mailbox.GetConnectionProfileAsync(fixture.Options.ConfiguredProfile, CancellationToken.None);

        // Act / Assert
        await Assert.ThrowsAsync<PersonalEmailConnectionRequiredException>(() => mailbox.ReplyForProfileAsync(
            profile, "immutable-message", "sender-test@hotmail.com", "PRIVATE_ANSWER", CancellationToken.None));
        Assert.Single(handler.Urls);
        Assert.Empty(handler.SentBodies);
    }

    [Fact]
    public async Task Replacing_The_Mailbox_Invalidates_An_Old_Message_And_Confirmation()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.ConnectAsync(PersonalEmailPurpose.Mailbox);
        var mailbox = fixture.MailboxClient(new MailHandler());
        var old = Assert.Single((await mailbox.ReadChangesAsync(null, CancellationToken.None)).Messages);
        var runs = new InMemoryAskAiRunStore();
        var receiver = new EmailQuestionReceiver(fixture.Options, runs, fixture.Harness.Object, mailbox,
            NullLogger<EmailQuestionReceiver>.Instance);
        await receiver.ReceiveAsync(old);
        var run = Assert.Single(await runs.ListRunsByTenantAsync("tenant"));

        // Act
        await fixture.ConnectAsync(PersonalEmailPurpose.Mailbox);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.ReceiveAsync(old));
        var confirmation = new EmailQuestionConfirmation(
            fixture.Options, mailbox, fixture.Access.Object, runs, Mock.Of<IAskAiQueue>(), fixture.Harness.Object);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            confirmation.ReadAsync(fixture.Actor, "tenant", run.RunId, CancellationToken.None));
    }

    [Fact]
    public async Task A_Selected_Checkpoint_Cannot_Read_Through_A_Replacement_Connection()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.ConnectAsync(PersonalEmailPurpose.Mailbox);
        var handler = new MailHandler();
        var mailbox = fixture.MailboxClient(handler);
        var selected = await mailbox.GetConnectionProfileAsync(fixture.Options.ConfiguredProfile, CancellationToken.None);
        var epoch = await mailbox.GetAdmissionStartAsync(selected, CancellationToken.None);
        var stored = await fixture.Connections.GetMailboxAsync(CancellationToken.None);
        Assert.Equal(stored.VerifiedAtUtc, epoch);

        // Act
        await fixture.ConnectAsync(PersonalEmailPurpose.Mailbox);

        // Assert
        await Assert.ThrowsAsync<PersonalEmailConnectionRequiredException>(() => mailbox.ReadForProfileAsync(
            selected, "https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages/delta?$deltatoken=old",
            CancellationToken.None, epoch));
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task An_Empty_Delta_Page_Cannot_Advance_A_Changed_Connection_Generation()
    {
        // Arrange
        var fixture = new Fixture();
        await fixture.ConnectAsync(PersonalEmailPurpose.Mailbox);
        var handler = new MailHandler
        {
            EmptyPage = true,
            BeforeResponse = () => fixture.ConnectAsync(PersonalEmailPurpose.Mailbox)
        };
        var mailbox = fixture.MailboxClient(handler);
        var profile = await mailbox.GetConnectionProfileAsync(fixture.Options.ConfiguredProfile, CancellationToken.None);

        // Act / Assert
        await Assert.ThrowsAsync<PersonalEmailConnectionRequiredException>(() =>
            mailbox.ReadForProfileAsync(profile, null, CancellationToken.None));
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task The_First_Personal_Checkpoint_Admits_Mail_From_Consent_Time_Not_First_Poll_Time()
    {
        // Arrange
        var fixture = new Fixture();
        var epoch = DateTimeOffset.UtcNow.AddMinutes(-1);
        var profile = new String('A', 64);
        var mailbox = new Mock<IConnectedMailboxClient>();
        mailbox.Setup(value => value.GetConnectionProfileAsync(
            fixture.Options.ConfiguredProfile, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        mailbox.Setup(value => value.GetAdmissionStartAsync(profile, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset?)epoch);
        var observed = new TaskCompletionSource<DateTimeOffset?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        mailbox.Setup(value => value.ReadForProfileAsync(profile, It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<DateTimeOffset?>()))
            .Callback<string, string?, CancellationToken, DateTimeOffset?>((_, _, _, after) => observed.TrySetResult(after))
            .ReturnsAsync(new MailboxChanges([], null, "fixture-delta"));
        var runs = new InMemoryAskAiRunStore();
        var receiver = new EmailQuestionReceiver(fixture.Options, runs, fixture.Harness.Object, mailbox.Object,
            NullLogger<EmailQuestionReceiver>.Instance);
        using var worker = new MailboxQuestionWorker(fixture.Options, mailbox.Object, receiver, runs,
            fixture.Harness.Object, NullLogger<MailboxQuestionWorker>.Instance);

        // Act
        await worker.StartAsync(CancellationToken.None);
        var after = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal(epoch, after);
        Assert.Equal(epoch, Assert.Single(await runs.ListRunsByTenantAsync("tenant")).CreatedAtUtc);
    }

    private sealed class Fixture
    {
        internal ActorIdentity Actor { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        internal EmailChannelOptions Options { get; }
        internal IdentityOptions Identity { get; }
        internal MemoryStore Store { get; } = new();
        internal Mock<IPersonalEmailOAuthClient> OAuth { get; } = new();
        internal Mock<ITenantAccessResolver> Access { get; } = new();
        internal Mock<IHarnessStore> Harness { get; } = new();
        internal PersonalEmailConnections Connections { get; }
        internal PersonalEmailToken MailboxToken { get; } = new("mailbox-home", "mailbox-fixture-access",
            DateTimeOffset.UtcNow.AddHours(1), Encoding.UTF8.GetBytes("mailbox-fixture-cache"));
        internal PersonalEmailToken SenderToken { get; } = new("sender-home", "sender-fixture-access",
            DateTimeOffset.UtcNow.AddHours(1), Encoding.UTF8.GetBytes("sender-fixture-cache"));

        internal Fixture()
        {
            Options = new EmailChannelOptions
            {
                Enabled = true, Provider = EmailMailboxProvider.OutlookCom, TenantId = Actor.DirectoryTenantId,
                ApplicationTenantId = "tenant", MailboxAddress = "vault-test@outlook.com",
                PublicOrigin = "https://docintel.example",
                Personal = new() { ClientId = Guid.NewGuid(), ClientSecret = "isolated-fixture-credential" }
            };
            Identity = new IdentityOptions { DirectoryTenantId = Actor.DirectoryTenantId };
            Access.Setup(value => value.ResolveAsync(It.IsAny<ActorIdentity>(), "tenant", It.IsAny<CancellationToken>()))
                .ReturnsAsync(DocumentAccessScope.Unrestricted);
            Harness.Setup(value => value.GetTenantAsync("tenant", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TenantHarness
                    { TenantId = "tenant", DisplayName = "Fixture workspace",
                      Agent = new() { EmailEnabled = true, EmailConfirmationRequired = true } });
            foreach (var (token, graphId, address, purpose) in new[]
            {
                (MailboxToken, "1111111111111111", "vault-test@outlook.com", PersonalEmailPurpose.Mailbox),
                (SenderToken, "2222222222222222", "sender-test@hotmail.com", PersonalEmailPurpose.Sender)
            })
            {
                OAuth.Setup(value => value.GetProfileAsync(token.AccessToken, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PersonalEmailProfile(graphId, [address]));
                OAuth.Setup(value => value.AcquireAsync(It.IsAny<PersonalEmailCredential>(),
                    purpose, It.IsAny<CancellationToken>())).ReturnsAsync(token);
            }
            Connections = new PersonalEmailConnections(Options, Identity, Store, OAuth.Object,
                Access.Object, Harness.Object, NullLogger<PersonalEmailConnections>.Instance);
        }

        internal async Task ConnectAsync(PersonalEmailPurpose purpose)
        {
            var request = await Connections.BeginAsync(Actor, "tenant", purpose, null, CancellationToken.None);
            await Connections.CompleteAsync(Actor, request, purpose + "-subject",
                purpose == PersonalEmailPurpose.Mailbox ? MailboxToken : SenderToken, CancellationToken.None);
        }

        internal ConnectedPersonalMailboxClient MailboxClient(MailHandler handler)
        {
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(value => value.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(handler, disposeHandler: false));
            return new ConnectedPersonalMailboxClient(Options, Connections,
                new GraphMailboxClient(factory.Object, new FixtureCredential(), Options),
                factory.Object, NullLogger<GraphMailboxClient>.Instance);
        }
    }

    private sealed class MemoryStore : IPersonalEmailStateStore
    {
        private PersonalEmailState state = new() { TenantId = "tenant" };
        public Task<PersonalEmailState> GetAsync(string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(JsonSerializer.Deserialize<PersonalEmailState>(JsonSerializer.Serialize(state))!);
        public Task SaveAsync(PersonalEmailState next, long expectedVersion, CancellationToken cancellationToken)
        {
            next.Validate("tenant");
            if (state.Version != expectedVersion || next.Version != expectedVersion + 1)
                throw new PersonalEmailStateConflictException();
            state = JsonSerializer.Deserialize<PersonalEmailState>(JsonSerializer.Serialize(next))!;
            return Task.CompletedTask;
        }
    }

    private sealed class FixtureCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("corporate-fixture-access", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class MailHandler : HttpMessageHandler
    {
        internal List<string> Urls { get; } = [];
        internal List<string> Preferences { get; } = [];
        internal List<string> SentBodies { get; } = [];
        internal bool EmptyPage { get; init; }
        internal Func<Task>? BeforeResponse { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            Preferences.Add(request.Headers.TryGetValues("Prefer", out var prefer) ? String.Join(", ", prefer) : String.Empty);
            if (BeforeResponse is not null) await BeforeResponse();
            if (request.Method == HttpMethod.Post)
            {
                SentBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            if (!request.RequestUri.AbsolutePath.StartsWith("/v1.0/me/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            var body = request.RequestUri.AbsolutePath.EndsWith("/messages/delta", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["value"] = EmptyPage ? Array.Empty<object>() :
                        [new { id = "immutable-message", receivedDateTime = DateTimeOffset.UtcNow }],
                    ["@odata.deltaLink"] = "https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages/delta?$deltatoken=fixture"
                })
                : JsonSerializer.Serialize(new
                {
                    id = "immutable-message", subject = "Question", receivedDateTime = DateTimeOffset.UtcNow,
                    from = new { emailAddress = new { address = "sender-test@hotmail.com" } },
                    body = new { contentType = "text", content = "What is the agreement notice period?" },
                    internetMessageHeaders = Array.Empty<object>()
                });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
