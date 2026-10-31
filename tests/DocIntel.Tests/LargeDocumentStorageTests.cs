using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DocIntel.Core.Authorization;
using DocIntel.Core.Evidence;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Storage;
using Microsoft.Azure.Cosmos;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class LargeDocumentStorageTests
{
    [Fact]
    public async Task Large_Checkpoints_And_Final_Results_Roundtrip_Complete_Content_On_All_Job_Reads()
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        await fixture.InitializeAsync();
        await using var lease = await fixture.Store.TryAcquireJobAsync("tenant", "one");
        var job = lease!.Job;
        PopulateLargeContent(job);
        Assert.True(LargeDocumentCosmosFixture.SerializedSize(job) > 2 * 1024 * 1024);

        // Act
        await lease.SaveAsync();
        var checkpoint = fixture.Persisted.ContentSnapshot;
        var checkpointBytes = await fixture.Artifacts.GetDocumentContentAsync(fixture.Persisted);
        job.Status = JobStatus.Completed;
        job.CompletedAtUtc = DateTimeOffset.UtcNow;
        job.ProcessingResultSha256 = await fixture.Artifacts.SaveProcessingResultAsync(job);
        await lease.SaveAsync();
        await lease.DisposeAsync();
        var restarted = fixture.CreateStore();
        var point = (await restarted.GetJobAsync("one", tenantId: "tenant"))!;
        var unique = (await restarted.GetJobAsync("one"))!;
        var listed = Assert.Single(await restarted.ListJobsByTenantAsync("tenant"));

        // Assert
        Assert.Equal("DocumentContent", checkpoint!.Kind);
        Assert.Equal("InitialProcessing", fixture.Persisted.ContentSnapshot!.Kind);
        Assert.Equal(job.ProcessingResultSha256, fixture.Persisted.ContentSnapshot.Sha256);
        Assert.NotEqual(checkpoint.Sha256, fixture.Persisted.ContentSnapshot.Sha256);
        Assert.NotNull(checkpointBytes);
        Assert.True(fixture.Persisted.SearchTextIsPartial);
        Assert.True(fixture.Persisted.Segments[0].BodyText.Length <= 2_048);
        Assert.Null(fixture.Persisted.Segments[0].DataExtraction);
        Assert.Empty(fixture.Persisted.DataExtractions);
        Assert.Null(fixture.Persisted.Segments[0].ExtractedEntities[0].Provenance);
        AssertCompleteContent(job, point!);
        AssertCompleteContent(job, unique!);
        AssertCompleteContent(job, listed);
        Assert.Equal(fixture.Source, await fixture.Artifacts.GetOriginalPdfAsync("tenant", "one"));
        Assert.All(fixture.SerializedWrites, size => Assert.True(size < 2 * 1024 * 1024));
        Assert.Empty(await restarted.ListRecoverableJobsAsync(100));
        Assert.Null(await restarted.TryAcquireJobAsync("tenant", "one"));
        Assert.Equal(1, point!.ExecutionAttempts);
        Assert.Equal(await fixture.Artifacts.GetProcessingResultAsync(point),
            await fixture.Artifacts.GetDocumentContentAsync(point));
    }

    [Fact]
    public async Task Updated_Derived_Content_Does_Not_Overwrite_The_Immutable_Initial_Result()
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        fixture.Persisted = LargeDocumentCosmosFixture.Job("tenant", fixture.Source,
            submittedBy: new ActorIdentity(Guid.NewGuid(), Guid.NewGuid()));
        await using var lease = await fixture.Store.TryAcquireJobAsync("tenant", "one");
        PopulateLargeContent(lease!.Job);
        lease.Job.Status = JobStatus.Completed;
        lease.Job.CompletedAtUtc = DateTimeOffset.UtcNow;
        lease.Job.ProcessingResultSha256 = await fixture.Artifacts.SaveProcessingResultAsync(lease.Job);
        await lease.SaveAsync();
        await lease.DisposeAsync();
        var initial = await fixture.Artifacts.GetProcessingResultAsync(lease.Job);
        var changed = (await fixture.Store.GetJobAsync("one", tenantId: "tenant"))!;
        changed.Segments[0].TranslatedText += "\nNew complete translation tail.";
        var evaluated = changed.PromptColumnResults[0] with
        {
            ResultText = "Reevaluated complete prompt output " + new string('r', 150_000),
            EvaluatedAtUtc = DateTimeOffset.UtcNow
        };
        changed.PromptColumnResults = [evaluated];
        changed.Segments[0].PromptColumnResults = [evaluated];
        var revokedSubmitter = new Mock<ITenantAccessResolver>();
        revokedSubmitter.Setup(a => a.ResolveAsync(
                It.IsAny<ActorIdentity?>(), "tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentAccessScope.Denied);

        // Act
        await fixture.CreateStore(access: revokedSubmitter.Object).UpsertJobAsync(changed);
        var hydrated = (await fixture.CreateStore().GetJobAsync("one", tenantId: "tenant"))!;

        // Assert
        Assert.Equal("DocumentContent", hydrated.ContentSnapshot!.Kind);
        Assert.NotEqual(hydrated.ProcessingResultSha256, hydrated.ContentSnapshot.Sha256);
        Assert.EndsWith("New complete translation tail.", hydrated.Segments[0].TranslatedText!);
        Assert.Equal(initial, await fixture.Artifacts.GetProcessingResultAsync(hydrated));
        Assert.Equal(changed.Segments[0].TranslatedText, hydrated.Segments[0].TranslatedText);
        Assert.Equal(evaluated.ResultText, Assert.Single(hydrated.PromptColumnResults).ResultText);
        Assert.Equal(evaluated.ResultText, Assert.Single(hydrated.Segments[0].PromptColumnResults).ResultText);
        revokedSubmitter.Verify(a => a.ResolveAsync(
            It.IsAny<ActorIdentity?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("漢")]
    [InlineData("<")]
    public async Task Cosmos_Size_Is_Measured_In_Actual_Serialized_Bytes_Not_Text_Character_Count(string character)
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        await using var lease = await fixture.Store.TryAcquireJobAsync("tenant", "one");
        var text = String.Concat(Enumerable.Repeat(character, 450_000));
        lease!.Job.Segments = [new() { SegmentIndex = 0, StartPage = 1, EndPage = 1, BodyText = text }];
        Assert.True(text.Length < 2 * 1024 * 1024);
        Assert.True(LargeDocumentCosmosFixture.SerializedSize(lease.Job) > 2 * 1024 * 1024);

        // Act
        await lease.SaveAsync();
        await lease.DisposeAsync();
        var hydrated = (await fixture.CreateStore().GetJobAsync("one", tenantId: "tenant"))!;

        // Assert
        Assert.Equal(text, Assert.Single(hydrated.Segments).BodyText);
        Assert.NotNull(fixture.Persisted.ContentSnapshot);
        Assert.All(fixture.SerializedWrites, size => Assert.True(size < 2 * 1024 * 1024));
    }

    [Fact]
    public async Task Entity_And_Type_Queries_Remain_Complete_And_Preview_Search_Discloses_Missing_Tail_Coverage()
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        await using var lease = await fixture.Store.TryAcquireJobAsync("tenant", "one");
        PopulateLargeContent(lease!.Job);
        lease.Job.Status = JobStatus.Completed;
        lease.Job.CompletedAtUtc = DateTimeOffset.UtcNow;
        lease.Job.ProcessingResultSha256 = await fixture.Artifacts.SaveProcessingResultAsync(lease.Job);
        await lease.SaveAsync();
        await lease.DisposeAsync();

        // Act
        var entityMatch = await fixture.Store.SearchDocumentsAsync("tenant", new DocumentQuery
        {
            DocumentType = "Invoice",
            Entities = [new("Customer Name", "Complete searchable customer")]
        }.WithAccess(new HashSet<string> { "one" }));
        var denied = await fixture.Store.SearchDocumentsAsync("tenant",
            new DocumentQuery().WithAccess(new HashSet<string>()));
        var preview = await fixture.Store.SearchDocumentsAsync("tenant", new() { SearchText = "Leading source" });
        var tail = await fixture.Store.SearchDocumentsAsync("tenant", new() { SearchText = "FINAL-PAGE-EVIDENCE" });

        // Assert
        Assert.Equal("one", Assert.Single(entityMatch.Documents).JobId);
        Assert.Empty(denied.Documents);
        Assert.Single(preview.Documents);
        Assert.Empty(tail.Documents);
        Assert.Contains("bounded previews", tail.TextSearchNotice);
        Assert.Contains("full evidence", preview.TextSearchNotice);
        Assert.Null(entityMatch.TextSearchNotice);
        Assert.Contains("FINAL-PAGE-EVIDENCE",
            (await fixture.Store.GetJobAsync("one", tenantId: "tenant"))!.Segments[0].BodyText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_Acknowledged_Artifact_Cannot_Be_Published_By_A_Replaced_Or_Expired_Owner(bool expired)
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        var artifacts = new Mock<IArtifactStore>();
        artifacts.Setup(a => a.SaveDocumentContentAsync(It.IsAny<DocumentJob>(), It.IsAny<CancellationToken>()))
            .Returns<DocumentJob, CancellationToken>(async (job, ct) =>
            {
                var reference = await fixture.Artifacts.SaveDocumentContentAsync(job, ct);
                if (expired) fixture.Clock.Advance(TimeSpan.FromSeconds(61));
                else
                {
                    fixture.Persisted.ProcessingOwner = "replacement-owner";
                    fixture.Version = "replacement-version";
                }
                return reference;
            });
        var store = fixture.CreateStore(artifacts.Object);
        await using var lease = await store.TryAcquireJobAsync("tenant", "one");
        PopulateLargeContent(lease!.Job);
        var writes = fixture.WrittenJobs.Count;

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.SaveAsync());
        Assert.True(lease.OwnershipLostToken.IsCancellationRequested);
        Assert.Null(fixture.Persisted.ContentSnapshot);
        Assert.Equal(writes, fixture.WrittenJobs.Count);
        await lease.DisposeAsync();
        if (!expired) Assert.Equal("replacement-owner", fixture.Persisted.ProcessingOwner);
    }

    [Fact]
    public async Task An_Invalid_Artifact_Acknowledgement_Publishes_No_Reference()
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        var artifacts = new Mock<IArtifactStore>();
        artifacts.Setup(a => a.SaveDocumentContentAsync(It.IsAny<DocumentJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentContentReference(
                new string('A', 64), 100, new string('B', 64), "DocumentContent"));
        await using var lease = await fixture.CreateStore(artifacts.Object).TryAcquireJobAsync("tenant", "one");
        PopulateLargeContent(lease!.Job);
        var writes = fixture.WrittenJobs.Count;

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => lease.SaveAsync());
        Assert.Null(fixture.Persisted.ContentSnapshot);
        Assert.Equal(writes, fixture.WrittenJobs.Count);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Revoked_Access_After_Artifact_Ack_Stops_Publication_But_Allows_A_Fenced_Failure()
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        var allowed = true;
        var access = new Mock<ITenantAccessResolver>();
        access.Setup(a => a.ResolveAsync(It.IsAny<ActorIdentity?>(), "tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => allowed ? DocumentAccessScope.Unrestricted : DocumentAccessScope.Denied);
        var artifacts = new Mock<IArtifactStore>();
        artifacts.Setup(a => a.SaveDocumentContentAsync(It.IsAny<DocumentJob>(), It.IsAny<CancellationToken>()))
            .Returns<DocumentJob, CancellationToken>(async (job, ct) =>
            {
                var reference = await fixture.Artifacts.SaveDocumentContentAsync(job, ct);
                allowed = false;
                return reference;
            });
        var store = fixture.CreateStore(artifacts.Object, access.Object);
        await using var lease = await store.TryAcquireJobAsync("tenant", "one");
        PopulateLargeContent(lease!.Job);
        var writes = fixture.WrittenJobs.Count;

        // Act
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => lease.SaveAsync());
        Assert.Null(fixture.Persisted.ContentSnapshot);
        Assert.Equal(writes, fixture.WrittenJobs.Count);
        lease.Job.Status = JobStatus.Failed;
        lease.Job.ErrorMessage = "Document processing access was revoked.";
        lease.Job.CompletedAtUtc = DateTimeOffset.UtcNow;
        await lease.SaveAsync();
        await lease.DisposeAsync();

        // Assert
        Assert.False(lease.OwnershipLostToken.IsCancellationRequested);
        Assert.Equal(JobStatus.Failed, fixture.Persisted.Status);
        Assert.Empty(await fixture.Store.ListRecoverableJobsAsync(100));
        Assert.Null(await fixture.Store.TryAcquireJobAsync("tenant", "one"));
        Assert.Contains("FINAL-PAGE-EVIDENCE",
            (await fixture.Store.GetJobAsync("one", tenantId: "tenant"))!.Segments[0].BodyText);
    }

    [Fact]
    public async Task Required_Search_Metadata_Overflow_Is_Durably_Terminal_Not_Ownership_Loss_Or_Recovery()
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        await using var lease = await fixture.Store.TryAcquireJobAsync("tenant", "one");
        var value = new string('v', 2 * 1024 * 1024);
        lease!.Job.Segments =
        [
            new() { SegmentIndex = 0, StartPage = 1, EndPage = 200,
                BodyText = "Complete original source", ExtractedEntities = [new("Required entity", value, "Business")] }
        ];
        lease.Job.TotalPages = 200;

        // Act
        var failure = await Assert.ThrowsAsync<DocumentStorageLimitException>(() => lease.SaveAsync());
        var hydrated = (await fixture.CreateStore().GetJobAsync("one", tenantId: "tenant"))!;
        await lease.DisposeAsync();

        // Assert
        Assert.Equal(DocumentStorageLimitException.SearchMetadataTooLarge, failure.Code);
        Assert.True(failure.IsDurablyPersisted);
        Assert.False(lease.OwnershipLostToken.IsCancellationRequested);
        Assert.Equal(JobStatus.Failed, hydrated.Status);
        Assert.Equal(failure.Code, hydrated.ErrorCode);
        Assert.True(hydrated.SearchMetadataUnavailable);
        Assert.Equal(value, Assert.Single(hydrated.Segments).ExtractedEntities[0].Value);
        Assert.Empty(fixture.Persisted.Segments);
        Assert.NotNull(fixture.Persisted.ContentSnapshot);
        Assert.All(fixture.SerializedWrites, size => Assert.True(size < 2 * 1024 * 1024));
        Assert.Empty(await fixture.Store.ListRecoverableJobsAsync(100));
        Assert.Null(await fixture.Store.TryAcquireJobAsync("tenant", "one"));
        Assert.Equal(1, fixture.Persisted.ExecutionAttempts);
    }

    [Fact]
    public async Task Cosmos_Payload_Rejection_Is_A_Fenced_Terminal_Limit_Not_A_Recoverable_Lease_Conflict()
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        await using var lease = await fixture.Store.TryAcquireJobAsync("tenant", "one");
        lease!.Job.Segments = [new() { SegmentIndex = 0, StartPage = 1, EndPage = 1, BodyText = "Retained source" }];
        fixture.RejectNextPayload = true;

        // Act
        var failure = await Assert.ThrowsAsync<DocumentStorageLimitException>(() => lease.SaveAsync());
        await lease.DisposeAsync();
        var recovered = (await fixture.CreateStore().GetJobAsync("one", tenantId: "tenant"))!;

        // Assert
        Assert.Equal(DocumentStorageLimitException.CosmosPayloadTooLarge, failure.Code);
        Assert.True(failure.IsDurablyPersisted);
        Assert.False(lease.OwnershipLostToken.IsCancellationRequested);
        Assert.Equal(JobStatus.Failed, recovered.Status);
        Assert.Equal("Retained source", Assert.Single(recovered.Segments).BodyText);
        Assert.Empty(await fixture.Store.ListRecoverableJobsAsync(100));
        Assert.Null(await fixture.Store.TryAcquireJobAsync("tenant", "one"));
        Assert.Equal(1, recovered.ExecutionAttempts);
    }

    [Fact]
    public async Task A_Complete_Content_Archive_Limit_Is_Explicit_Terminal_And_Does_Not_Advertise_Truncated_Evidence()
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        await fixture.InitializeAsync();
        var artifacts = new Mock<IArtifactStore>();
        artifacts.Setup(a => a.SaveDocumentContentAsync(It.IsAny<DocumentJob>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DocumentStorageLimitException(DocumentStorageLimitException.ContentTooLarge,
                "Complete output exceeds its archive limit."));
        await using var lease = await fixture.CreateStore(artifacts.Object).TryAcquireJobAsync("tenant", "one");
        PopulateLargeContent(lease!.Job);

        // Act
        var failure = await Assert.ThrowsAsync<DocumentStorageLimitException>(() => lease.SaveAsync());
        var failed = (await fixture.Store.GetJobAsync("one", tenantId: "tenant"))!;

        // Assert
        Assert.Equal(DocumentStorageLimitException.ContentTooLarge, failure.Code);
        Assert.True(failure.IsDurablyPersisted);
        Assert.False(lease.OwnershipLostToken.IsCancellationRequested);
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Null(failed.ContentSnapshot);
        Assert.Empty(failed.Segments);
        Assert.Equal(fixture.Source, await fixture.Artifacts.GetOriginalPdfAsync("tenant", "one"));
        Assert.Empty(await fixture.Store.ListRecoverableJobsAsync(100));
        Assert.Null(await fixture.Store.TryAcquireJobAsync("tenant", "one"));
        Assert.Equal(1, failed.ExecutionAttempts);
    }

    [Fact]
    public async Task Missing_Or_Corrupt_Content_Fails_Closed_On_Point_Unique_And_Tenant_List_Reads()
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        await using var lease = await fixture.Store.TryAcquireJobAsync("tenant", "one");
        PopulateLargeContent(lease!.Job);
        await lease.SaveAsync();
        await lease.DisposeAsync();
        var reference = fixture.Persisted.ContentSnapshot!;
        var path = Path.Combine(fixture.ArtifactDirectory, "tenant-tenant", "one-a1",
            $"content-{reference.Sha256}.json");

        // Act / Assert
        File.Delete(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.GetJobAsync("one", tenantId: "tenant"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.GetJobAsync("one"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.ListJobsByTenantAsync("tenant"));
        await File.WriteAllTextAsync(path, "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.GetJobAsync("one", tenantId: "tenant"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.GetJobAsync("one"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.ListJobsByTenantAsync("tenant"));
    }

    [Fact]
    public async Task Wrong_Tenant_And_Ambiguous_Document_Ids_Are_Rejected_Before_Content_Is_Read()
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        var content = new Mock<IArtifactStore>(MockBehavior.Strict);
        await using var lease = await fixture.Store.TryAcquireJobAsync("tenant", "one");
        PopulateLargeContent(lease!.Job);
        await lease.SaveAsync();
        await lease.DisposeAsync();
        var store = fixture.CreateStore(content.Object);

        // Act / Assert
        Assert.Null(await store.GetJobAsync("one", tenantId: "other"));
        fixture.QueryRows = [fixture.Persisted, LargeDocumentCosmosFixture.Job("other", fixture.Source)];
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetJobAsync("one"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ListJobsByTenantAsync("tenant"));
        content.Verify(a => a.GetDocumentContentAsync(
            It.IsAny<DocumentJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Soft_Deletion_Preserves_Full_Archived_Content_And_Cannot_Be_Reversed_By_Stale_Metadata()
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        await using var lease = await fixture.Store.TryAcquireJobAsync("tenant", "one");
        PopulateLargeContent(lease!.Job);
        lease.Job.Status = JobStatus.Completed;
        lease.Job.CompletedAtUtc = DateTimeOffset.UtcNow;
        lease.Job.ProcessingResultSha256 = await fixture.Artifacts.SaveProcessingResultAsync(lease.Job);
        await lease.SaveAsync();
        await lease.DisposeAsync();
        var stale = (await fixture.Store.GetJobAsync("one", tenantId: "tenant"))!;
        var deleted = (await fixture.Store.GetJobAsync("one", tenantId: "tenant"))!;
        deleted.Status = JobStatus.Deleted;

        // Act
        await fixture.Store.UpsertJobAsync(deleted);

        // Assert
        Assert.Equal(JobStatus.Deleted, fixture.Persisted.Status);
        Assert.NotNull(await fixture.Artifacts.GetDocumentContentAsync(fixture.Persisted));
        Assert.Null(await fixture.Store.GetJobAsync("one", tenantId: "tenant"));
        await Assert.ThrowsAsync<CosmosException>(() => fixture.Store.UpsertJobAsync(stale));
        Assert.Equal(JobStatus.Deleted, fixture.Persisted.Status);
        Assert.Null(await fixture.Store.TryAcquireJobAsync("tenant", "one"));
    }

    private static void PopulateLargeContent(DocumentJob job)
    {
        job.Status = JobStatus.Running;
        job.TotalPages = 200;
        job.PacketSummary = "Packet summary\n" + new string('s', 20_000);
        var provenance = new ValueProvenance(GroundingStatus.ExactTextMatch, ["p200-w1"], [],
            "FINAL-PAGE-EVIDENCE");
        var entity = new ExtractedEntity("Customer Name", "Complete searchable customer", "Business",
            SourceSliceIndex: 0, Provenance: provenance);
        var extraction = new DocumentDataExtractionResult("Invoice", "Footer-200", "1-200",
            [new("Full field", new string('f', 50_000), Provenance: provenance)],
            [new("Complete table", [new([new("Tail column", "Every complete row")])])]);
        var prompt = new PromptColumnResult("column", "Full result", new string('a', 100_000),
            "Matched customer", true, ["Customer Name"], "Completed", new string('p', 150_000),
            false, "source.pdf", 100, String.Empty, "model", 0, "Invoice", "1-200", DateTimeOffset.UtcNow);
        job.ExtractedEntities = [entity];
        job.DataExtractions = [extraction];
        job.PromptColumnResults = [prompt];
        job.ExecutionTrace = [new("Complete stage", true, true, null, 1, new string('n', 30_000))];
        job.Segments =
        [
            new()
            {
                SegmentIndex = 0, StartPage = 1, EndPage = 200, IdentifiedDocumentName = "Invoice",
                Footers = ["Every original footer"], ExtractedEntities = [entity], DataExtraction = extraction,
                PromptColumnResults = [prompt], BodyText = "Leading source\n" + new string('b', 750_000) +
                    "\nFINAL-PAGE-EVIDENCE",
                Markdown = "# Complete Markdown\n" + new string('m', 750_000) + "\nFINAL-MARKDOWN-TAIL",
                TranslatedText = "Complete translation\n" + new string('t', 750_000) + "\nFINAL-TRANSLATION-TAIL",
                InsightSummary = new string('i', 50_000), WasTranslated = true, DetectedLanguage = "fr",
                TargetLanguage = "en"
            }
        ];
    }

    private static void AssertCompleteContent(DocumentJob expected, DocumentJob actual)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(expected.TotalPages, actual.TotalPages);
        Assert.Equal(expected.PacketSummary, actual.PacketSummary);
        Assert.Equal(JsonSerializer.Serialize(expected.ProcessingHarness, options),
            JsonSerializer.Serialize(actual.ProcessingHarness, options));
        Assert.Equal(JsonSerializer.Serialize(expected.Segments, options),
            JsonSerializer.Serialize(actual.Segments, options));
        Assert.Equal(JsonSerializer.Serialize(expected.DataExtractions, options),
            JsonSerializer.Serialize(actual.DataExtractions, options));
        Assert.Equal(JsonSerializer.Serialize(expected.PromptColumnResults, options),
            JsonSerializer.Serialize(actual.PromptColumnResults, options));
        Assert.Equal(JsonSerializer.Serialize(expected.ExtractedEntities, options),
            JsonSerializer.Serialize(actual.ExtractedEntities, options));
        Assert.Equal(expected.ExecutionTrace, actual.ExecutionTrace);
    }
}

internal sealed class LargeDocumentCosmosFixture : IDisposable
{
    public string ArtifactDirectory { get; } = Path.Combine(Directory.GetCurrentDirectory(), "TestOutput",
        "LargeDocumentStorage", Guid.NewGuid().ToString("N"));
    public Mock<Container> Container { get; } = new();
    public TestClock Clock { get; } = new();
    public LocalFileArtifactStore Artifacts { get; }
    public byte[] Source { get; }
    public DocumentJob Persisted { get; set; }
    public string Version { get; set; } = "v0";
    public bool RejectNextPayload { get; set; }
    public List<DocumentJob> WrittenJobs { get; } = [];
    public List<int> SerializedWrites { get; } = [];
    public IReadOnlyList<DocumentJob>? QueryRows { get; set; }
    public CosmosJobStore Store { get; }

    public LargeDocumentCosmosFixture(byte[]? source = null, TenantHarness? tenant = null)
    {
        Source = source ?? PdfPigLayoutExtractor.CreateSamplePdf([("Accepted source evidence", "Source")]);
        Artifacts = new LocalFileArtifactStore(ArtifactDirectory);
        Persisted = Job("tenant", Source, tenant);
        Container.Setup(c => c.ReadItemAsync<DocumentJob>(It.IsAny<string>(), It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, PartitionKey, ItemRequestOptions, CancellationToken>((id, partition, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (id != Persisted.JobId || !partition.Equals(new PartitionKey(Persisted.TenantId)))
                    throw Error(HttpStatusCode.NotFound);
                return Task.FromResult(Response(Persisted, Version));
            });
        Container.Setup(c => c.ReplaceItemAsync(It.IsAny<DocumentJob>(), It.IsAny<string>(), It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .Returns<DocumentJob, string, PartitionKey, ItemRequestOptions, CancellationToken>(
                (job, id, partition, options, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                SerializedWrites.Add(SerializedSize(job));
                if (options.IfMatchEtag != Version) throw Error(HttpStatusCode.PreconditionFailed);
                if (SerializedSize(job) >= 2 * 1024 * 1024 || RejectNextPayload)
                {
                    RejectNextPayload = false;
                    throw Error(HttpStatusCode.RequestEntityTooLarge);
                }
                if (id != Persisted.JobId || !partition.Equals(new PartitionKey(Persisted.TenantId)))
                    throw Error(HttpStatusCode.NotFound);
                Persisted = Clone(job);
                WrittenJobs.Add(Clone(job));
                Version = $"v{WrittenJobs.Count}";
                return Task.FromResult(Response(Persisted, Version));
            });
        Container.Setup(c => c.GetItemQueryIterator<DocumentJob>(It.IsAny<QueryDefinition>(),
                It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns<QueryDefinition, string, QueryRequestOptions>((_, _, _) =>
                Feed((QueryRows ?? [Persisted]).Where(j => j.Status != JobStatus.Deleted).Select(Clone).ToList()));
        Container.Setup(c => c.GetItemQueryIterator<JobQueueTicket>(It.IsAny<QueryDefinition>(),
                It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns<QueryDefinition, string, QueryRequestOptions>((_, _, _) => Feed<JobQueueTicket>(
                Persisted.Status is JobStatus.Queued or JobStatus.Running &&
                (Persisted.ProcessingOwner is null || Persisted.ProcessingLeaseUntilUtc <= Clock.GetUtcNow())
                    ? [new JobQueueTicket(Persisted.JobId, Persisted.TenantId)] : []));
        Container.Setup(c => c.GetItemQueryIterator<DocumentReference>(It.IsAny<QueryDefinition>(),
                It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns<QueryDefinition, string, QueryRequestOptions>((query, _, _) =>
            {
                var parameters = query.GetQueryParameters().ToDictionary(p => p.Name, p => p.Value);
                var plan = new DocumentQuery
                {
                    SearchText = parameters.GetValueOrDefault("@text") as string,
                    DocumentType = parameters.GetValueOrDefault("@documentType") as string,
                    Entities = parameters.ContainsKey("@name0")
                        ? [new((string)parameters["@name0"], (string)parameters["@value0"])] : []
                };
                if (parameters.TryGetValue("@authorizedJobIds", out var ids))
                    plan = plan.WithAccess(((string[])ids).ToHashSet());
                var matches = DocumentQueryCompiler.Matches(Persisted, (string)parameters["@tenantId"], plan);
                return Feed<DocumentReference>(matches
                    ? [new(Persisted.JobId, Persisted.FileName, Persisted.TotalPages, Persisted.Segments.Count)]
                    : []);
            });
        Store = CreateStore();
    }

    public Task InitializeAsync() => Artifacts.SaveOriginalPdfAsync("tenant", "one", Source);

    public CosmosJobStore CreateStore(IArtifactStore? artifacts = null, ITenantAccessResolver? access = null) =>
        new(Container.Object, Clock, artifacts: artifacts ?? Artifacts, access: access);

    public static DocumentJob Job(string tenantId, byte[] source, TenantHarness? tenant = null,
        ActorIdentity? submittedBy = null) => new()
    {
        JobId = "one", TenantId = tenantId, TenantDisplayName = "Test", FileName = "source.pdf",
        AppliedHarnessVersion = tenant?.HarnessVersion ?? 1,
        ProcessingHarness = tenant ?? new TenantHarness
            { TenantId = tenantId, DisplayName = "Test", Capabilities = [] },
        SubmittedBy = submittedBy,
        OriginalSha256 = Convert.ToHexString(SHA256.HashData(source))
    };

    public static int SerializedSize(DocumentJob job)
    {
        using var stream = new SystemTextJsonCosmosSerializer().ToStream(job);
        return checked((int)stream.Length);
    }

    private static DocumentJob Clone(DocumentJob job)
    {
        var serializer = new SystemTextJsonCosmosSerializer();
        return serializer.FromStream<DocumentJob>(serializer.ToStream(job));
    }

    private static ItemResponse<DocumentJob> Response(DocumentJob job, string version)
    {
        var response = new Mock<ItemResponse<DocumentJob>>();
        response.SetupGet(r => r.Resource).Returns(Clone(job));
        response.SetupGet(r => r.ETag).Returns(version);
        return response.Object;
    }

    private static FeedIterator<T> Feed<T>(IReadOnlyList<T> rows)
    {
        var consumed = false;
        var page = new Mock<FeedResponse<T>>();
        page.Setup(r => r.GetEnumerator()).Returns(() => rows.GetEnumerator());
        var iterator = new Mock<FeedIterator<T>>();
        iterator.SetupGet(i => i.HasMoreResults).Returns(() => !consumed);
        iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).Returns<CancellationToken>(ct =>
        {
            ct.ThrowIfCancellationRequested();
            consumed = true;
            return Task.FromResult(page.Object);
        });
        return iterator.Object;
    }

    private static CosmosException Error(HttpStatusCode status) => new("Test storage failure.", status, 0, "test", 0);

    public void Dispose()
    {
        if (Directory.Exists(ArtifactDirectory)) Directory.Delete(ArtifactDirectory, recursive: true);
    }

    public sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
