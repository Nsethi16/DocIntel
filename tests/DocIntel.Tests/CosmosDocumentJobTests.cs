using System.Net;
using System.Text.Json;
using DocIntel.Core.Jobs;
using DocIntel.Core.Storage;
using DocIntel.Core.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Azure.Cosmos;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class CosmosDocumentJobTests
{
    [Fact]
    public async Task Cosmos_Soft_Deletion_Is_Fenced_And_Cannot_Be_Resurrected_By_A_Stale_Save()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Persisted.Status = JobStatus.Completed;
        fixture.Persisted.PacketSummary = "Retained processing result";
        var previous = await fixture.Store.GetJobAsync("one", tenantId: "tenant");
        var actor = new ActorIdentity(Guid.NewGuid(), Guid.NewGuid());
        var lifecycle = new DocumentLifecycleService(fixture.Store, NullLogger<DocumentLifecycleService>.Instance);

        // Act
        await lifecycle.SoftDeleteAsync("tenant", "one", actor, development: false);

        // Assert
        Assert.Equal(JobStatus.Deleted, fixture.Persisted.Status);
        Assert.Equal(actor, fixture.Persisted.DeletedBy);
        Assert.Equal("Retained processing result", fixture.Persisted.PacketSummary);
        Assert.Null(await fixture.Store.GetJobAsync("one", tenantId: "tenant"));
        Assert.Null(await fixture.Store.TryAcquireJobAsync("tenant", "one"));
        await Assert.ThrowsAsync<CosmosException>(() => fixture.Store.UpsertJobAsync(previous!));
        Assert.Equal(JobStatus.Deleted, fixture.Persisted.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    public async Task Missing_Or_Wildcard_Versions_Cannot_Create_Unfenced_Ownership(string? etag)
    {
        // Arrange
        var fixture = new Fixture { Version = etag };

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.TryAcquireJobAsync("tenant", "one"));
        Assert.Equal(0, fixture.Replacements);
    }

    [Fact]
    public async Task A_Competing_Claim_Is_Not_Treated_As_Successful_Ownership()
    {
        // Arrange
        var fixture = new Fixture { RejectNextWrite = true };

        // Act / Assert
        Assert.Null(await fixture.Store.TryAcquireJobAsync("tenant", "one"));
        Assert.Null(fixture.Persisted.ProcessingOwner);
    }

    [Fact]
    public async Task Owned_Saves_Use_The_Preceding_Response_Version_And_Advance_The_Fence()
    {
        // Arrange
        var fixture = new Fixture();
        await using var lease = await fixture.Store.TryAcquireJobAsync("tenant", "one");
        lease!.Job.PacketSummary = "Completed analysis.";

        // Act
        await lease.SaveAsync();

        // Assert
        Assert.Equal(new[] { "v0", "v1" }, fixture.Fences);
        Assert.Equal("v2", lease.Job.ETag);
        Assert.Equal("Completed analysis.", fixture.Persisted.PacketSummary);
        Assert.Equal(1, fixture.Persisted.ExecutionAttempts);
    }

    [Fact]
    public async Task A_Rejected_Save_Cancels_The_Old_Owner_And_Cannot_Overwrite_Another_Owner()
    {
        // Arrange
        var fixture = new Fixture();
        var lease = await fixture.Store.TryAcquireJobAsync("tenant", "one");
        fixture.Persisted.ProcessingOwner = "another-owner";
        fixture.Version = "another-version";

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease!.SaveAsync());
        Assert.True(lease!.OwnershipLostToken.IsCancellationRequested);
        var replacements = fixture.Replacements;
        await lease.DisposeAsync();
        Assert.Equal(replacements, fixture.Replacements);
        Assert.Equal("another-owner", fixture.Persisted.ProcessingOwner);
    }

    [Fact]
    public async Task Missing_Response_Versions_Cancel_Ownership_Instead_Of_Allowing_An_Unfenced_Next_Save()
    {
        // Arrange
        var fixture = new Fixture();
        await using var lease = await fixture.Store.TryAcquireJobAsync("tenant", "one");
        fixture.MissingWriteResponseVersion = true;

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => lease!.SaveAsync());
        Assert.True(lease!.OwnershipLostToken.IsCancellationRequested);
        var replacements = fixture.Replacements;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.SaveAsync());
        Assert.Equal(replacements, fixture.Replacements);
    }

    [Fact]
    public async Task Explicit_Updates_Can_Clear_An_Expired_Terminal_Claim_But_Not_An_Active_Claim()
    {
        // Arrange
        var fixture = new Fixture();
        await using var owner = await fixture.Store.TryAcquireJobAsync("tenant", "one");
        owner!.Job.Status = JobStatus.Completed;
        await owner.SaveAsync();
        var completed = await fixture.Store.GetJobAsync("one", tenantId: "tenant");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.UpsertJobAsync(completed!));
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));

        // Act
        completed!.PacketSummary = "Explicitly reevaluated.";
        await fixture.Store.UpsertJobAsync(completed);

        // Assert
        Assert.Equal("Explicitly reevaluated.", fixture.Persisted.PacketSummary);
        Assert.Null(fixture.Persisted.ProcessingOwner);
        Assert.Equal("v2", fixture.Fences[^1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.SaveAsync());
    }

    [Fact]
    public async Task Expired_Ownership_Cannot_Write_Even_Without_A_Replacement_Owner()
    {
        // Arrange
        var fixture = new Fixture();
        await using var lease = await fixture.Store.TryAcquireJobAsync("tenant", "one");
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease!.SaveAsync());
        Assert.Equal(1, fixture.Replacements);
    }

    [Fact]
    public async Task Renewal_Preserves_The_Last_Saved_Snapshot_Not_In_Flight_Workflow_Changes()
    {
        // Arrange
        var fixture = new Fixture();
        await using var lease = await fixture.Store.TryAcquireJobAsync("tenant", "one");
        lease!.Job.PacketSummary = "Not yet published.";

        // Act
        await fixture.SecondWrite.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        Assert.Null(fixture.Persisted.PacketSummary);
        Assert.Equal("Not yet published.", lease.Job.PacketSummary);
        await lease.SaveAsync();
        Assert.Equal("Not yet published.", fixture.Persisted.PacketSummary);
    }

    [Fact]
    public async Task Release_Clears_Only_Its_Own_Claim_And_Preserves_Final_Results()
    {
        // Arrange
        var fixture = new Fixture();
        var lease = await fixture.Store.TryAcquireJobAsync("tenant", "one");
        lease!.Job.Status = JobStatus.Completed;
        lease.Job.PacketSummary = "Saved result.";
        await lease.SaveAsync();

        // Act
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        // Assert
        Assert.Null(fixture.Persisted.ProcessingOwner);
        Assert.Null(fixture.Persisted.ProcessingLeaseUntilUtc);
        Assert.Equal("Saved result.", fixture.Persisted.PacketSummary);
        Assert.Equal(JobStatus.Completed, fixture.Persisted.Status);
        Assert.Equal(3, fixture.Replacements);
    }

    [Fact]
    public async Task A_Wrong_Stored_Tenant_Is_Rejected_Before_Any_Claim()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Persisted = Job("different");

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.TryAcquireJobAsync("tenant", "one"));
        Assert.Equal(0, fixture.Replacements);
    }

    [Fact]
    public async Task Recovery_Uses_Bounded_Parameterized_Status_And_Expiry_Conditions()
    {
        // Arrange
        var fixture = new Fixture();
        QueryDefinition? definition = null;
        var feed = new Mock<FeedIterator<JobQueueTicket>>();
        feed.SetupGet(f => f.HasMoreResults).Returns(false);
        fixture.Container.Setup(c => c.GetItemQueryIterator<JobQueueTicket>(
                It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Callback<QueryDefinition, string, QueryRequestOptions>((query, _, _) => definition = query)
            .Returns(feed.Object);

        // Act
        await fixture.Store.ListRecoverableJobsAsync(25);

        // Assert
        Assert.Contains("TOP @count", definition!.QueryText);
        Assert.Contains("c.processingLeaseUntilUtc <= @now", definition.QueryText);
        Assert.Contains("IS_NULL(c.processingLeaseUntilUtc)", definition.QueryText);
        Assert.Contains(definition.GetQueryParameters(), p => p.Name == "@count" && Equals(p.Value, 25));
        Assert.Contains(definition.GetQueryParameters(), p => p.Name == "@running" && Equals(p.Value, "Running"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Store.ListRecoverableJobsAsync(101));
    }

    private static DocumentJob Job(string tenant = "tenant") => new()
    {
        JobId = "one",
        TenantId = tenant,
        TenantDisplayName = "Test",
        AppliedHarnessVersion = 1,
        FileName = "source.pdf"
    };

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class Fixture
    {
        public Mock<Container> Container { get; } = new();
        public TestClock Clock { get; } = new();
        public DocumentJob Persisted { get; set; } = Job();
        public string? Version { get; set; } = "v0";
        public bool RejectNextWrite { get; set; }
        public bool MissingWriteResponseVersion { get; set; }
        public int Replacements { get; private set; }
        public List<string?> Fences { get; } = [];
        public TaskCompletionSource SecondWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CosmosJobStore Store { get; }

        public Fixture()
        {
            Container.Setup(c => c.ReadItemAsync<DocumentJob>("one", new PartitionKey("tenant"),
                    It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(Response(Persisted, Version)));
            Container.Setup(c => c.ReplaceItemAsync(It.IsAny<DocumentJob>(), "one", new PartitionKey("tenant"),
                    It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                .Returns<DocumentJob, string, PartitionKey, ItemRequestOptions, CancellationToken>((job, _, _, options, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    Replacements++;
                    Fences.Add(options.IfMatchEtag);
                    if (RejectNextWrite || options.IfMatchEtag != Version)
                    {
                        RejectNextWrite = false;
                        throw new CosmosException("Version changed.", HttpStatusCode.PreconditionFailed, 0, "test", 0);
                    }
                    Persisted = Clone(job);
                    Version = $"v{Replacements}";
                    var response = Response(Persisted, MissingWriteResponseVersion ? null : Version);
                    if (Replacements == 2) SecondWrite.TrySetResult();
                    return Task.FromResult(response);
                });
            Store = new CosmosJobStore(Container.Object, Clock);
        }

        private static DocumentJob Clone(DocumentJob job) =>
            JsonSerializer.Deserialize<DocumentJob>(JsonSerializer.SerializeToUtf8Bytes(job))!;

        private static ItemResponse<DocumentJob> Response(DocumentJob job, string? etag)
        {
            var response = new Mock<ItemResponse<DocumentJob>>();
            response.SetupGet(r => r.Resource).Returns(Clone(job));
            response.SetupGet(r => r.ETag).Returns(etag!);
            return response.Object;
        }
    }
}
