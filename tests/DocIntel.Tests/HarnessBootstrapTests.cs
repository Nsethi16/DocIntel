using System.Net;
using System.Text.Json;
using DocIntel.Core;
using DocIntel.Core.Harness;
using DocIntel.Core.Storage;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class HarnessBootstrapTests
{
    [Theory]
    [InlineData(false, null, null)]
    [InlineData(true, null, null)]
    [InlineData(false, "", "")]
    [InlineData(true, "", "")]
    [InlineData(false, " ", " ")]
    [InlineData(true, " ", " ")]
    public async Task Empty_Bootstrap_Settings_Keep_The_Sample_Workspaces(
        bool cosmos, string? tenantId, string? displayName)
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos);
        var store = fixture.Store(new HarnessBootstrapOptions { TenantId = tenantId, DisplayName = displayName });

        // Act
        var tenants = await store.ListTenantsAsync();

        // Assert
        Assert.Equal(["contoso-legal", "fabrikam-procurement"], tenants.Select(tenant => tenant.TenantId).Order());
        Assert.All(tenants, tenant =>
        {
            Assert.Equal(1, tenant.HarnessVersion);
            Assert.Equal("decision-1", tenant.DecisionModelId);
            Assert.Equal("gpt-5-mini", tenant.DefaultModelId);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_New_Installation_Creates_Only_The_Configured_Customer_With_Supported_Defaults(bool cosmos)
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos);
        var store = fixture.Store(Customer());
        var defaults = new TenantHarness { TenantId = "template", DisplayName = "Template" };

        // Act
        var tenant = Assert.Single(await store.ListTenantsAsync());

        // Assert
        Assert.Equal("woodgrove", tenant.TenantId);
        Assert.Equal(tenant.TenantId, tenant.Id);
        Assert.Equal("Woodgrove Legal", tenant.DisplayName);
        Assert.Equal(1, tenant.HarnessVersion);
        Assert.True(tenant.IsActive);
        Assert.Equal("decision-1", tenant.DecisionModelId);
        Assert.Equal("gpt-5-mini", tenant.DefaultModelId);
        Assert.Equal(["decision-1", "gpt-5-mini"], tenant.AllowedModels);
        Assert.Equal(defaults.Capabilities.Keys.Order(), tenant.Capabilities.Keys.Order());
        Assert.Equal("decision-1", tenant.Capabilities[FeatureHarness.IdentifyDocumentsKey].ModelIdOverride);
        Assert.Equal("gpt-5-mini", tenant.Capabilities[FeatureHarness.DocumentInsightKey].ModelIdOverride);
        Assert.NotEmpty(tenant.Capabilities[FeatureHarness.PromptColumnsKey].PromptColumns!.Columns);
        Assert.True(tenant.Agent.IsValid);
        Assert.False(tenant.Agent.EmailEnabled);
        Assert.False(tenant.Agent.TeamsEnabled);
        Assert.False(tenant.ReviewPolicy.Enabled);
        Assert.Null(await store.GetTenantAsync("contoso-legal"));
        Assert.Null(await store.GetTenantAsync("fabrikam-procurement"));
        var restarted = (await fixture.Store(Customer()).GetTenantAsync("woodgrove"))!;
        Assert.Equal(tenant.AllowedModels, restarted.AllowedModels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_Empty_Persisted_Snapshot_Is_Initialized_With_Only_The_Customer_Workspace(bool cosmos)
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos);
        await fixture.PersistAsync();

        // Act
        var tenants = await fixture.Store(Customer()).ListTenantsAsync();

        // Assert
        Assert.Equal("woodgrove", Assert.Single(tenants).TenantId);
        Assert.Single(await fixture.PersistedAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_First_Write_Cannot_Overwrite_The_Bootstrap_Workspace_With_A_Version_Zero_Draft(bool cosmos)
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos);
        var store = fixture.Store(Customer());
        var draft = new TenantHarness
        {
            TenantId = "woodgrove", DisplayName = "Competing name", HarnessVersion = 0
        };

        // Act
        var error = await Record.ExceptionAsync(() => store.UpsertTenantAsync(draft));

        // Assert
        Assert.IsType<HarnessConflictException>(error);
        Assert.Equal("Woodgrove Legal", Assert.Single(await fixture.PersistedAsync()).DisplayName);
        fixture.VerifyNoUnconditionalWrites();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restarting_With_Changed_Bootstrap_Settings_Never_Resets_An_Existing_Workspace(bool cosmos)
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos);
        var existing = CustomizedTenant("woodgrove");
        await fixture.PersistAsync(existing);
        var before = await fixture.SnapshotAsync();
        var options = Customer() with { DisplayName = "Replacement name" };

        // Act
        var first = await fixture.Store(options).GetTenantAsync("woodgrove");
        var restarted = await fixture.Store(options).ListTenantsAsync();

        // Assert
        AssertPreserved(existing, first!);
        AssertPreserved(existing, Assert.Single(restarted));
        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.Equal(0, fixture.CreateAttempts);
    }

    [Theory]
    [InlineData(false, "", "phi-4")]
    [InlineData(false, "decision-1", "phi-4")]
    [InlineData(false, "gpt-4o", "gpt-4o")]
    [InlineData(true, "", "")]
    [InlineData(true, "decision-1", "decision-1")]
    [InlineData(true, "gpt-4o", "gpt-4o")]
    public async Task File_Model_Migrations_Only_Run_When_Bootstrap_Is_Unconfigured(
        bool configured, string storedDefault, string expectedDefault)
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos: false);
        var existing = CustomizedTenant("woodgrove");
        existing.DefaultModelId = storedDefault;
        existing.DecisionModelId = "";
        existing.AllowedModels = ["custom-model"];
        await fixture.PersistAsync(existing);
        var before = await fixture.SnapshotAsync();
        var options = configured ? Customer() : new HarnessBootstrapOptions { TenantId = " ", DisplayName = " " };
        var store = fixture.Store(options);

        // Act
        var tenant = (await store.GetTenantAsync(existing.TenantId))!;
        var listed = Assert.Single(await store.ListTenantsAsync());

        // Assert
        Assert.Equal(expectedDefault, tenant.DefaultModelId);
        Assert.Equal(expectedDefault, listed.DefaultModelId);
        Assert.Equal(existing.DisplayName, tenant.DisplayName);
        Assert.Equal(existing.HarnessVersion, tenant.HarnessVersion);
        Assert.Equal(existing.IsActive, tenant.IsActive);
        Assert.Equal(existing.UpdatedAtUtc, tenant.UpdatedAtUtc);
        if (configured)
        {
            AssertPreserved(existing, tenant);
            AssertPreserved(existing, listed);
        }
        else
        {
            Assert.Equal(["decision-1", "mai-reasoning", "phi-4", "custom-model"], tenant.AllowedModels);
            Assert.Equal("decision-1", tenant.DecisionModelId);
            var identification = tenant.Capabilities[FeatureHarness.IdentifyDocumentsKey];
            Assert.Equal("decision-1", identification.ModelIdOverride);
            Assert.Equal("decision-1", identification.IdentifyDocuments!.DecisionModelId);
        }
        Assert.Equal(before, await fixture.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_Writes_Only_Persist_Legacy_Model_Migrations_When_Bootstrap_Is_Unconfigured(bool configured)
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos: false);
        var existing = CustomizedTenant("woodgrove");
        existing.DefaultModelId = "decision-1";
        existing.DecisionModelId = "";
        existing.AllowedModels = ["custom-model"];
        await fixture.PersistAsync(existing);
        var store = fixture.Store(configured ? Customer() : null);
        var another = new TenantHarness
        {
            TenantId = "another-workspace", DisplayName = "Another workspace", HarnessVersion = 0
        };

        // Act
        await store.UpsertTenantAsync(another);
        var persisted = (await fixture.PersistedAsync()).Single(tenant => tenant.TenantId == existing.TenantId);

        // Assert
        if (configured)
        {
            Assert.Equal(JsonSerializer.Serialize(existing), JsonSerializer.Serialize(persisted));
        }
        else
        {
            Assert.Equal("phi-4", persisted.DefaultModelId);
            Assert.Equal("decision-1", persisted.DecisionModelId);
            Assert.Equal(["decision-1", "mai-reasoning", "phi-4", "custom-model"], persisted.AllowedModels);
            Assert.Equal("decision-1", persisted.Capabilities[FeatureHarness.IdentifyDocumentsKey].ModelIdOverride);
        }
        Assert.Equal(existing.DisplayName, persisted.DisplayName);
        Assert.Equal(existing.HarnessVersion, persisted.HarnessVersion);
        Assert.Equal(existing.IsActive, persisted.IsActive);
        Assert.Equal(existing.UpdatedAtUtc, persisted.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Missing_Configured_Workspace_Is_Created_Without_Changing_Other_Persisted_Workspaces(bool cosmos)
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos);
        var existing = CustomizedTenant("another-customer");
        existing.Capabilities.Clear();
        await fixture.PersistAsync(existing);
        var before = JsonSerializer.Serialize(Assert.Single(await fixture.PersistedAsync()));

        // Act
        var tenants = await fixture.Store(Customer()).ListTenantsAsync();

        // Assert
        Assert.Equal(2, tenants.Count);
        AssertPreserved(existing, tenants.Single(tenant => tenant.TenantId == existing.TenantId));
        Assert.Equal("Woodgrove Legal", tenants.Single(tenant => tenant.TenantId == "woodgrove").DisplayName);
        Assert.DoesNotContain(tenants, tenant => tenant.TenantId is "contoso-legal" or "fabrikam-procurement");
        var persisted = (await fixture.PersistedAsync()).Single(tenant => tenant.TenantId == existing.TenantId);
        Assert.Equal(before, JsonSerializer.Serialize(persisted));
        Assert.Empty(persisted.Capabilities);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_Sample_Initialization_Completes_Without_Resetting_The_First_Workspace(bool cosmos)
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos);
        var existing = LegacyCompatibleTenant("contoso-legal");
        await fixture.PersistAsync(existing);

        // Act
        var tenants = await fixture.Store().ListTenantsAsync();

        // Assert
        Assert.Equal(2, tenants.Count);
        AssertPreserved(existing, tenants.Single(tenant => tenant.TenantId == existing.TenantId));
        Assert.Equal("Fabrikam Procurement Ops",
            tenants.Single(tenant => tenant.TenantId == "fabrikam-procurement").DisplayName);
        var persisted = (await fixture.PersistedAsync()).Single(tenant => tenant.TenantId == existing.TenantId);
        Assert.Equal(JsonSerializer.Serialize(existing), JsonSerializer.Serialize(persisted));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_Settings_Do_Not_Inject_Samples_Into_An_Existing_Customer_Installation(bool cosmos)
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos);
        var existing = LegacyCompatibleTenant("another-customer");
        await fixture.PersistAsync(existing);
        var before = await fixture.SnapshotAsync();

        // Act
        var tenants = await fixture.Store().ListTenantsAsync();

        // Assert
        AssertPreserved(existing, Assert.Single(tenants));
        Assert.Equal(before, await fixture.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task All_Persisted_Workspaces_Are_Considered_Before_Repairing_A_Partial_Sample_Initialization(
        bool cosmos)
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos);
        await fixture.PersistAsync(CustomizedTenant("contoso-legal"), CustomizedTenant("another-customer"));
        var before = await fixture.SnapshotAsync();

        // Act
        var tenants = await fixture.Store().ListTenantsAsync();

        // Assert
        Assert.Equal(2, tenants.Count);
        Assert.DoesNotContain(tenants, tenant => tenant.TenantId == "fabrikam-procurement");
        Assert.Equal(before, await fixture.SnapshotAsync());
    }

    [Fact]
    public async Task A_Damaged_Local_Snapshot_Is_Not_Replaced_With_Bootstrap_Defaults()
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos: false);
        const string damaged = """[{"tenantId":"woodgrove","displayName":""";
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.FilePath)!);
        await File.WriteAllTextAsync(fixture.FilePath, damaged);

        // Act
        var error = await Record.ExceptionAsync(() => fixture.Store(Customer()).ListTenantsAsync());

        // Assert
        Assert.IsAssignableFrom<JsonException>(error);
        Assert.Equal(damaged, await File.ReadAllTextAsync(fixture.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_Replicas_Create_One_Workspace_And_Do_Not_Reset_The_Winning_Configuration(bool cosmos)
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos);
        fixture.PauseCosmosCreates();
        var stores = Enumerable.Range(0, 8).Select(_ => fixture.Store(Customer())).ToArray();

        // Act
        var reads = stores.Select(store => store.ListTenantsAsync()).ToArray();
        fixture.ReleaseCosmosCreates();
        var snapshots = await Task.WhenAll(reads);
        var edited = CustomizedTenant("woodgrove");
        await fixture.PersistAsync(edited);
        var afterRestart = await fixture.Store(Customer()).ListTenantsAsync();

        // Assert
        Assert.All(snapshots, tenants => Assert.Equal("woodgrove", Assert.Single(tenants).TenantId));
        AssertPreserved(edited, Assert.Single(afterRestart));
        AssertPreserved(edited, Assert.Single(await stores[0].ListTenantsAsync()));
        Assert.Single(await fixture.PersistedAsync());
        if (cosmos)
        {
            Assert.Equal(8, fixture.CreateAttempts);
            Assert.Equal(7, fixture.CreationConflicts);
        }
        fixture.VerifyNoUnconditionalWrites();
    }

    [Fact]
    public async Task Concurrent_Reads_On_One_Cosmos_Store_Run_Initialization_Once()
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos: true);
        fixture.PauseCosmosCreates();
        var store = fixture.Store(Customer());

        // Act
        var reads = Enumerable.Range(0, 8).Select(_ => store.ListTenantsAsync()).ToArray();
        fixture.ReleaseCosmosCreates();
        await Task.WhenAll(reads);

        // Assert
        Assert.Equal(1, fixture.CreateAttempts);
        Assert.Single(await fixture.PersistedAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Failed_Cosmos_Initialization_Is_Retried_Without_Overwriting_An_Already_Created_Workspace(
        HttpStatusCode failureStatus)
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos: true)
        {
            FailTenantId = "fabrikam-procurement",
            FailureStatus = failureStatus
        };
        var store = fixture.Store();

        // Act
        var exception = await Assert.ThrowsAsync<CosmosException>(() => store.ListTenantsAsync());
        var partial = Assert.Single(await fixture.PersistedAsync());
        var edited = CustomizedTenant(partial.TenantId);
        await fixture.PersistAsync(edited);
        var recovered = await store.ListTenantsAsync();

        // Assert
        Assert.Equal(failureStatus, exception.StatusCode);
        Assert.Equal("contoso-legal", partial.TenantId);
        Assert.Equal(2, recovered.Count);
        AssertPreserved(edited, recovered.Single(tenant => tenant.TenantId == partial.TenantId));
        Assert.Equal(3, fixture.CreateAttempts);
        fixture.VerifyNoUnconditionalWrites();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canceled_Initialization_Can_Be_Retried_Without_Publishing_Partial_Customer_State(bool cosmos)
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos);
        var store = fixture.Store(Customer());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ListTenantsAsync(cancellation.Token));
        var beforeRetry = await fixture.PersistedAsync();
        var tenants = await store.ListTenantsAsync();

        // Assert
        Assert.Empty(beforeRetry);
        Assert.Equal("woodgrove", Assert.Single(tenants).TenantId);
    }

    [Theory]
    [InlineData(null, "Customer")]
    [InlineData("customer", null)]
    [InlineData("customer", " ")]
    [InlineData("Customer", "Customer")]
    [InlineData("../customer", "Customer")]
    [InlineData("customer/name", "Customer")]
    [InlineData(" customer", "Customer")]
    [InlineData("customer ", "Customer")]
    [InlineData("-customer", "Customer")]
    [InlineData("customer-", "Customer")]
    [InlineData("customer--one", "Customer")]
    [InlineData("cüstomer", "Customer")]
    [InlineData("customer", "Customer\nName")]
    [InlineData("customer", "Customer\0Name")]
    public void Invalid_Configuration_Is_Rejected_Before_Either_Store_Accesses_Persistence(
        string? tenantId, string? displayName)
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos: false);
        var client = new Mock<CosmosClient>(MockBehavior.Strict);
        var options = new HarnessBootstrapOptions { TenantId = tenantId, DisplayName = displayName };

        // Act
        var fileError = Record.Exception(() => new FileHarnessStore(fixture.FilePath, options));
        var cosmosError = Record.Exception(() => new CosmosHarnessStore(client.Object, bootstrapOptions: options));

        // Assert
        Assert.IsType<InvalidOperationException>(fileError);
        Assert.IsType<InvalidOperationException>(cosmosError);
        Assert.False(File.Exists(fixture.FilePath));
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public void Bootstrap_Identity_Lengths_Are_Bounded()
    {
        // Arrange
        var maximum = new HarnessBootstrapOptions
        {
            TenantId = new string('a', 128),
            DisplayName = new string('N', 128)
        };
        var longSlug = maximum with { TenantId = new string('a', 129) };
        var longName = maximum with { DisplayName = new string('N', 129) };

        // Act
        var valid = Record.Exception(maximum.Validate);

        // Assert
        Assert.Null(valid);
        Assert.Throws<InvalidOperationException>(longSlug.Validate);
        Assert.Throws<InvalidOperationException>(longName.Validate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Configuration_Is_Bound_And_Passed_To_The_Selected_Persistence_Provider(bool cosmos)
    {
        // Arrange
        using var fixture = new StoreFixture(cosmos);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HarnessBootstrap:TenantId"] = "woodgrove",
            ["HarnessBootstrap:DisplayName"] = " Woodgrove Legal ",
            ["AzureAI:CosmosEndpoint"] = cosmos ? "https://example.documents.azure.com/" : null
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(fixture.Client);
        services.AddDocIntelCore(configuration, harnessFilePath: fixture.FilePath);
        using var provider = services.BuildServiceProvider();

        // Act
        var store = provider.GetRequiredService<IHarnessStore>();
        var tenants = await store.ListTenantsAsync();

        // Assert
        Assert.Equal(cosmos, store is CosmosHarnessStore);
        Assert.Equal(!cosmos, store is FileHarnessStore);
        Assert.Equal("woodgrove", provider.GetRequiredService<HarnessBootstrapOptions>().TenantId);
        Assert.Equal("Woodgrove Legal", Assert.Single(tenants).DisplayName);
    }

    [Fact]
    public void Invalid_Bound_Configuration_Fails_During_Service_Registration()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HarnessBootstrap:TenantId"] = "customer"
        }).Build();
        var services = new ServiceCollection();

        // Act
        var error = Record.Exception(() => services.AddDocIntelCore(configuration));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Separate_Installations_Do_Not_Share_Bootstrap_Settings_Or_Mutable_Default_Capabilities(
        bool cosmos)
    {
        // Arrange
        using var first = new StoreFixture(cosmos);
        using var second = new StoreFixture(cosmos);
        var firstStore = first.Store(Customer());
        var secondStore = second.Store(new HarnessBootstrapOptions
        {
            TenantId = "adatum", DisplayName = "Adatum"
        });

        // Act
        var tenant = Assert.Single(await firstStore.ListTenantsAsync());
        tenant.Capabilities[FeatureHarness.DocumentInsightKey].Enabled = false;
        var other = Assert.Single(await secondStore.ListTenantsAsync());
        var reloaded = (await firstStore.GetTenantAsync("woodgrove"))!;

        // Assert
        Assert.Equal("adatum", other.TenantId);
        Assert.Equal("Adatum", other.DisplayName);
        Assert.True(other.Capabilities[FeatureHarness.DocumentInsightKey].Enabled);
        Assert.Equal("woodgrove", reloaded.TenantId);
        Assert.True(reloaded.Capabilities[FeatureHarness.DocumentInsightKey].Enabled);
    }

    private static HarnessBootstrapOptions Customer() =>
        new() { TenantId = "woodgrove", DisplayName = "Woodgrove Legal" };

    private static TenantHarness CustomizedTenant(string tenantId)
    {
        var tenant = new TenantHarness
        {
            TenantId = tenantId,
            DisplayName = "Administrator's existing name",
            HarnessVersion = 9,
            IsActive = false,
            DeactivatedAtUtc = DateTimeOffset.Parse("2025-01-01T00:00:00Z"),
            DefaultModelId = "phi-4",
            DecisionModelId = "custom-decision",
            AllowedModels = ["phi-4"],
            Agent = new AgentProfile { DisplayName = "Existing agent", Instructions = "Keep this guidance." },
            UpdatedAtUtc = DateTimeOffset.Parse("2025-01-01T00:00:00Z")
        };
        tenant.Capabilities[FeatureHarness.IdentifyDocumentsKey].ModelIdOverride = "custom-feature-model";
        tenant.Capabilities[FeatureHarness.DocumentInsightKey].Enabled = false;
        return tenant;
    }

    private static TenantHarness LegacyCompatibleTenant(string tenantId)
    {
        var tenant = CustomizedTenant(tenantId);
        tenant.AllowedModels = ["decision-1", "mai-reasoning", "phi-4"];
        var identification = tenant.Capabilities[FeatureHarness.IdentifyDocumentsKey];
        identification.ModelIdOverride = tenant.DecisionModelId;
        identification.IdentifyDocuments!.DecisionModelId = tenant.DecisionModelId;
        return tenant;
    }

    private static void AssertPreserved(TenantHarness expected, TenantHarness actual)
    {
        Assert.Equal(expected.TenantId, actual.TenantId);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.HarnessVersion, actual.HarnessVersion);
        Assert.Equal(expected.IsActive, actual.IsActive);
        Assert.Equal(expected.DeactivatedAtUtc, actual.DeactivatedAtUtc);
        Assert.Equal(expected.UpdatedAtUtc, actual.UpdatedAtUtc);
        Assert.Equal(expected.DefaultModelId, actual.DefaultModelId);
        Assert.Equal(expected.DecisionModelId, actual.DecisionModelId);
        Assert.Equal(expected.AllowedModels, actual.AllowedModels);
        Assert.Equal(expected.Agent, actual.Agent);
        foreach (var (key, capability) in expected.Capabilities)
            Assert.Equal(JsonSerializer.Serialize(capability), JsonSerializer.Serialize(actual.Capabilities[key]));
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly bool _cosmos;
        private readonly string _directory = Path.GetFullPath(Path.Combine(
            ".harness-bootstrap-tests", Guid.NewGuid().ToString("N")));
        private readonly object _gate = new();
        private readonly Dictionary<string, TenantHarness> _persisted = new(StringComparer.Ordinal);
        private readonly Mock<Container> _container = new();
        private readonly Mock<CosmosClient> _client = new();
        private TaskCompletionSource? _releaseCreates;
        private int _createAttempts;
        private int _creationConflicts;

        public StoreFixture(bool cosmos)
        {
            _cosmos = cosmos;
            _client.SetupGet(client => client.Endpoint).Returns(new Uri("https://example.documents.azure.com/"));
            _client.Setup(client => client.GetContainer("DocIntel", "harnesses")).Returns(_container.Object);
            _container.Setup(container => container.GetItemQueryIterator<string>(
                    It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
                .Returns(() => Pages(Current().Select(tenant => tenant.TenantId).ToArray()));
            _container.Setup(container => container.GetItemQueryIterator<TenantHarness>(
                    It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
                .Returns(() => Pages(Current()));
            _container.Setup(container => container.ReadItemAsync<TenantHarness>(
                    It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, PartitionKey, ItemRequestOptions, CancellationToken>((id, _, _, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var tenant = Current().SingleOrDefault(item => item.TenantId == id);
                    return tenant is null
                        ? Task.FromException<ItemResponse<TenantHarness>>(Error(HttpStatusCode.NotFound))
                        : Task.FromResult(Response(tenant));
                });
            _container.Setup(container => container.CreateItemAsync(
                    It.IsAny<TenantHarness>(), It.IsAny<PartitionKey?>(), It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .Returns<TenantHarness, PartitionKey?, ItemRequestOptions, CancellationToken>(
                    (tenant, _, _, token) => CreateAsync(tenant, token));
        }

        public string FilePath => Path.Combine(_directory, "harness.json");
        public CosmosClient Client => _client.Object;
        public int CreateAttempts => _createAttempts;
        public int CreationConflicts => _creationConflicts;
        public string? FailTenantId { get; set; }
        public HttpStatusCode FailureStatus { get; set; }

        public IHarnessStore Store(HarnessBootstrapOptions? options = null) => _cosmos
            ? new CosmosHarnessStore(Client, bootstrapOptions: options)
            : new FileHarnessStore(FilePath, options);

        public async Task PersistAsync(params TenantHarness[] tenants)
        {
            if (_cosmos)
            {
                lock (_gate)
                    foreach (var tenant in tenants) _persisted[tenant.TenantId] = Clone(tenant);
            }
            else
            {
                Directory.CreateDirectory(_directory);
                await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(tenants));
            }
        }

        public async Task<IReadOnlyList<TenantHarness>> PersistedAsync()
        {
            if (_cosmos) return Current();
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<TenantHarness>>(
                await File.ReadAllTextAsync(FilePath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }

        public async Task<string> SnapshotAsync() => JsonSerializer.Serialize(
            (await PersistedAsync()).OrderBy(tenant => tenant.TenantId, StringComparer.Ordinal));

        public void PauseCosmosCreates()
        {
            if (_cosmos) _releaseCreates = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseCosmosCreates() => _releaseCreates?.SetResult();

        public void VerifyNoUnconditionalWrites()
        {
            _container.Verify(container => container.UpsertItemAsync(
                It.IsAny<TenantHarness>(), It.IsAny<PartitionKey?>(), It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Never);
            _container.Verify(container => container.ReplaceItemAsync(
                It.IsAny<TenantHarness>(), It.IsAny<string>(), It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        public void Dispose()
        {
            _releaseCreates?.TrySetCanceled();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }

        private TenantHarness[] Current()
        {
            lock (_gate) return _persisted.Values.Select(Clone).ToArray();
        }

        private async Task<ItemResponse<TenantHarness>> CreateAsync(
            TenantHarness tenant, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _createAttempts);
            if (_releaseCreates is not null) await _releaseCreates.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (tenant.TenantId == FailTenantId)
                {
                    FailTenantId = null;
                    throw Error(FailureStatus);
                }
                if (!_persisted.TryAdd(tenant.TenantId, Clone(tenant)))
                {
                    Interlocked.Increment(ref _creationConflicts);
                    throw Error(HttpStatusCode.Conflict);
                }
                return Response(tenant);
            }
        }

        private static FeedIterator<T> Pages<T>(T[] items)
        {
            var pages = items.Chunk(1).ToArray();
            var index = 0;
            var iterator = new Mock<FeedIterator<T>>();
            iterator.SetupGet(value => value.HasMoreResults).Returns(() => index < pages.Length);
            iterator.Setup(value => value.ReadNextAsync(It.IsAny<CancellationToken>()))
                .Returns<CancellationToken>(token =>
                {
                    token.ThrowIfCancellationRequested();
                    var page = pages[index++];
                    var response = new Mock<FeedResponse<T>>();
                    response.Setup(value => value.GetEnumerator())
                        .Returns(() => ((IEnumerable<T>)page).GetEnumerator());
                    return Task.FromResult(response.Object);
                });
            return iterator.Object;
        }

        private static CosmosException Error(HttpStatusCode status) => new("Test storage failure", status, 0, "", 0);

        private static ItemResponse<TenantHarness> Response(TenantHarness tenant)
        {
            var response = new Mock<ItemResponse<TenantHarness>>();
            response.SetupGet(value => value.Resource).Returns(Clone(tenant));
            return response.Object;
        }

        private static TenantHarness Clone(TenantHarness tenant) =>
            JsonSerializer.Deserialize<TenantHarness>(JsonSerializer.Serialize(tenant))!;
    }
}
