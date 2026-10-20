using System.Net;
using DocIntel.Core.Ocr;
using DocIntel.Core.Storage;
using Microsoft.Azure.Cosmos;
using Moq;
using Xunit;
using BudgetRecord = DocIntel.Core.Storage.CosmosOcrUsageStore.BudgetRecord;
using MonthRecord = DocIntel.Core.Storage.CosmosOcrUsageStore.MonthRecord;
using RequestRecord = DocIntel.Core.Storage.CosmosOcrUsageStore.RequestRecord;

namespace DocIntel.Tests;

public sealed class CosmosOcrUsageTests
{
    [Fact]
    public async Task Reservation_Fences_The_Live_Budget_And_Updates_Counter_And_Request_In_One_Partition()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Month(new MonthRecord("month-2026-10", "tenant", "2026-10", 3, 1));

        // Act
        var allocated = await fixture.Store.ReserveAsync("tenant", "2026-10", fixture.RequestId, 2, "OCR", "one-a1");

        // Assert
        Assert.Equal(5, allocated.Usage.ReservedPages);
        Assert.Equal(2, allocated.Usage.RequestCount);
        fixture.Container.Verify(c => c.CreateTransactionalBatch(new PartitionKey("tenant")), Times.Once);
        fixture.Batch.Verify(b => b.ReplaceItem("budget", It.IsAny<BudgetRecord>(),
            It.Is<TransactionalBatchItemRequestOptions>(o => o.IfMatchEtag == "budget-etag")), Times.Once);
        fixture.Batch.Verify(b => b.ReplaceItem("month-2026-10", It.Is<MonthRecord>(m => m.Pages == 5),
            It.Is<TransactionalBatchItemRequestOptions>(o => o.IfMatchEtag == "month-etag")), Times.Once);
        fixture.Batch.Verify(b => b.CreateItem(It.Is<RequestRecord>(r => r.Pages == 2 &&
            r.TenantId == "tenant" && r.Id == "request-" + fixture.RequestId),
            It.IsAny<TransactionalBatchItemRequestOptions>()), Times.Once);
    }

    [Fact]
    public async Task An_Exhausted_Live_Budget_Does_Not_Submit_A_Transaction()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Budget(new BudgetRecord("budget", "tenant", 0, 80, 1));

        // Act / Assert
        await Assert.ThrowsAsync<OcrBudgetExceededException>(() =>
            fixture.Store.ReserveAsync("tenant", "2026-10", fixture.RequestId, 1, "OCR", "one"));
        fixture.Container.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    public async Task Missing_Or_Wildcard_Budget_Fences_Cannot_Authorize_Usage(string? version)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Budget(new BudgetRecord("budget", "tenant", 10, 80, 1), version);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Store.ReserveAsync("tenant", "2026-10", fixture.RequestId, 1, "OCR", "one"));
        fixture.Container.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Never);
    }

    [Fact]
    public async Task A_Version_Conflict_Rereads_And_Retries_Without_Claiming_A_Partial_Reservation()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Batch.SetupSequence(b => b.ExecuteAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(BatchResponse(false, HttpStatusCode.PreconditionFailed))
            .ReturnsAsync(BatchResponse(true, HttpStatusCode.OK));

        // Act
        var allocated = await fixture.Store.ReserveAsync("tenant", "2026-10", fixture.RequestId, 2, "OCR", "one");

        // Assert
        Assert.Equal(2, allocated.Usage.ReservedPages);
        fixture.Batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task An_Existing_Request_Is_Validated_And_Does_Not_Allocate_Again()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Month(new MonthRecord("month-2026-10", "tenant", "2026-10", 2, 1));
        fixture.Container.Setup(c => c.ReadItemAsync<RequestRecord>("request-" + fixture.RequestId,
                new PartitionKey("tenant"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response(new RequestRecord("request-" + fixture.RequestId,
                "tenant", "2026-10", 2, "OCR", "one"), "request-etag"));

        // Act
        var allocation = await fixture.Store.ReserveAsync("tenant", "2026-10", fixture.RequestId, 2, "OCR", "one");

        // Assert
        Assert.True(allocation.Replayed);
        Assert.Equal(2, allocation.Usage.ReservedPages);
        fixture.Container.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Never);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Store.ReserveAsync("tenant", "2026-10", fixture.RequestId, 3, "OCR", "one"));
    }

    [Fact]
    public async Task Stale_Admin_Policy_Revisions_Cannot_Overwrite_Current_Limits()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Budget(new BudgetRecord("budget", "tenant", 25, 80, 2));

        // Act / Assert
        await Assert.ThrowsAsync<OcrBudgetConflictException>(() =>
            fixture.Store.UpdateBudgetAsync("tenant", new OcrBudget(100, 80, "1")));
        fixture.Container.Verify(c => c.ReplaceItemAsync(It.IsAny<BudgetRecord>(), It.IsAny<string>(),
            It.IsAny<PartitionKey?>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_Current_Budget_Edit_Uses_The_Latest_Storage_Fence_And_Advances_Its_Policy_Revision()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Container.Setup(c => c.ReplaceItemAsync(It.IsAny<BudgetRecord>(), "budget", new PartitionKey("tenant"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response(new BudgetRecord("budget", "tenant", 25, 70, 2), "updated-etag"));

        // Act
        var budget = await fixture.Store.UpdateBudgetAsync("tenant", new OcrBudget(25, 70, "1"));

        // Assert
        Assert.Equal("2", budget.Version);
        fixture.Container.Verify(c => c.ReplaceItemAsync(It.Is<BudgetRecord>(b => b.Limit == 25 &&
            b.Revision == 2 && b.Warning == 70), "budget", new PartitionKey("tenant"),
            It.Is<ItemRequestOptions>(o => o.IfMatchEtag == "budget-etag"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Storage_Authorization_Errors_Are_Not_Replaced_By_An_InMemory_Allowance()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Container.Setup(c => c.ReadItemAsync<BudgetRecord>("budget", new PartitionKey("tenant"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("Denied.", HttpStatusCode.Forbidden, 0, "test", 0));

        // Act / Assert
        await Assert.ThrowsAsync<CosmosException>(() => fixture.Store.GetBudgetAsync("tenant"));
    }

    private static ItemResponse<T> Response<T>(T resource, string? version)
    {
        var response = new Mock<ItemResponse<T>>();
        response.SetupGet(r => r.Resource).Returns(resource);
        response.SetupGet(r => r.ETag).Returns(version!);
        return response.Object;
    }

    private static TransactionalBatchResponse BatchResponse(bool success, HttpStatusCode status)
    {
        var response = new Mock<TransactionalBatchResponse>();
        response.SetupGet(r => r.IsSuccessStatusCode).Returns(success);
        response.SetupGet(r => r.StatusCode).Returns(status);
        return response.Object;
    }

    private sealed class Fixture
    {
        public Mock<Container> Container { get; } = new();
        public Mock<TransactionalBatch> Batch { get; } = new();
        public string RequestId { get; } = Guid.NewGuid().ToString("N");
        public CosmosOcrUsageStore Store { get; }

        public Fixture()
        {
            Store = new CosmosOcrUsageStore(Container.Object);
            Budget(new BudgetRecord("budget", "tenant", 100, 80, 1));
            Container.Setup(c => c.ReadItemAsync<MonthRecord>(It.IsAny<string>(), new PartitionKey("tenant"),
                    It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new CosmosException("Missing.", HttpStatusCode.NotFound, 0, "test", 0));
            Container.Setup(c => c.ReadItemAsync<RequestRecord>(It.IsAny<string>(), new PartitionKey("tenant"),
                    It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new CosmosException("Missing.", HttpStatusCode.NotFound, 0, "test", 0));
            Container.Setup(c => c.CreateTransactionalBatch(new PartitionKey("tenant"))).Returns(Batch.Object);
            Batch.Setup(b => b.ReplaceItem("budget", It.IsAny<BudgetRecord>(),
                It.IsAny<TransactionalBatchItemRequestOptions>())).Returns(Batch.Object);
            Batch.Setup(b => b.CreateItem(It.IsAny<MonthRecord>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
                .Returns(Batch.Object);
            Batch.Setup(b => b.ReplaceItem(It.IsAny<string>(), It.IsAny<MonthRecord>(),
                It.IsAny<TransactionalBatchItemRequestOptions>())).Returns(Batch.Object);
            Batch.Setup(b => b.CreateItem(It.IsAny<RequestRecord>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
                .Returns(Batch.Object);
            Batch.Setup(b => b.ExecuteAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(BatchResponse(true, HttpStatusCode.OK));
        }

        public void Budget(BudgetRecord record, string? version = "budget-etag") =>
            Container.Setup(c => c.ReadItemAsync<BudgetRecord>("budget", new PartitionKey("tenant"),
                    It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response(record, version));

        public void Month(MonthRecord record) =>
            Container.Setup(c => c.ReadItemAsync<MonthRecord>("month-2026-10", new PartitionKey("tenant"),
                    It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response(record, "month-etag"));
    }
}
