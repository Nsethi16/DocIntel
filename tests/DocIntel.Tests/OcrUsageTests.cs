using System.Globalization;
using DocIntel.Core.Evidence;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Ocr;
using DocIntel.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class OcrUsageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Allowance_Counters_Are_Tenant_And_Month_Scoped(bool persistent)
    {
        // Arrange
        using var fixture = new Fixture(persistent);
        var store = fixture.Store;
        var budget = await store.GetBudgetAsync("tenant");
        Assert.Equal(100_000, budget.MonthlyPageLimit);
        await store.ReserveAsync("tenant", "2026-10", Request(), 3, "OCR", "one-a1");

        // Act / Assert
        Assert.Equal(3, (await fixture.Reopen().GetUsageAsync("tenant", "2026-10")).ReservedPages);
        Assert.Equal(0, (await store.GetUsageAsync("other", "2026-10")).ReservedPages);
        Assert.Equal(0, (await store.GetUsageAsync("tenant", "2026-11")).ReservedPages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replayed_Reservation_Does_Not_Count_Twice_Or_Authorize_Different_Work(bool persistent)
    {
        // Arrange
        using var fixture = new Fixture(persistent);
        var id = Request();
        await fixture.Store.ReserveAsync("tenant", "2026-10", id, 3, "OCR", "one-a1");

        // Act
        var replay = await fixture.Reopen().ReserveAsync("tenant", "2026-10", id, 3, "OCR", "one-a1");

        // Assert
        Assert.True(replay.Replayed);
        Assert.Equal(3, replay.Usage.ReservedPages);
        Assert.Equal(1, replay.Usage.RequestCount);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Store.ReserveAsync("tenant", "2026-10", id, 4, "OCR", "one-a1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_Operational_Limits_Are_Not_Overwritten_By_Stale_Admin_Reads(bool persistent)
    {
        // Arrange
        using var fixture = new Fixture(persistent);
        var first = await fixture.Store.GetBudgetAsync("tenant");
        var updated = await fixture.Store.UpdateBudgetAsync("tenant", first with { MonthlyPageLimit = 1 });

        // Act / Assert
        await Assert.ThrowsAsync<OcrBudgetConflictException>(() =>
            fixture.Store.UpdateBudgetAsync("tenant", first with { MonthlyPageLimit = 100 }));
        await fixture.Store.ReserveAsync("tenant", "2026-10", Request(), 1, "OCR", "one-a1");
        await Assert.ThrowsAsync<OcrBudgetExceededException>(() =>
            fixture.Store.ReserveAsync("tenant", "2026-10", Request(), 1, "OCR", "two-a1"));
        Assert.Equal(updated, await fixture.Reopen().GetBudgetAsync("tenant"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_Allocations_Cannot_Exceed_The_Live_Monthly_Limit(bool persistent)
    {
        // Arrange
        using var fixture = new Fixture(persistent);
        var budget = await fixture.Store.GetBudgetAsync("tenant");
        await fixture.Store.UpdateBudgetAsync("tenant", budget with { MonthlyPageLimit = 10 });

        // Act
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => Task.Run(async () =>
        {
            try
            {
                await fixture.Reopen().ReserveAsync("tenant", "2026-10", Request(), 1, "OCR", "one");
                return true;
            }
            catch (OcrBudgetExceededException) { return false; }
        })));

        // Assert
        Assert.Equal(10, outcomes.Count(accepted => accepted));
        Assert.Equal(10, (await fixture.Store.GetUsageAsync("tenant", "2026-10")).ReservedPages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_Inputs_And_Cancellation_Do_Not_Allocate_Pages(bool persistent)
    {
        // Arrange
        using var fixture = new Fixture(persistent);
        var budget = await fixture.Store.GetBudgetAsync("tenant");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Store.ReserveAsync("tenant", "2026-99", Request(), 1, "OCR", "one"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Store.UpdateBudgetAsync("tenant", budget with { MonthlyPageLimit = -1 }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Store.ReserveAsync("tenant", "2026-10", Request(), 1, "OCR", "one", canceled.Token));
        Assert.Equal(0, (await fixture.Store.GetUsageAsync("tenant", "2026-10")).ReservedPages);
    }

    [Fact]
    public async Task Budget_Rejection_Precedes_Provider_Invocation()
    {
        // Arrange
        var store = new InMemoryOcrUsageStore();
        await store.UpdateBudgetAsync("tenant", (await store.GetBudgetAsync("tenant")) with { MonthlyPageLimit = 0 });
        var provider = Provider();

        // Act / Assert
        await Assert.ThrowsAsync<OcrBudgetExceededException>(() => new OcrProcessor(
            NullLogger<OcrProcessor>.Instance, provider.Object, usage: store).EnrichAsync(State()));
        provider.Verify(p => p.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<int>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Failed_Provider_Requests_Keep_Their_Conservative_Reservation()
    {
        // Arrange
        var store = new InMemoryOcrUsageStore();
        var clock = new TestClock();
        var provider = Provider();
        provider.Setup(p => p.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Unknown provider outcome."));

        // Act / Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => new OcrProcessor(
            NullLogger<OcrProcessor>.Instance, provider.Object, usage: store, clock: clock).EnrichAsync(State()));
        Assert.Equal(1, (await store.GetUsageAsync("tenant", "2026-10")).ReservedPages);
        clock.UtcNow = DateTimeOffset.Parse("2026-11-01T00:00:00Z", CultureInfo.InvariantCulture);
        await Assert.ThrowsAsync<HttpRequestException>(() => new OcrProcessor(
            NullLogger<OcrProcessor>.Instance, provider.Object, usage: store, clock: clock).EnrichAsync(State()));
        Assert.Equal(1, (await store.GetUsageAsync("tenant", "2026-11")).ReservedPages);
    }

    [Fact]
    public async Task Cached_Pages_Do_Not_Consume_Allowance_And_Work_After_The_Limit_Is_Zero()
    {
        // Arrange
        using var fixture = new Fixture(true);
        var store = fixture.Store;
        var provider = Provider();
        provider.SetupGet(p => p.CacheIdentity).Returns("provider:v1");
        var artifacts = new LocalFileArtifactStore(Path.Combine(fixture.Directory, "artifacts"));
        var clock = new TestClock();
        var processor = new OcrProcessor(NullLogger<OcrProcessor>.Instance, provider.Object, artifacts, store, clock);
        await processor.EnrichAsync(State());
        await store.UpdateBudgetAsync("tenant", (await store.GetBudgetAsync("tenant")) with { MonthlyPageLimit = 0 });
        var next = State();

        // Act
        await processor.EnrichAsync(next);

        // Assert
        Assert.True(next.Pages[0].OcrCacheHit);
        Assert.Equal(1, (await store.GetUsageAsync("tenant", "2026-10")).ReservedPages);
        provider.Verify(p => p.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<int>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static string Request() => Guid.NewGuid().ToString("N");
    private static Mock<IOcrProvider> Provider()
    {
        var provider = new Mock<IOcrProvider>();
        provider.SetupGet(p => p.Name).Returns("TestOCR");
        provider.Setup(p => p.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OcrPageResult(1, 8.5, 11,
            [new PageWord("w_1_0", "Text", new BoundingBox(1, [0.1, 0.1, 0.2, 0.1, 0.2, 0.2, 0.1, 0.2]), 0.8)], "TestOCR")]);
        return provider;
    }
    private static SplittingState State() => new()
    {
        Tenant = new TenantHarness { TenantId = "tenant", DisplayName = "Test" },
        DocumentId = "one-a1",
        FileName = "source.pdf",
        SourcePdfBytes = "%PDF-source"u8.ToArray(),
        Pages = [new PageLayout { PageNumber = 1, PageWidth = 612, PageHeight = 792, ImageAreaRatio = 1 }]
    };

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-31T23:59:59Z", CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly bool _persistent;
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "DocIntel.OcrUsage.Tests", Guid.NewGuid().ToString("N"));
        public IOcrUsageStore Store { get; }
        public Fixture(bool persistent)
        {
            _persistent = persistent;
            Store = persistent ? new SqliteJobStore(Path.Combine(Directory, "jobs.sqlite")) : new InMemoryOcrUsageStore();
        }
        public IOcrUsageStore Reopen() => _persistent ? new SqliteJobStore(Path.Combine(Directory, "jobs.sqlite")) : Store;
        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
