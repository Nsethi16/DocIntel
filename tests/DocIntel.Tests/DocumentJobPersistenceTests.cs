using DocIntel.Core.Jobs;
using Xunit;

namespace DocIntel.Tests;

public sealed class DocumentJobPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DocIntel.JobPersistence.Tests",
        Guid.NewGuid().ToString("N"));

    private string Database => Path.Combine(_directory, "jobs.sqlite");

    [Fact]
    public async Task Document_Metadata_Survives_Store_Recreation()
    {
        // Arrange
        var first = new SqliteJobStore(Database);
        var job = Job();
        job.OriginalSha256 = "accepted-source-hash";
        await first.UpsertJobAsync(job);

        // Act
        var restarted = new SqliteJobStore(Database);
        var recovered = await restarted.GetJobAsync("one", tenantId: "tenant");

        // Assert
        Assert.Equal("accepted-source-hash", recovered?.OriginalSha256);
        Assert.Single(await restarted.ListRecoverableJobsAsync(100));
    }

    [Fact]
    public async Task Identical_Job_Ids_In_Different_Tenants_Do_Not_Overwrite_Each_Other()
    {
        // Arrange
        var store = new SqliteJobStore(Database);
        await store.UpsertJobAsync(Job("first"));
        await store.UpsertJobAsync(Job("second"));

        // Act / Assert
        Assert.Equal("first", (await store.GetJobAsync("one", tenantId: "first"))?.TenantId);
        Assert.Equal("second", (await store.GetJobAsync("one", tenantId: "second"))?.TenantId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetJobAsync("one"));
    }

    [Fact]
    public async Task Independent_Store_Instances_Cannot_Claim_The_Same_Active_Job()
    {
        // Arrange
        var first = new SqliteJobStore(Database);
        var second = new SqliteJobStore(Database);
        await first.UpsertJobAsync(Job());
        await using var owner = await first.TryAcquireJobAsync("tenant", "one");

        // Act
        var other = await second.TryAcquireJobAsync("tenant", "one");

        // Assert
        Assert.NotNull(owner);
        Assert.Null(other);
        Assert.Empty(await second.ListRecoverableJobsAsync(100));
    }

    [Fact]
    public async Task Expired_Owners_Are_Fenced_After_A_Replacement_Claims_The_Job()
    {
        // Arrange
        var clock = new TestClock();
        var store = new SqliteJobStore(Database, clock);
        await store.UpsertJobAsync(Job());
        await using var old = await store.TryAcquireJobAsync("tenant", "one");
        old!.Job.Status = JobStatus.Running;
        await old.SaveAsync();
        clock.Advance(TimeSpan.FromSeconds(61));
        await using var current = await new SqliteJobStore(Database, clock).TryAcquireJobAsync("tenant", "one");
        current!.Job.Status = JobStatus.Completed;
        current.Job.PacketSummary = "Current result.";
        await current.SaveAsync();

        // Act / Assert
        old.Job.PacketSummary = "Stale result.";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.SaveAsync());
        Assert.True(old.OwnershipLostToken.IsCancellationRequested);
        Assert.Equal("Current result.", (await store.GetJobAsync("one", tenantId: "tenant"))?.PacketSummary);
    }

    [Fact]
    public async Task An_Expired_Owner_Cannot_Save_Even_Before_A_Replacement_Claims_The_Job()
    {
        // Arrange
        var clock = new TestClock();
        var store = new SqliteJobStore(Database, clock);
        await store.UpsertJobAsync(Job());
        await using var owner = await store.TryAcquireJobAsync("tenant", "one");
        clock.Advance(TimeSpan.FromSeconds(61));

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner!.SaveAsync());
        Assert.Single(await store.ListRecoverableJobsAsync(100));
    }

    [Fact]
    public async Task Ordinary_Updates_Cannot_Overwrite_An_Owned_Job()
    {
        // Arrange
        var store = new SqliteJobStore(Database);
        await store.UpsertJobAsync(Job());
        var stale = await store.GetJobAsync("one", tenantId: "tenant");
        await using var owner = await store.TryAcquireJobAsync("tenant", "one");

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertJobAsync(stale!));
    }

    [Fact]
    public async Task An_Expired_Terminal_Claim_Does_Not_Permanently_Block_Explicit_Updates()
    {
        // Arrange
        var clock = new TestClock();
        var store = new SqliteJobStore(Database, clock);
        await store.UpsertJobAsync(Job());
        await using var owner = await store.TryAcquireJobAsync("tenant", "one");
        owner!.Job.Status = JobStatus.Completed;
        await owner.SaveAsync();
        var completed = await store.GetJobAsync("one", tenantId: "tenant");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertJobAsync(completed!));
        clock.Advance(TimeSpan.FromSeconds(61));

        // Act
        completed!.PacketSummary = "Explicitly updated after terminal-owner expiry.";
        await store.UpsertJobAsync(completed);

        // Assert
        Assert.Equal(completed.PacketSummary, (await store.GetJobAsync("one", tenantId: "tenant"))?.PacketSummary);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.SaveAsync());
        Assert.Empty(await store.ListRecoverableJobsAsync(100));
    }

    [Fact]
    public async Task Stale_Administrative_Reads_Cannot_Overwrite_Newer_Results()
    {
        // Arrange
        var store = new SqliteJobStore(Database);
        await store.UpsertJobAsync(Job());
        var first = await store.GetJobAsync("one", tenantId: "tenant");
        var stale = await store.GetJobAsync("one", tenantId: "tenant");
        first!.PacketSummary = "Newer result.";
        await store.UpsertJobAsync(first);

        // Act / Assert
        stale!.PacketSummary = "Stale result.";
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertJobAsync(stale));
        Assert.Equal("Newer result.", (await store.GetJobAsync("one", tenantId: "tenant"))?.PacketSummary);
    }

    [Fact]
    public async Task Completed_Jobs_Are_Not_Rediscovered_Or_Reexecuted()
    {
        // Arrange
        var store = new SqliteJobStore(Database);
        await store.UpsertJobAsync(Job());
        await using (var owner = await store.TryAcquireJobAsync("tenant", "one"))
        {
            owner!.Job.Status = JobStatus.Completed;
            await owner.SaveAsync();
        }

        // Act / Assert
        Assert.Empty(await store.ListRecoverableJobsAsync(100));
        Assert.Null(await store.TryAcquireJobAsync("tenant", "one"));
    }

    [Fact]
    public async Task Search_Applies_The_Server_Owned_Document_Scope_After_Restart()
    {
        // Arrange
        var store = new SqliteJobStore(Database);
        var allowed = Job();
        allowed.Status = JobStatus.Completed;
        await store.UpsertJobAsync(allowed);
        var other = Job(id: "two");
        other.Status = JobStatus.Completed;
        await store.UpsertJobAsync(other);

        // Act
        var result = await new SqliteJobStore(Database).SearchDocumentsAsync("tenant",
            new DocumentQuery().WithAccess(new HashSet<string> { "one" }));

        // Assert
        Assert.Equal("one", Assert.Single(result.Documents).JobId);
    }

    private static DocumentJob Job(string tenant = "tenant", string id = "one") => new()
    {
        JobId = id,
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

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
