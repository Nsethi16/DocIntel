using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using DocIntel.Core;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class CloudAskAiStorageTests
{
    [Fact]
    public void Shared_Storage_Configuration_Does_Not_Fall_Back_To_Process_Local_State()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAI:BlobEndpoint"] = "https://example.blob.core.windows.net/",
            ["AzureAI:QueueEndpoint"] = "https://example.queue.core.windows.net/"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDocIntelCore(configuration);
        using var provider = services.BuildServiceProvider();

        // Act / Assert
        Assert.IsType<AzureBlobAskAiRunStore>(provider.GetRequiredService<IAskAiRunStore>());
        Assert.IsType<AzureQueueAskAiQueue>(provider.GetRequiredService<IAskAiQueue>());
    }

    [Fact]
    public void Invalid_Cloud_Endpoints_Are_Rejected_Instead_Of_Using_Local_Storage()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAI:BlobEndpoint"] = "not-a-valid-endpoint"
        }).Build();
        var services = new ServiceCollection();

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => services.AddDocIntelCore(configuration));
    }

    [Fact]
    public async Task Shared_Snapshot_Keys_Are_Tenant_Scoped_And_Do_Not_Interpret_Path_Characters()
    {
        // Arrange
        var fixture = new BlobFixture();
        var paths = new List<string>();
        fixture.Container.Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(paths.Add).Returns(fixture.Blob.Object);

        // Act
        await fixture.Store.UpsertRunAsync(Run("first/../tenant"));
        await fixture.Store.UpsertRunAsync(Run("second"));

        // Assert
        Assert.NotEqual(paths[0], paths[1]);
        Assert.DoesNotContain("..", paths[0]);
        Assert.DoesNotContain("tenant", paths[0]);
    }

    [Fact]
    public async Task Ownership_Conflicts_Are_Not_Treated_As_An_Unleased_Execution()
    {
        // Arrange
        var fixture = new BlobFixture();
        fixture.Lease.Setup(l => l.AcquireAsync(It.IsAny<TimeSpan>(), It.IsAny<RequestConditions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(409, "Owned.", "LeaseAlreadyPresent", null));

        // Act
        var lease = await fixture.Store.TryAcquireAsync("tenant", "one");

        // Assert
        Assert.Null(lease);
        fixture.Blob.Verify(b => b.DownloadStreamingAsync(
            It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Authorization_Failure_Is_Not_Disguised_As_A_Missing_Or_Owned_Run()
    {
        // Arrange
        var fixture = new BlobFixture();
        fixture.Lease.Setup(l => l.AcquireAsync(It.IsAny<TimeSpan>(), It.IsAny<RequestConditions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "Access denied."));

        // Act / Assert
        await Assert.ThrowsAsync<RequestFailedException>(() => fixture.Store.TryAcquireAsync("tenant", "one"));
    }

    [Fact]
    public async Task Shared_Saves_Are_Fenced_By_The_Storage_Lease()
    {
        // Arrange
        var fixture = new BlobFixture();
        string? fence = null;
        fixture.Blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<BinaryData, BlobUploadOptions, CancellationToken>((_, options, _) => fence = options.Conditions.LeaseId)
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());
        await using var lease = await fixture.Store.TryAcquireAsync("tenant", "one");
        lease!.Run.Status = JobStatus.Completed;

        // Act
        await lease.SaveAsync();

        // Assert
        Assert.Equal(fixture.LeaseId, fence);
    }

    [Fact]
    public async Task A_Storage_Fence_Rejection_Cancels_The_Owner_And_Prevents_Further_Saves()
    {
        // Arrange
        var fixture = new BlobFixture();
        var lease = await fixture.Store.TryAcquireAsync("tenant", "one");
        fixture.Blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(412, "Old owner.", "LeaseIdMismatchWithBlobOperation", null));

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease!.SaveAsync());
        Assert.True(lease!.OwnershipLostToken.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.SaveAsync());
        await lease.DisposeAsync();
        fixture.Lease.Verify(l => l.ReleaseAsync(It.IsAny<RequestConditions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Normal_Disposal_Releases_Once_And_A_Disposed_Owner_Cannot_Save()
    {
        // Arrange
        var fixture = new BlobFixture();
        var lease = await fixture.Store.TryAcquireAsync("tenant", "one");

        // Act
        await lease!.DisposeAsync();
        await lease.DisposeAsync();

        // Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.SaveAsync());
        fixture.Lease.Verify(l => l.ReleaseAsync(It.IsAny<RequestConditions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Wrong_Stored_Identity_Does_Not_Leak_Through_A_Tenant_Scoped_Read()
    {
        // Arrange
        var fixture = new BlobFixture();
        fixture.Reads(Run("different-tenant"));

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.GetRunAsync("tenant", "one"));
    }

    [Fact]
    public async Task Lease_Renewal_Failure_Cancels_Active_Ownership()
    {
        // Arrange
        var fixture = new BlobFixture();
        fixture.Lease.Setup(l => l.RenewAsync(It.IsAny<RequestConditions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "Renewal unavailable."));
        await using var lease = await fixture.Store.TryAcquireAsync("tenant", "one");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, lease!.OwnershipLostToken);

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(Timeout.InfiniteTimeSpan, linked.Token));

        // Assert
        Assert.False(deadline.IsCancellationRequested);
        Assert.True(lease.OwnershipLostToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Failed_Durable_Dispatch_Leaves_The_Persisted_Run_For_Recovery()
    {
        // Arrange
        var client = new Mock<QueueClient>();
        client.Setup(q => q.SendMessageAsync(It.IsAny<BinaryData>(), It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "Unavailable."));
        var queue = new AzureQueueAskAiQueue(client.Object, Mock.Of<QueueClient>(),
            new AskAiExecutionOptions(), NullLogger<AzureQueueAskAiQueue>.Instance);

        // Act
        var dispatched = await queue.TryEnqueueAsync(new AskAiQueueTicket("one", "tenant"));

        // Assert
        Assert.False(dispatched);
    }

    [Fact]
    public async Task Dispatch_Acknowledgement_Follows_Consumer_Processing_Not_Delivery()
    {
        // Arrange
        using var stop = new CancellationTokenSource();
        var client = new Mock<QueueClient>();
        var message = QueuesModelFactory.QueueMessage("message", "receipt",
            BinaryData.FromObjectAsJson(new AskAiQueueTicket("one", "tenant")), 1, null, null, null);
        client.Setup(q => q.ReceiveMessagesAsync(It.IsAny<int?>(), It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(new[] { message }, Mock.Of<Response>()));
        client.Setup(q => q.DeleteMessageAsync("message", "receipt", It.IsAny<CancellationToken>()))
            .Callback(() => stop.Cancel()).ReturnsAsync(Mock.Of<Response>());
        var queue = new AzureQueueAskAiQueue(client.Object, Mock.Of<QueueClient>(),
            new AskAiExecutionOptions(), NullLogger<AzureQueueAskAiQueue>.Instance);
        await using var iterator = queue.ReadAllAsync(stop.Token).GetAsyncEnumerator();

        // Act / Assert
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(new AskAiQueueTicket("one", "tenant"), iterator.Current);
        client.Verify(q => q.DeleteMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync());
        client.Verify(q => q.DeleteMessageAsync("message", "receipt", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Malformed_Dispatch_Is_Quarantined_Without_Being_Yielded_To_A_Processor()
    {
        // Arrange
        using var stop = new CancellationTokenSource();
        var client = new Mock<QueueClient>();
        var poison = new Mock<QueueClient>();
        var message = QueuesModelFactory.QueueMessage("message", "receipt",
            BinaryData.FromString("{\"TenantId\":\"\",\"RunId\":\"one\"}"), 1, null, null, null);
        client.Setup(q => q.ReceiveMessagesAsync(It.IsAny<int?>(), It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(new[] { message }, Mock.Of<Response>()));
        poison.Setup(q => q.SendMessageAsync(It.IsAny<BinaryData>(), It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<SendReceipt>>());
        client.Setup(q => q.DeleteMessageAsync("message", "receipt", It.IsAny<CancellationToken>()))
            .Callback(() => stop.Cancel()).ReturnsAsync(Mock.Of<Response>());
        var queue = new AzureQueueAskAiQueue(client.Object, poison.Object,
            new AskAiExecutionOptions(), NullLogger<AzureQueueAskAiQueue>.Instance);
        await using var iterator = queue.ReadAllAsync(stop.Token).GetAsyncEnumerator();

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync());
        poison.Verify(q => q.SendMessageAsync(It.IsAny<BinaryData>(), It.IsAny<TimeSpan?>(),
            It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static AskAiRun Run(string tenant = "tenant") => new()
    {
        RunId = "one",
        TenantId = tenant,
        Question = "Question"
    };

    private sealed class BlobFixture
    {
        public Mock<BlobContainerClient> Container { get; } = new();
        public Mock<BlobClient> Blob { get; } = new();
        public Mock<BlobLeaseClient> Lease { get; } = new();
        public string? LeaseId { get; private set; }
        public AzureBlobAskAiRunStore Store { get; }

        public BlobFixture()
        {
            Container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(Blob.Object);
            Blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());
            Lease.Setup(l => l.AcquireAsync(It.IsAny<TimeSpan>(), It.IsAny<RequestConditions>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<Response<BlobLease>>());
            Lease.Setup(l => l.ReleaseAsync(It.IsAny<RequestConditions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<Response<ReleasedObjectInfo>>());
            Store = new AzureBlobAskAiRunStore(Container.Object, NullLogger<AzureBlobAskAiRunStore>.Instance,
                (_, id) => { LeaseId = id; return Lease.Object; });
            Reads(Run());
        }

        public void Reads(AskAiRun run)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(run);
            Blob.Setup(b => b.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Response.FromValue(
                    BlobsModelFactory.BlobDownloadStreamingResult(new MemoryStream(bytes),
                        BlobsModelFactory.BlobDownloadDetails(contentLength: bytes.Length)), Mock.Of<Response>()));
        }
    }
}
