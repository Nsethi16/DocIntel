using DocIntel.Api.Identity;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DocIntel.Core.Authorization;
using DocIntel.Core.Harness;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class TenantUserTests
{
    [Fact]
    public async Task Shared_Access_Uses_A_Private_Blob_Prefix_And_The_Exact_Current_ETag()
    {
        // Arrange
        var container = new Mock<BlobContainerClient>();
        var blob = new Mock<BlobClient>();
        container.Setup(c => c.GetBlobClient(It.Is<string>(name => name.StartsWith("tenant-access/v1/"))))
            .Returns(blob.Object);
        var current = new TenantUsersSnapshot { TenantId = "tenant", Version = 2 };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(current, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var tag = new ETag("\"version-two\"");
        blob.Setup(b => b.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response.FromValue(BlobsModelFactory.BlobDownloadStreamingResult(new MemoryStream(bytes),
                BlobsModelFactory.BlobDownloadDetails(contentLength: bytes.Length, eTag: tag)), Mock.Of<Response>()));
        BlobUploadOptions? written = null;
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .Callback<BinaryData, BlobUploadOptions, CancellationToken>((_, options, _) => written = options)
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());
        var store = new BlobTenantUserStore(container.Object);

        // Act
        await store.SaveAsync(current with { Version = 3 }, 2);

        // Assert
        Assert.Equal(tag, written!.Conditions.IfMatch);
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(412, "Concurrent edit"));
        await Assert.ThrowsAsync<TenantUsersConflictException>(() => store.SaveAsync(current with { Version = 3 }, 2));
    }

    [Fact]
    public async Task New_Users_Read_All_Tenant_Documents_But_Cannot_Administer_And_Revocation_Is_Immediate()
    {
        // Arrange
        using var fixture = new Fixture();
        var changes = new[] { new TenantUserChange(fixture.User.ObjectId, TenantUserRole.User, TenantUserStatus.Active) };

        // Act
        await fixture.Service.ChangeAsync("tenant", fixture.Admin, false, 0, changes, CancellationToken.None);
        var scope = await fixture.Service.GetAccessAsync(fixture.User, "tenant", CancellationToken.None);
        await fixture.Service.ChangeAsync("tenant", fixture.Admin, false, 1,
            [changes[0] with { Status = TenantUserStatus.Disabled }], CancellationToken.None);
        var revoked = await fixture.Service.GetAccessAsync(fixture.User, "tenant", CancellationToken.None);

        // Assert
        Assert.True(scope.CanReadDocument("any-document"));
        Assert.False(scope.CanAdmin);
        Assert.False(scope.CanProcess);
        Assert.False(revoked.CanRead);
        Assert.False((await fixture.Service.GetAccessAsync(fixture.User, "other", CancellationToken.None)).CanRead);
        Assert.Equal(2, (await fixture.Store.GetAsync("tenant")).Audit.Count);
    }

    [Fact]
    public async Task The_Last_Active_Admin_Cannot_Be_Disabled_Removed_Or_Demoted_And_Failed_Edits_Do_Not_Persist()
    {
        // Arrange
        using var fixture = new Fixture();

        // Act / Assert
        foreach (var change in new[]
        {
            new TenantUserChange(fixture.Admin.ObjectId, TenantUserRole.User, TenantUserStatus.Active),
            new TenantUserChange(fixture.Admin.ObjectId, TenantUserRole.TenantAdmin, TenantUserStatus.Disabled),
            new TenantUserChange(fixture.Admin.ObjectId, TenantUserRole.TenantAdmin, TenantUserStatus.Removed)
        })
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ChangeAsync(
                "tenant", fixture.Admin, false, 0, [change], CancellationToken.None));
        Assert.Equal(0, (await fixture.Store.GetAsync("tenant")).Version);
    }

    [Fact]
    public async Task Stale_Concurrent_Access_Edits_Conflict_And_A_New_Admin_Can_Revoke_An_Operator_Bootstrap_Admin()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.Service.ChangeAsync("tenant", fixture.Admin, false, 0,
            [new(fixture.User.ObjectId, TenantUserRole.TenantAdmin, TenantUserStatus.Active)], CancellationToken.None);

        // Act
        await Assert.ThrowsAsync<TenantUsersConflictException>(() => fixture.Service.ChangeAsync(
            "tenant", fixture.Admin, false, 0,
            [new(fixture.User.ObjectId, TenantUserRole.User, TenantUserStatus.Active)], CancellationToken.None));
        await fixture.Service.ChangeAsync("tenant", fixture.User, false, 1,
            [new(fixture.Admin.ObjectId, TenantUserRole.TenantAdmin, TenantUserStatus.Removed)], CancellationToken.None);

        // Assert
        Assert.False((await fixture.Service.GetAccessAsync(fixture.Admin, "tenant", CancellationToken.None)).CanRead);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ChangeAsync(
            "tenant", fixture.Admin, false, 2,
            [new(fixture.Admin.ObjectId, TenantUserRole.TenantAdmin, TenantUserStatus.Active)], CancellationToken.None));
        Assert.Equal(2, (await fixture.Store.GetAsync("tenant")).Version);
    }

    [Fact]
    public async Task Directory_Disablement_Or_Ambiguous_Address_Ownership_Does_Not_Authorize_Email()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.Service.ChangeAsync("tenant", fixture.Admin, false, 0,
            [new(fixture.User.ObjectId, TenantUserRole.User, TenantUserStatus.Active)], CancellationToken.None);
        fixture.Directory.Setup(d => d.FindByEmailAsync("user@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Person(fixture.User.ObjectId));

        // Act
        var accepted = await fixture.Service.ResolveAsync("tenant", "user@example.com", CancellationToken.None);
        fixture.Directory.Setup(d => d.GetAsync(fixture.User.ObjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Person(fixture.User.ObjectId) with { AccountEnabled = false });
        var denied = await fixture.Service.ResolveAsync("tenant", "user@example.com", CancellationToken.None);

        // Assert
        Assert.Equal(fixture.User, accepted!.Actor);
        Assert.Equal("user@example.com", accepted.Address);
        Assert.Null(denied);
        Assert.Null(await fixture.Service.ResolveAsync("other", "user@example.com", CancellationToken.None));
    }

    [Fact]
    public async Task Existing_Restricted_Operator_Grants_Are_Not_Broadened_By_The_Tenant_Wide_User_Model()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Options.Memberships.Add(new()
        {
            DirectoryTenantId = fixture.User.DirectoryTenantId,
            ObjectId = fixture.User.ObjectId,
            ApplicationTenantId = "tenant",
            Role = TenantRole.Reader,
            AllDocuments = false,
            DocumentIds = ["allowed"]
        });

        // Act / Assert
        var scope = await fixture.Service.GetAccessAsync(fixture.User, "tenant", CancellationToken.None);
        Assert.True(scope.CanReadDocument("allowed"));
        Assert.False(scope.CanReadDocument("other"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ChangeAsync("tenant", fixture.Admin, false, 0,
            [new(fixture.User.ObjectId, TenantUserRole.TenantAdmin, TenantUserStatus.Active)], CancellationToken.None));
    }

    [Fact]
    public async Task Separate_File_Store_Instances_Keep_Audits_And_Reject_Stale_Writes_Across_Restarts()
    {
        // Arrange
        using var fixture = new Fixture();
        var original = await fixture.Store.GetAsync("tenant");
        var changed = original with
        {
            Version = 1,
            Users = [new(fixture.User.DirectoryTenantId, fixture.User.ObjectId, TenantUserRole.User, TenantUserStatus.Active)],
            Audit = [new(1, DateTimeOffset.UtcNow, fixture.Admin, false,
                [new(fixture.User.ObjectId, TenantUserRole.User, TenantUserStatus.Active)])]
        };
        var second = new FileTenantUserStore(fixture.Path);

        // Act
        await fixture.Store.SaveAsync(changed, 0);
        await Assert.ThrowsAsync<TenantUsersConflictException>(() => second.SaveAsync(changed, 0));
        var restored = await second.GetAsync("tenant");

        // Assert
        Assert.Equal(1, restored.Version);
        Assert.Equal(fixture.Admin, Assert.Single(restored.Audit).Actor);
        Assert.Empty((await second.GetAsync("other")).Users);
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DocIntel.Users-" + Guid.NewGuid());
        public ActorIdentity Admin { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        public ActorIdentity User { get; }
        public IdentityOptions Options { get; }
        public Mock<IEnterpriseDirectory> Directory { get; } = new();
        public FileTenantUserStore Store { get; }
        public TenantUserService Service { get; }
        public Fixture()
        {
            User = new(Admin.DirectoryTenantId, Guid.NewGuid());
            Options = new()
            {
                Mode = IdentityMode.Entra,
                DirectoryTenantId = Admin.DirectoryTenantId,
                Memberships = [new()
                {
                    DirectoryTenantId = Admin.DirectoryTenantId, ObjectId = Admin.ObjectId,
                    ApplicationTenantId = "tenant", Role = TenantRole.Admin, AllDocuments = true
                }]
            };
            Store = new(Path);
            var monitor = new Mock<IOptionsMonitor<IdentityOptions>>();
            monitor.SetupGet(m => m.CurrentValue).Returns(Options);
            Directory.SetupGet(d => d.IsConfigured).Returns(true);
            Directory.SetupGet(d => d.DirectoryTenantId).Returns(Admin.DirectoryTenantId);
            Directory.Setup(d => d.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => Person(id));
            var harness = new Mock<IHarnessStore>();
            harness.Setup(h => h.GetTenantAsync("tenant", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TenantHarness { TenantId = "tenant", DisplayName = "Tenant" });
            Service = new(Store, monitor.Object, Directory.Object, harness.Object, NullLogger<TenantUserService>.Instance);
        }
        public DirectoryPerson Person(Guid id) => new(id, "Directory Person", "user@example.com",
            "user@example.com", "Analyst", "Operations", "Office", true, true);
        public void Dispose() { if (System.IO.Directory.Exists(Path)) System.IO.Directory.Delete(Path, recursive: true); }
    }
}
