using System.Net;
using System.Text.Json;
using DocIntel.Core.Harness;
using DocIntel.Core.Storage;
using Microsoft.Azure.Cosmos;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class HarnessConcurrencyTests
{
    [Fact]
    public async Task Independent_Local_Stores_Reject_Stale_Drafts_And_Observe_The_Saved_Version()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "DocIntel.Harness.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "harness.json");
            var first = new FileHarnessStore(path);
            var second = new FileHarnessStore(path);
            var a = (await first.GetTenantAsync("contoso-legal"))!;
            var b = (await second.GetTenantAsync("contoso-legal"))!;
            a.DisplayName = "First administrator";
            b.DisplayName = "Stale administrator";

            // Act
            var saved = await first.UpsertTenantAsync(a);

            // Assert
            await Assert.ThrowsAsync<HarnessConflictException>(() => second.UpsertTenantAsync(b));
            Assert.Equal(1, a.HarnessVersion);
            Assert.Equal(2, saved.HarnessVersion);
            Assert.Equal("First administrator", (await second.GetTenantAsync("contoso-legal"))!.DisplayName);
            Assert.Equal(2, (await second.GetTenantAsync("contoso-legal"))!.HarnessVersion);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Concurrent_Local_Saves_Publish_Exactly_One_Complete_Snapshot()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "DocIntel.Harness.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "harness.json");
            var stores = Enumerable.Range(0, 8).Select(_ => new FileHarnessStore(path)).ToArray();
            var drafts = await Task.WhenAll(stores.Select(store => store.GetTenantAsync("contoso-legal")));

            // Act
            var results = await Task.WhenAll(stores.Select(async (store, index) =>
            {
                drafts[index]!.DisplayName = "Admin " + index;
                return await Record.ExceptionAsync(() => store.UpsertTenantAsync(drafts[index]!));
            }));

            // Assert
            Assert.Equal(1, results.Count(error => error is null));
            Assert.Equal(7, results.Count(error => error is HarnessConflictException));
            Assert.Equal(2, (await stores[0].GetTenantAsync("contoso-legal"))!.HarnessVersion);
            using var snapshot = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(2, snapshot.RootElement.GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Version_Zero_Creates_Without_Overwriting_Existing_Tenants()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "DocIntel.Harness.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileHarnessStore(Path.Combine(directory, "harness.json"));
            var draft = new TenantHarness { TenantId = "new", DisplayName = "New", HarnessVersion = 0 };

            // Act
            var created = await store.UpsertTenantAsync(draft);

            // Assert
            Assert.Equal(1, created.HarnessVersion);
            Assert.Equal(0, draft.HarnessVersion);
            await Assert.ThrowsAsync<HarnessConflictException>(() => store.UpsertTenantAsync(draft));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cosmos_Replacements_Are_Fenced_By_The_Read_ETag_And_Expected_Public_Version()
    {
        // Arrange
        var fixture = new CosmosFixture();
        var draft = new TenantHarness { TenantId = "tenant", DisplayName = "Changed", HarnessVersion = 3 };

        // Act
        var saved = await fixture.Store.UpsertTenantAsync(draft);

        // Assert
        Assert.Equal("etag-3", fixture.Fence);
        Assert.Equal(4, saved.HarnessVersion);
        Assert.Equal(3, draft.HarnessVersion);
        Assert.Equal("Changed", fixture.Persisted!.DisplayName);
        await Assert.ThrowsAsync<HarnessConflictException>(() => fixture.Store.UpsertTenantAsync(draft));
        Assert.Equal(1, fixture.Writes);
    }

    [Fact]
    public async Task A_Cosmos_Race_Is_A_Conflict_Not_An_Automatic_Overwrite_Retry()
    {
        // Arrange
        var fixture = new CosmosFixture { Race = true };

        // Act / Assert
        await Assert.ThrowsAsync<HarnessConflictException>(() => fixture.Store.UpsertTenantAsync(
            new TenantHarness { TenantId = "tenant", DisplayName = "Stale draft", HarnessVersion = 3 }));
        Assert.Equal(1, fixture.Writes);
        Assert.Equal("Original", fixture.Persisted!.DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    public async Task Missing_Or_Wildcard_Cosmos_Versions_Never_Write(string? version)
    {
        // Arrange
        var fixture = new CosmosFixture { Version = version };

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.UpsertTenantAsync(
            new TenantHarness { TenantId = "tenant", DisplayName = "Changed", HarnessVersion = 3 }));
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public async Task Duplicate_Cosmos_Creation_Is_Conditional_And_Never_Uses_Upsert()
    {
        // Arrange
        var fixture = new CosmosFixture { Persisted = null };
        var draft = new TenantHarness { TenantId = "tenant", DisplayName = "New", HarnessVersion = 0 };

        // Act
        var created = await fixture.Store.UpsertTenantAsync(draft);

        // Assert
        Assert.Equal(1, created.HarnessVersion);
        await Assert.ThrowsAsync<HarnessConflictException>(() => fixture.Store.UpsertTenantAsync(draft));
        Assert.Equal(1, fixture.Writes);
        fixture.Container.Verify(c => c.UpsertItemAsync(It.IsAny<TenantHarness>(), It.IsAny<PartitionKey?>(),
            It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class CosmosFixture
    {
        public Mock<Container> Container { get; } = new();
        public TenantHarness? Persisted { get; set; } =
            new() { TenantId = "tenant", DisplayName = "Original", HarnessVersion = 3 };
        public string? Version { get; set; } = "etag-3";
        public bool Race { get; set; }
        public string? Fence { get; private set; }
        public int Writes { get; private set; }
        public CosmosHarnessStore Store { get; }

        public CosmosFixture()
        {
            Container.Setup(c => c.ReadItemAsync<TenantHarness>("tenant", new PartitionKey("tenant"),
                    It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                .Returns(() => Persisted is null
                    ? Task.FromException<ItemResponse<TenantHarness>>(
                        new CosmosException("Missing", HttpStatusCode.NotFound, 0, "", 0))
                    : Task.FromResult(Response(Persisted)));
            Container.Setup(c => c.ReplaceItemAsync(It.IsAny<TenantHarness>(), "tenant", new PartitionKey("tenant"),
                    It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                .Returns<TenantHarness, string, PartitionKey, ItemRequestOptions, CancellationToken>((value, _, _, options, _) =>
                {
                    Writes++;
                    Fence = options.IfMatchEtag;
                    if (Race) return Task.FromException<ItemResponse<TenantHarness>>(
                        new CosmosException("Race", HttpStatusCode.PreconditionFailed, 0, "", 0));
                    Persisted = Clone(value);
                    Version = "etag-" + value.HarnessVersion;
                    return Task.FromResult(Response(value));
                });
            Container.Setup(c => c.CreateItemAsync(It.IsAny<TenantHarness>(), new PartitionKey("tenant"),
                    It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                .Returns<TenantHarness, PartitionKey, ItemRequestOptions, CancellationToken>((value, _, _, _) =>
                {
                    Writes++;
                    Persisted = Clone(value);
                    return Task.FromResult(Response(value));
                });
            var client = new Mock<CosmosClient>();
            client.SetupGet(c => c.Endpoint).Returns(new Uri("https://example.documents.azure.com"));
            client.Setup(c => c.GetContainer("DocIntel", "harnesses")).Returns(Container.Object);
            Store = new CosmosHarnessStore(client.Object);
        }

        private ItemResponse<TenantHarness> Response(TenantHarness harness)
        {
            var response = new Mock<ItemResponse<TenantHarness>>();
            response.SetupGet(r => r.Resource).Returns(Clone(harness));
            response.SetupGet(r => r.ETag).Returns(Version ?? String.Empty);
            return response.Object;
        }

        private static TenantHarness Clone(TenantHarness harness) =>
            JsonSerializer.Deserialize<TenantHarness>(JsonSerializer.Serialize(harness))!;
    }
}
