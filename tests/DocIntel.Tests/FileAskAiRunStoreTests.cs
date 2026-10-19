using System.Text.Json;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DocIntel.Tests;

public sealed class FileAskAiRunStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "DocIntel.RunStore.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_New_Store_Instance_Reads_Previously_Persisted_Runs()
    {
        // Arrange
        var original = new FileAskAiRunStore(_directory);
        await original.UpsertRunAsync(CreateRun());

        // Act
        var restarted = new FileAskAiRunStore(_directory);
        var run = await restarted.GetRunAsync("tenant", "one");
        var pending = await restarted.ListUnfinishedRunsAsync(100);

        // Assert
        Assert.Equal("Hi", run?.Question);
        Assert.Equal(new AskAiQueueTicket("one", "tenant"), Assert.Single(pending));
    }

    [Fact]
    public async Task Independent_Store_Instances_Cannot_Own_The_Same_Run()
    {
        // Arrange
        var first = new FileAskAiRunStore(_directory);
        var second = new FileAskAiRunStore(_directory);
        await first.UpsertRunAsync(CreateRun());
        await using var lease = await first.TryAcquireAsync("tenant", "one");

        // Act
        var competing = await second.TryAcquireAsync("tenant", "one");
        var pending = await second.ListUnfinishedRunsAsync(100);
        lease!.Run.Status = JobStatus.Running;
        await lease.SaveAsync();

        // Assert
        Assert.Null(competing);
        Assert.Empty(pending);
        Assert.Equal(JobStatus.Running, (await second.GetRunAsync("tenant", "one"))?.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.UpsertRunAsync(CreateRun()));
    }

    [Fact]
    public async Task A_Released_Owner_Cannot_Overwrite_A_Later_Owner()
    {
        // Arrange
        var store = new FileAskAiRunStore(_directory);
        await store.UpsertRunAsync(CreateRun());
        var oldLease = await store.TryAcquireAsync("tenant", "one");
        await oldLease!.DisposeAsync();
        await using var current = await store.TryAcquireAsync("tenant", "one");
        current!.Run.Status = JobStatus.Completed;
        current.Run.Answer = "Current answer.";
        await current.SaveAsync();

        // Act
        oldLease.Run.Answer = "Stale answer.";
        await Assert.ThrowsAsync<ObjectDisposedException>(() => oldLease.SaveAsync());

        // Assert
        Assert.Equal("Current answer.", (await store.GetRunAsync("tenant", "one"))?.Answer);
    }

    [Fact]
    public async Task Abandoned_Running_State_Is_Discovered_Without_A_Queue_Message()
    {
        // Arrange
        var original = new FileAskAiRunStore(_directory);
        await original.UpsertRunAsync(CreateRun());
        await using (var lease = await original.TryAcquireAsync("tenant", "one"))
        {
            lease!.Run.Status = JobStatus.Running;
            lease.Run.ExecutionAttempts = 1;
            await lease.SaveAsync();
        }

        // Act
        var restarted = new FileAskAiRunStore(_directory);
        var pending = await restarted.ListUnfinishedRunsAsync(100);
        await using var newOwner = await restarted.TryAcquireAsync("tenant", "one");

        // Assert
        Assert.Single(pending);
        Assert.NotNull(newOwner);
        Assert.Equal(1, newOwner.Run.ExecutionAttempts);
    }

    [Fact]
    public async Task Storage_Keys_Isolate_Tenants_And_Do_Not_Interpret_Path_Characters()
    {
        // Arrange
        var store = new FileAskAiRunStore(_directory);
        await store.UpsertRunAsync(CreateRun("..\\first", "..\\one"));
        await store.UpsertRunAsync(CreateRun("..\\second", "..\\one"));

        // Act
        var first = await store.GetRunAsync("..\\first", "..\\one");
        var second = await store.GetRunAsync("..\\second", "..\\one");
        var other = await store.GetRunAsync("other", "..\\one");

        // Assert
        Assert.Equal("..\\first", first?.TenantId);
        Assert.Equal("..\\second", second?.TenantId);
        Assert.Null(other);
        Assert.Equal(2, Directory.GetDirectories(_directory).Length);
    }

    [Fact]
    public async Task A_Canceled_Save_Leaves_The_Last_Complete_Snapshot_Readable()
    {
        // Arrange
        var store = new FileAskAiRunStore(_directory);
        await store.UpsertRunAsync(CreateRun());
        await using var lease = await store.TryAcquireAsync("tenant", "one");
        lease!.Run.Answer = "Must not be committed.";
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.SaveAsync(cancellation.Token));

        // Assert
        Assert.Null((await store.GetRunAsync("tenant", "one"))?.Answer);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Polling_Sees_Complete_Snapshots_During_Repeated_Writes()
    {
        // Arrange
        var store = new FileAskAiRunStore(_directory);
        await store.UpsertRunAsync(CreateRun());
        await using var lease = await store.TryAcquireAsync("tenant", "one");
        var writer = Task.Run(async () =>
        {
            for (var index = 1; index <= 30; index++)
            {
                lease!.Run.Answer = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                lease.Run.DocumentsEvaluated = index;
                await lease.SaveAsync();
            }
        });

        // Act / Assert
        try
        {
            while (!writer.IsCompleted)
            {
                var snapshot = await store.GetRunAsync("tenant", "one");
                if (snapshot!.Answer is not null)
                {
                    Assert.Equal(snapshot.DocumentsEvaluated.ToString(
                        System.Globalization.CultureInfo.InvariantCulture), snapshot.Answer);
                }
            }
        }
        finally
        {
            await writer;
        }
    }

    [Fact]
    public async Task A_Corrupt_Snapshot_Does_Not_Prevent_Other_Runs_From_Being_Discovered()
    {
        // Arrange
        var store = new FileAskAiRunStore(_directory);
        await store.UpsertRunAsync(CreateRun());
        var path = Assert.Single(Directory.GetFiles(_directory, "*.json", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(path, "{corrupt");
        await store.UpsertRunAsync(CreateRun(runId: "two"));

        // Act
        var pending = await store.ListUnfinishedRunsAsync(100);

        // Assert
        Assert.Equal("two", Assert.Single(pending).RunId);
        await Assert.ThrowsAsync<JsonException>(() => store.GetRunAsync("tenant", "one"));
    }

    [Fact]
    public async Task Full_Dispatch_Hints_Do_Not_Lose_The_Persisted_Run()
    {
        // Arrange
        var queue = new ChannelAskAiQueue(NullLogger<ChannelAskAiQueue>.Instance);
        var store = new FileAskAiRunStore(_directory);
        for (var index = 0; index < 100; index++)
        {
            Assert.True(queue.TryEnqueue(new AskAiQueueTicket($"other-{index}", "tenant")));
        }
        await store.UpsertRunAsync(CreateRun());

        // Act
        var acceptedHint = queue.TryEnqueue(new AskAiQueueTicket("one", "tenant"));
        var unfinished = await store.ListUnfinishedRunsAsync(100);

        // Assert
        Assert.False(acceptedHint);
        Assert.Equal(new AskAiQueueTicket("one", "tenant"), Assert.Single(unfinished));
    }

    private static AskAiRun CreateRun(string tenantId = "tenant", string runId = "one") => new()
    {
        TenantId = tenantId,
        RunId = runId,
        Question = "Hi"
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
