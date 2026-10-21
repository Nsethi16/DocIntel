using System.Text.Json;
using DocIntel.Core.Authorization;
using DocIntel.Core.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DocIntel.Tests;

public sealed class DocumentLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleted_Documents_Disappear_From_Reads_Search_And_Recovery_Without_Physical_Erasure(bool sqlite)
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "DocIntel.Lifecycle.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var database = Path.Combine(directory, "jobs.sqlite");
            IJobStore store = sqlite ? new SqliteJobStore(database) : new InMemoryJobStore();
            var original = Job();
            await store.UpsertJobAsync(original);
            var actor = new ActorIdentity(Guid.NewGuid(), Guid.NewGuid());
            var lifecycle = new DocumentLifecycleService(store, NullLogger<DocumentLifecycleService>.Instance);

            // Act
            var tombstone = await lifecycle.SoftDeleteAsync("tenant", "one", actor, development: false);

            // Assert
            Assert.Equal(JobStatus.Deleted, tombstone!.Status);
            Assert.Equal(actor, tombstone.DeletedBy);
            Assert.NotNull(tombstone.DeletedAtUtc);
            Assert.False(tombstone.DeletionInDevelopment);
            Assert.Equal("Retained original value", tombstone.PacketSummary);
            Assert.Null(await store.GetJobAsync("one", tenantId: "tenant"));
            Assert.Empty(await store.ListJobsByTenantAsync("tenant"));
            Assert.Empty((await store.SearchDocumentsAsync("tenant", new DocumentQuery())).Documents);
            Assert.Empty(await store.ListRecoverableJobsAsync(100));
            Assert.Null(await store.TryAcquireJobAsync("tenant", "one"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertJobAsync(original));
            Assert.Null(await lifecycle.SoftDeleteAsync("tenant", "one", actor, development: false));
            if (sqlite)
            {
                using var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT body FROM document_jobs WHERE tenant='tenant' AND id='one'";
                var body = (byte[])(await command.ExecuteScalarAsync())!;
                var persisted = JsonSerializer.Deserialize<DocumentJob>(body)!;
                Assert.Equal(JobStatus.Deleted, persisted.Status);
                Assert.Equal(actor, persisted.DeletedBy);
                Assert.Equal("Retained original value", persisted.PacketSummary);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_Worker_Ownership_Blocks_Deletion_Instead_Of_Overwriting_An_In_Flight_Result(bool sqlite)
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "DocIntel.Lifecycle.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            IJobStore store = sqlite ? new SqliteJobStore(Path.Combine(directory, "jobs.sqlite")) : new InMemoryJobStore();
            var job = Job();
            job.Status = JobStatus.Queued;
            await store.UpsertJobAsync(job);
            await using var owner = await store.TryAcquireJobAsync("tenant", "one");
            var lifecycle = new DocumentLifecycleService(store, NullLogger<DocumentLifecycleService>.Instance);

            // Act / Assert
            await Assert.ThrowsAsync<DocumentLifecycleConflictException>(() => lifecycle.SoftDeleteAsync(
                "tenant", "one", new ActorIdentity(Guid.NewGuid(), Guid.NewGuid()), development: false));
            Assert.NotEqual(JobStatus.Deleted, (await store.GetJobAsync("one", tenantId: "tenant"))!.Status);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Unspecified_Retention_Never_Enables_Purge_Or_Invents_A_Legal_Retention_Period()
    {
        // Arrange / Act
        var policy = new DataRetentionPolicy();

        // Assert
        Assert.True(policy.IsValid);
        Assert.False(policy.PermanentPurgeEnabled);
        Assert.Null(policy.DocumentDays);
        Assert.Null(policy.ConversationDays);
        Assert.Null(policy.AuditDays);
        policy.PermanentPurgeEnabled = true;
        Assert.False(policy.IsValid);
        policy.PermanentPurgeEnabled = false;
        policy.AuditDays = 0;
        Assert.False(policy.IsValid);
        policy.AuditDays = 36_501;
        Assert.False(policy.IsValid);
    }

    private static DocumentJob Job() => new()
    {
        JobId = "one",
        TenantId = "tenant",
        TenantDisplayName = "Tenant",
        AppliedHarnessVersion = 1,
        FileName = "file.pdf",
        Status = JobStatus.Completed,
        PacketSummary = "Retained original value"
    };
}
