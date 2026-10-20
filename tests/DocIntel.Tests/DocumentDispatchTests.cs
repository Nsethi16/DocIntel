using System.Text.Json;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using DocIntel.Core.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class DocumentDispatchTests
{
    [Fact]
    public async Task Durable_Dispatch_Contains_Only_Tenant_And_Job_Identifiers()
    {
        // Arrange
        var client = new Mock<QueueClient>();
        BinaryData? sent = null;
        client.Setup(q => q.SendMessageAsync(It.IsAny<BinaryData>(), It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<BinaryData, TimeSpan?, TimeSpan?, CancellationToken>((body, _, _, _) => sent = body)
            .ReturnsAsync(Mock.Of<Response<SendReceipt>>());
        var queue = Queue(client.Object);

        // Act
        Assert.True(await queue.TryEnqueueAsync(new JobQueueTicket("one", "tenant")));

        // Assert
        using var json = JsonDocument.Parse(sent!.ToMemory());
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
        Assert.Equal(new JobQueueTicket("one", "tenant"), sent.ToObjectFromJson<JobQueueTicket>());
    }

    [Fact]
    public async Task A_Dispatch_Failure_Does_Not_Pretend_The_Persisted_Job_Was_Enqueued()
    {
        // Arrange
        var client = new Mock<QueueClient>();
        client.Setup(q => q.SendMessageAsync(It.IsAny<BinaryData>(), It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "Unavailable."));

        // Act / Assert
        Assert.False(await Queue(client.Object).TryEnqueueAsync(new JobQueueTicket("one", "tenant")));
    }

    [Fact]
    public async Task Document_Acknowledgement_Follows_Processing_And_Covers_The_Execution_Budget()
    {
        // Arrange
        using var stop = new CancellationTokenSource();
        var client = new Mock<QueueClient>();
        var message = QueuesModelFactory.QueueMessage("message", "receipt",
            BinaryData.FromObjectAsJson(new JobQueueTicket("one", "tenant")), 1, null, null, null);
        client.Setup(q => q.ReceiveMessagesAsync(1, TimeSpan.FromSeconds(1_920), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(new[] { message }, Mock.Of<Response>()));
        client.Setup(q => q.DeleteMessageAsync("message", "receipt", It.IsAny<CancellationToken>()))
            .Callback(() => stop.Cancel()).ReturnsAsync(Mock.Of<Response>());
        await using var iterator = Queue(client.Object).ReadAllAsync(stop.Token).GetAsyncEnumerator();

        // Act / Assert
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(new JobQueueTicket("one", "tenant"), iterator.Current);
        client.Verify(q => q.DeleteMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync());
        client.Verify(q => q.DeleteMessageAsync("message", "receipt", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("{\"JobId\":\"one\",\"TenantId\":\"\"}", 1)]
    [InlineData("{\"JobId\":\"one\",\"TenantId\":\"tenant\"}", 6)]
    public async Task Invalid_Or_Repeated_Delivery_Is_Quarantined_Before_Processing(string json, long deliveries)
    {
        // Arrange
        using var stop = new CancellationTokenSource();
        var client = new Mock<QueueClient>();
        var poison = new Mock<QueueClient>();
        var message = QueuesModelFactory.QueueMessage("message", "receipt",
            BinaryData.FromString(json), deliveries, null, null, null);
        client.Setup(q => q.ReceiveMessagesAsync(It.IsAny<int?>(), It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(new[] { message }, Mock.Of<Response>()));
        poison.Setup(q => q.SendMessageAsync(It.IsAny<BinaryData>(), It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<SendReceipt>>());
        client.Setup(q => q.DeleteMessageAsync("message", "receipt", It.IsAny<CancellationToken>()))
            .Callback(() => stop.Cancel()).ReturnsAsync(Mock.Of<Response>());
        var queue = new AzureQueueJobQueue(client.Object, poison.Object, new DocumentProcessingOptions(),
            NullLogger<AzureQueueJobQueue>.Instance);
        await using var iterator = queue.ReadAllAsync(stop.Token).GetAsyncEnumerator();

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync());
        poison.Verify(q => q.SendMessageAsync(It.IsAny<BinaryData>(), It.IsAny<TimeSpan?>(),
            It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static AzureQueueJobQueue Queue(QueueClient client) => new(
        client, Mock.Of<QueueClient>(), new DocumentProcessingOptions(), NullLogger<AzureQueueJobQueue>.Instance);
}
