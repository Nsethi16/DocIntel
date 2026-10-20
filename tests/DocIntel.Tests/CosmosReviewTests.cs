using System.Net;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Jobs;
using DocIntel.Core.Review;
using DocIntel.Core.Storage;
using Microsoft.Azure.Cosmos;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class CosmosReviewTests
{
    [Fact]
    public async Task Session_Creation_And_Audit_Append_Are_One_Tenant_Partition_Transaction()
    {
        // Arrange
        var fixture = new Fixture();

        // Act
        await fixture.Store.CreateAsync(fixture.Current, fixture.Created);

        // Assert
        fixture.Container.Verify(c => c.CreateTransactionalBatch(new PartitionKey("tenant")), Times.Once);
        fixture.Batch.Verify(b => b.CreateItem(It.Is<ReviewSession>(s => s.Id == fixture.Current.Id),
            It.IsAny<TransactionalBatchItemRequestOptions>()), Times.Once);
        fixture.Batch.Verify(b => b.CreateItem(It.Is<ReviewAuditEvent>(a => a.Hash == fixture.Created.Hash),
            It.IsAny<TransactionalBatchItemRequestOptions>()), Times.Once);
        fixture.Batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_State_Change_Is_Fenced_And_Appended_With_Its_Audit_In_One_Batch()
    {
        // Arrange
        var fixture = new Fixture();
        var changed = ReviewEngine.Claim(fixture.Current, new ActorIdentity(Guid.NewGuid(), Guid.NewGuid()),
            fixture.Now) with
        { Version = 2 };
        var audit = ReviewEngine.Audit(changed, "claim", changed.ClaimedBy, fixture.Now);
        changed = changed with { LastAuditHash = audit.Hash };

        // Act
        await fixture.Store.SaveAsync(changed, 1, audit);

        // Assert
        fixture.Batch.Verify(b => b.ReplaceItem(fixture.Current.Id, It.Is<ReviewSession>(s => s.Version == 2),
            It.Is<TransactionalBatchItemRequestOptions>(o => o.IfMatchEtag == "current-etag")), Times.Once);
        fixture.Batch.Verify(b => b.CreateItem(It.Is<ReviewAuditEvent>(a => a.PreviousHash == fixture.Created.Hash),
            It.IsAny<TransactionalBatchItemRequestOptions>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    public async Task Missing_Storage_Versions_Do_Not_Enable_Unfenced_Review_Writes(string? version)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Reads(fixture.Current, version);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.GetAsync("tenant", fixture.Current.Id));
        fixture.Container.Verify(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>()), Times.Never);
    }

    [Fact]
    public async Task Failed_Atomic_Audit_Publication_Does_Not_Report_A_Successful_Review()
    {
        // Arrange
        var fixture = new Fixture();
        var response = new Mock<TransactionalBatchResponse>();
        response.SetupGet(r => r.IsSuccessStatusCode).Returns(false);
        response.SetupGet(r => r.StatusCode).Returns(HttpStatusCode.PreconditionFailed);
        fixture.Batch.Setup(b => b.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(response.Object);

        // Act / Assert
        await Assert.ThrowsAsync<ReviewConflictException>(() => fixture.Store.CreateAsync(fixture.Current, fixture.Created));
        fixture.Batch.Verify(b => b.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class Fixture
    {
        public Mock<Container> Container { get; } = new();
        public Mock<TransactionalBatch> Batch { get; } = new();
        public CosmosReviewStore Store { get; }
        public ReviewSession Current { get; }
        public ReviewAuditEvent Created { get; }
        public DateTimeOffset Now { get; } = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

        public Fixture()
        {
            var job = new DocumentJob
            {
                TenantId = "tenant",
                TenantDisplayName = "Test",
                JobId = "one",
                FileName = "source.pdf",
                AppliedHarnessVersion = 1,
                Segments = [new DocumentSlice { StartPage = 1, EndPage = 1 }]
            };
            var initial = ReviewEngine.Create(job, new ReviewPolicy(), Now);
            Created = ReviewEngine.Audit(initial, "Created", null, Now);
            Current = initial with { LastAuditHash = Created.Hash };
            Store = new CosmosReviewStore(Container.Object);
            Reads(Current, "current-etag");
            Container.Setup(c => c.CreateTransactionalBatch(new PartitionKey("tenant"))).Returns(Batch.Object);
            Batch.Setup(b => b.CreateItem(It.IsAny<ReviewSession>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
                .Returns(Batch.Object);
            Batch.Setup(b => b.CreateItem(It.IsAny<ReviewAuditEvent>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
                .Returns(Batch.Object);
            Batch.Setup(b => b.ReplaceItem(It.IsAny<string>(), It.IsAny<ReviewSession>(),
                It.IsAny<TransactionalBatchItemRequestOptions>())).Returns(Batch.Object);
            var response = new Mock<TransactionalBatchResponse>();
            response.SetupGet(r => r.IsSuccessStatusCode).Returns(true);
            Batch.Setup(b => b.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(response.Object);
        }

        public void Reads(ReviewSession session, string? version)
        {
            var response = new Mock<ItemResponse<ReviewSession>>();
            response.SetupGet(r => r.Resource).Returns(session);
            response.SetupGet(r => r.ETag).Returns(version!);
            Container.Setup(c => c.ReadItemAsync<ReviewSession>(Current.Id, new PartitionKey("tenant"),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(response.Object);
        }
    }
}
