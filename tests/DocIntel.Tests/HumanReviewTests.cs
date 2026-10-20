using DocIntel.Core.Authorization;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Jobs;
using DocIntel.Core.Review;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class HumanReviewTests
{
    private static readonly ActorIdentity First = new(Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Guid.Parse("20000000-0000-0000-0000-000000000001"));
    private static readonly ActorIdentity Second = First with { ObjectId = Guid.Parse("20000000-0000-0000-0000-000000000002") };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Corrections_Persist_Separately_From_Original_Model_Values_With_Verified_Audit(bool persistent)
    {
        // Arrange
        using var fixture = new Fixture(persistent);
        await fixture.CreateAsync();
        var claimed = await fixture.Service.ChangeAsync(fixture.Job, First, 1, "claim");

        // Act
        var corrected = await fixture.Service.ChangeAsync(fixture.Job, First, claimed.Version, "field",
            "entity:0:0", FieldReviewOutcome.Corrected, "Corrected", "Verified against original.");
        var restarted = new ReviewService(fixture.Reopen(), fixture.Clock);
        var recovered = await restarted.GetAsync(fixture.Job);

        // Assert
        Assert.Equal("Original", fixture.Job.Segments[0].ExtractedEntities[0].Value);
        Assert.Equal("Original", recovered!.Fields[0].OriginalValue);
        Assert.Equal("Corrected", recovered.Fields[0].CurrentValue);
        Assert.Equal(First, recovered.Fields[0].ReviewedBy);
        Assert.Equal(FieldReviewOutcome.Corrected, corrected.Fields[0].Outcome);
        ReviewEngine.VerifyHistory(recovered, await fixture.Reopen().GetAuditAsync("tenant", recovered.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_Edits_Do_Not_Modify_State_Or_Append_An_Audit_Event(bool persistent)
    {
        // Arrange
        using var fixture = new Fixture(persistent);
        await fixture.CreateAsync();
        var current = await fixture.Service.ChangeAsync(fixture.Job, First, 1, "claim");

        // Act / Assert
        await Assert.ThrowsAsync<ReviewConflictException>(() => fixture.Service.ChangeAsync(
            fixture.Job, First, 1, "field", "entity:0:0", FieldReviewOutcome.Approved));
        Assert.Equal(current.Version, (await fixture.Service.GetAsync(fixture.Job))!.Version);
        Assert.Equal(2, (await fixture.Store.GetAuditAsync("tenant", fixture.Job.ReviewId!)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_Claims_Have_One_Winner_And_No_Partial_State_Audit_Write(bool persistent)
    {
        // Arrange
        using var fixture = new Fixture(persistent);
        await fixture.CreateAsync();

        // Act
        var outcomes = await Task.WhenAll(new[] { First, Second }.Select(actor => Task.Run(async () =>
        {
            try
            {
                await new ReviewService(fixture.Reopen(), fixture.Clock).ChangeAsync(fixture.Job, actor, 1, "claim");
                return true;
            }
            catch (ReviewConflictException) { return false; }
        })));

        // Assert
        Assert.Single(outcomes, accepted => accepted);
        var current = (await fixture.Service.GetAsync(fixture.Job))!;
        Assert.Equal(2, current.Version);
        ReviewEngine.VerifyHistory(current, await fixture.Store.GetAuditAsync("tenant", current.Id));
    }

    [Fact]
    public async Task Expired_Claims_Cannot_Edit_And_A_Replacement_Reviewer_Can_Take_Over()
    {
        // Arrange
        using var fixture = new Fixture(true);
        await fixture.CreateAsync();
        var claimed = await fixture.Service.ChangeAsync(fixture.Job, First, 1, "claim");
        fixture.Clock.Advance(TimeSpan.FromMinutes(16));

        // Act / Assert
        await Assert.ThrowsAsync<ReviewConflictException>(() => fixture.Service.ChangeAsync(
            fixture.Job, First, claimed.Version, "field", "entity:0:0", FieldReviewOutcome.Approved));
        var replacement = await fixture.Service.ChangeAsync(fixture.Job, Second, claimed.Version, "claim");
        Assert.Equal(Second, replacement.ClaimedBy);
        await Assert.ThrowsAsync<ReviewConflictException>(() => fixture.Service.ChangeAsync(
            fixture.Job, First, replacement.Version, "field", "entity:0:0", FieldReviewOutcome.Approved));
    }

    [Fact]
    public async Task Dual_Review_Requires_Two_Distinct_Directory_Identities()
    {
        // Arrange
        using var fixture = new Fixture(true);
        await fixture.CreateAsync(dual: true);
        var claimed = await fixture.Service.ChangeAsync(fixture.Job, First, 1, "claim");
        var approved = await fixture.Service.ChangeAsync(fixture.Job, First, claimed.Version, "field",
            "entity:0:0", FieldReviewOutcome.Approved);
        var pending = await fixture.Service.ChangeAsync(fixture.Job, First, approved.Version, "submit");

        // Act / Assert
        Assert.Equal(ReviewStatus.AwaitingSecondReview, pending.Status);
        await Assert.ThrowsAsync<ReviewConflictException>(() =>
            fixture.Service.ChangeAsync(fixture.Job, First, pending.Version, "claim"));
        var second = await fixture.Service.ChangeAsync(fixture.Job, Second, pending.Version, "claim");
        await Assert.ThrowsAsync<ReviewConflictException>(() =>
            fixture.Service.ChangeAsync(fixture.Job, Second, second.Version, "field",
                "entity:0:0", FieldReviewOutcome.Corrected, "Changed", "Second reviewer correction."));
        var final = await fixture.Service.ChangeAsync(fixture.Job, Second, second.Version, "submit");
        Assert.Equal(ReviewStatus.Approved, final.Status);
        Assert.Equal(First, final.FirstReviewer);
        Assert.Equal(Second, final.SecondReviewer);
        ReviewEngine.VerifyHistory(final, await fixture.Store.GetAuditAsync("tenant", final.Id));
    }

    [Fact]
    public async Task Pending_Or_Rejected_Fields_Do_Not_Get_Implicitly_Approved_On_Submit()
    {
        // Arrange
        using var fixture = new Fixture(false);
        await fixture.CreateAsync();
        var claimed = await fixture.Service.ChangeAsync(fixture.Job, First, 1, "claim");

        // Act / Assert
        await Assert.ThrowsAsync<ReviewConflictException>(() =>
            fixture.Service.ChangeAsync(fixture.Job, First, claimed.Version, "submit"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ChangeAsync(fixture.Job, First,
            claimed.Version, "field", "entity:0:0", FieldReviewOutcome.Corrected, "Changed"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ChangeAsync(fixture.Job, First,
            claimed.Version, "field", "../another-field", FieldReviewOutcome.Approved));
        var rejected = await fixture.Service.ChangeAsync(fixture.Job, First, claimed.Version, "field",
            "entity:0:0", FieldReviewOutcome.Rejected, reason: "Not supported by the document.");
        await Assert.ThrowsAsync<ReviewConflictException>(() =>
            fixture.Service.ChangeAsync(fixture.Job, First, rejected.Version, "submit"));
    }

    [Fact]
    public async Task A_Source_Change_Invalidates_The_Old_Human_Review_Snapshot()
    {
        // Arrange
        using var fixture = new Fixture(true);
        await fixture.CreateAsync();
        fixture.Job.Segments[0].ExtractedEntities = [new ExtractedEntity("Name", "Changed", "Party", 0.9, 0)];

        // Act / Assert
        await Assert.ThrowsAsync<ReviewConflictException>(() => fixture.Service.GetAsync(fixture.Job));
    }

    [Fact]
    public async Task The_Maximum_Admitted_Field_Count_Can_Complete_Individual_Dual_Review()
    {
        // Arrange
        using var fixture = new Fixture(false);
        fixture.Job.Segments[0].ExtractedEntities = Enumerable.Range(0, 2_000)
            .Select(index => new ExtractedEntity($"Field{index}", $"Value{index}", "Test", 0.9, 0)).ToList();
        var now = fixture.Clock.GetUtcNow();
        var created = ReviewEngine.Create(fixture.Job, new ReviewPolicy { RequiresDualReview = true }, now);
        var current = created with
        {
            Status = ReviewStatus.InReview,
            ClaimedBy = First,
            ClaimUntilUtc = now.AddMinutes(15),
            Fields = created.Fields.Select(field => field with { Outcome = FieldReviewOutcome.Approved }).ToList(),
            Version = created.Fields.Count + 2,
            LastAuditHash = new string('A', 64)
        };
        fixture.Job.ReviewId = current.Id;
        var store = new Mock<IReviewStore>();
        store.Setup(s => s.GetAsync("tenant", current.Id, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<ReviewSession?>(current));
        store.Setup(s => s.SaveAsync(It.IsAny<ReviewSession>(), It.IsAny<long>(), It.IsAny<ReviewAuditEvent>(),
                It.IsAny<CancellationToken>()))
            .Callback<ReviewSession, long, ReviewAuditEvent, CancellationToken>((session, _, _, _) => current = session)
            .Returns(Task.CompletedTask);
        var service = new ReviewService(store.Object, fixture.Clock);

        // Act
        var first = await service.ChangeAsync(fixture.Job, First, current.Version, "submit");
        var second = await service.ChangeAsync(fixture.Job, Second, first.Version, "claim");
        var final = await service.ChangeAsync(fixture.Job, Second, second.Version, "submit");

        // Assert
        Assert.Equal(ReviewStatus.Approved, final.Status);
        Assert.Equal(2_005, final.Version);
        Assert.Equal(Second, final.SecondReviewer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reserved_Audit_Capacity_Allows_Terminal_Rejection_After_Editing_Is_Disabled(bool expired)
    {
        // Arrange
        using var fixture = new Fixture(false);
        var now = fixture.Clock.GetUtcNow();
        var current = ReviewEngine.Create(fixture.Job, new ReviewPolicy(), now) with
        {
            Status = ReviewStatus.InReview,
            ClaimedBy = First,
            ClaimUntilUtc = now.AddMinutes(expired ? -1 : 15),
            Version = 9_999,
            LastAuditHash = new string('A', 64)
        };
        fixture.Job.ReviewId = current.Id;
        var store = new Mock<IReviewStore>();
        store.Setup(s => s.GetAsync("tenant", current.Id, It.IsAny<CancellationToken>())).ReturnsAsync(current);
        store.Setup(s => s.SaveAsync(It.IsAny<ReviewSession>(), It.IsAny<long>(), It.IsAny<ReviewAuditEvent>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var service = new ReviewService(store.Object, fixture.Clock);

        // Act / Assert
        await Assert.ThrowsAsync<ReviewConflictException>(() => service.ChangeAsync(fixture.Job, First,
            current.Version, "field", "entity:0:0", FieldReviewOutcome.Approved));
        var final = await service.ChangeAsync(fixture.Job, expired ? Second : First, current.Version,
            "reject", reason: "Requires rework.");
        Assert.Equal(10_000, final.Version);
        Assert.Equal(ReviewStatus.Rejected, final.Status);
    }

    [Fact]
    public async Task Audit_Snapshot_Reads_Remain_Valid_When_A_Later_Mutation_Has_Committed()
    {
        // Arrange
        using var fixture = new Fixture(false);
        await fixture.CreateAsync();
        var captured = (await fixture.Service.GetAsync(fixture.Job))!;
        await fixture.Service.ChangeAsync(fixture.Job, First, captured.Version, "claim");
        var later = await fixture.Store.GetAuditAsync("tenant", captured.Id);

        // Act
        var snapshot = ReviewEngine.HistorySnapshot(captured, later);

        // Assert
        Assert.Single(snapshot);
        Assert.Equal(captured.LastAuditHash, snapshot[0].Hash);
    }

    [Fact]
    public async Task Audit_Content_Or_Chain_Tampering_Is_Detected()
    {
        // Arrange
        using var fixture = new Fixture(false);
        await fixture.CreateAsync();
        var current = (await fixture.Service.GetAsync(fixture.Job))!;
        var events = (await fixture.Store.GetAuditAsync("tenant", current.Id)).ToArray();

        // Act / Assert
        ReviewEngine.VerifyHistory(current, events);
        events[0] = events[0] with { Reason = "Tampered" };
        Assert.Throws<InvalidDataException>(() => ReviewEngine.VerifyHistory(current, events));
        Assert.Throws<InvalidDataException>(() => ReviewEngine.VerifyHistory(current, []));
        Assert.Null(await fixture.Store.GetAsync("other", current.Id));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly bool _persistent;
        private readonly string _path = Path.Combine(Path.GetTempPath(), "DocIntel.Review.Tests", Guid.NewGuid().ToString("N"));
        public Clock Clock { get; } = new();
        public IReviewStore Store { get; }
        public ReviewService Service { get; }
        public DocumentJob Job { get; } = new()
        {
            JobId = "one",
            TenantId = "tenant",
            TenantDisplayName = "Test",
            AppliedHarnessVersion = 1,
            FileName = "source.pdf",
            Status = JobStatus.Completed,
            OriginalSha256 = new string('A', 64),
            Segments = [new DocumentSlice { SegmentIndex = 0, StartPage = 1, EndPage = 1,
                ExtractedEntities = [new ExtractedEntity("Name", "Original", "Party", 0.99, 0)] }]
        };

        public Fixture(bool persistent)
        {
            _persistent = persistent;
            Store = persistent ? new SqliteJobStore(Path.Combine(_path, "jobs.sqlite")) : new InMemoryReviewStore();
            Service = new ReviewService(Store, Clock);
        }

        public async Task CreateAsync(bool dual = false) =>
            Job.ReviewId = await Service.CreateAsync(Job, new ReviewPolicy { RequiresDualReview = dual });
        public IReviewStore Reopen() => _persistent ? new SqliteJobStore(Path.Combine(_path, "jobs.sqlite")) : Store;
        public void Dispose()
        {
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
        }
    }
}
