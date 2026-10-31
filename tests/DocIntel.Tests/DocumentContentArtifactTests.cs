using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Jobs;
using DocIntel.Core.Storage;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class DocumentContentArtifactTests
{
    [Fact]
    public async Task Checkpoints_Are_Content_Addressed_Immutable_And_Idempotent_Only_After_Verifying_Existing_Bytes()
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        var job = ContentJob(fixture);

        // Act
        var first = await fixture.Artifacts.SaveDocumentContentAsync(job);
        job.ContentSnapshot = first;
        var identical = await new LocalFileArtifactStore(fixture.ArtifactDirectory).SaveDocumentContentAsync(job);
        var firstBytes = await fixture.Artifacts.GetDocumentContentAsync(job);
        job.Segments[0].BodyText += "\nNew tail.";
        var next = await fixture.Artifacts.SaveDocumentContentAsync(job);
        var firstPath = ContentPath(fixture, first);
        await File.WriteAllTextAsync(firstPath, "{}");
        job.Segments[0].BodyText = "Complete source and tail.";

        // Assert
        Assert.Equal(first, identical);
        Assert.NotEqual(first.Sha256, next.Sha256);
        Assert.NotNull(firstBytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Artifacts.SaveDocumentContentAsync(job));
        Assert.Empty(Directory.GetFiles(fixture.ArtifactDirectory, "*.upload", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("document")]
    [InlineData("source")]
    [InlineData("layout")]
    [InlineData("attempt")]
    [InlineData("attempt-counter")]
    [InlineData("harness-version")]
    [InlineData("harness-content")]
    [InlineData("content-hash")]
    public async Task A_Copied_Content_Snapshot_Is_Rejected_For_Any_Source_Harness_Or_Attempt_Mismatch(string mismatch)
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        var original = ContentJob(fixture);
        original.ContentSnapshot = await fixture.Artifacts.SaveDocumentContentAsync(original);
        var node = JsonSerializer.SerializeToNode(
            original, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        if (mismatch == "tenant") node["tenantId"] = "other";
        if (mismatch == "document")
        {
            node["id"] = "other";
            node["jobId"] = "other";
            node["artifactDocumentId"] = "other-a1";
        }
        if (mismatch == "source") node["originalSha256"] = new string('B', 64);
        if (mismatch == "layout") node["layoutSha256"] = new string('B', 64);
        if (mismatch is "attempt" or "attempt-counter") node["executionAttempts"] = 2;
        if (mismatch == "attempt") node["artifactDocumentId"] = "one-a2";
        if (mismatch == "harness-version") node["appliedHarnessVersion"] = 2;
        if (mismatch == "harness-content") node["processingHarness"]!["defaultModelId"] = "changed-accepted-config";
        if (mismatch == "content-hash") node["contentSnapshot"]!["sha256"] = new string('B', 64);
        if (mismatch == "tenant") node["processingHarness"]!["tenantId"] = "other";
        var request = node.Deserialize<DocumentJob>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var copied = Path.Combine(fixture.ArtifactDirectory, "tenant-" + request.TenantId, request.ArtifactDocumentId!,
            $"content-{request.ContentSnapshot!.Sha256}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(copied)!);
        if (copied != ContentPath(fixture, original.ContentSnapshot!))
            File.Copy(ContentPath(fixture, original.ContentSnapshot!), copied);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Artifacts.GetDocumentContentAsync(request));
    }

    [Theory]
    [InlineData("https://storage.example.invalid/private")]
    [InlineData("../other")]
    [InlineData("..\\other")]
    public async Task Content_References_Cannot_Select_Arbitrary_Urls_Or_Paths(string value)
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        var job = ContentJob(fixture);
        job.ContentSnapshot = new(value, 100, new string('A', 64), "DocumentContent");

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Artifacts.GetDocumentContentAsync(job));
    }

    [Fact]
    public async Task Canceled_Publication_Exposes_No_Partial_Checkpoint()
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        var job = ContentJob(fixture);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Artifacts.SaveDocumentContentAsync(job, canceled.Token));
        Assert.Empty(Directory.GetFiles(fixture.ArtifactDirectory, "*.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(fixture.ArtifactDirectory, "*.upload", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Azure_Checkpoint_Uses_Hash_Key_Conditional_Create_And_Verifies_A_Conflicting_Publication()
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        var job = ContentJob(fixture);
        var service = new Mock<BlobServiceClient>();
        var container = new Mock<BlobContainerClient>();
        var blob = new Mock<BlobClient>();
        service.Setup(s => s.GetBlobContainerClient("tenant-tenant")).Returns(container.Object);
        var keys = new List<string>();
        container.Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(keys.Add).Returns(blob.Object);
        BinaryData? saved = null;
        BlobUploadOptions? conditions = null;
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<BinaryData, BlobUploadOptions, CancellationToken>((bytes, options, _) =>
            {
                saved = bytes;
                conditions = options;
            }).ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());
        var artifacts = new BlobArtifactStore(service.Object);
        var first = await artifacts.SaveDocumentContentAsync(job);
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(412, "Existing immutable content."));
        blob.Setup(b => b.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response.FromValue(BlobsModelFactory.BlobDownloadStreamingResult(
                new MemoryStream(saved!.ToArray()),
                BlobsModelFactory.BlobDownloadDetails(contentLength: saved!.ToMemory().Length)),
                Mock.Of<Response>()));

        // Act
        var identical = await artifacts.SaveDocumentContentAsync(job);
        job.Segments[0].BodyText += "Changed content.";

        // Assert
        Assert.Equal(first, identical);
        Assert.Equal(ETag.All, conditions!.Conditions.IfNoneMatch);
        Assert.Equal("application/json", conditions.HttpHeaders.ContentType);
        Assert.All(keys, key => Assert.Equal($"one-a1/content-{first.Sha256}.json", key));
        await Assert.ThrowsAsync<InvalidDataException>(() => artifacts.SaveDocumentContentAsync(job));
    }

    [Fact]
    public async Task Azure_Final_Content_Reuses_Only_An_Identical_Durably_Acknowledged_Initial_Result()
    {
        // Arrange
        using var fixture = new LargeDocumentCosmosFixture();
        var job = ContentJob(fixture);
        job.Status = JobStatus.Completed;
        job.CompletedAtUtc = DateTimeOffset.UtcNow;
        var service = new Mock<BlobServiceClient>();
        var container = new Mock<BlobContainerClient>();
        var blob = new Mock<BlobClient>();
        service.Setup(s => s.GetBlobContainerClient("tenant-tenant")).Returns(container.Object);
        container.Setup(c => c.GetBlobClient("one-a1/result.json")).Returns(blob.Object);
        BinaryData? saved = null;
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<BinaryData, BlobUploadOptions, CancellationToken>((bytes, _, _) => saved = bytes)
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());
        blob.Setup(b => b.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response.FromValue(BlobsModelFactory.BlobDownloadStreamingResult(
                new MemoryStream(saved!.ToArray())), Mock.Of<Response>()));
        var artifacts = new BlobArtifactStore(service.Object);
        job.ProcessingResultSha256 = await artifacts.SaveProcessingResultAsync(job);

        // Act
        var reference = await artifacts.SaveDocumentContentAsync(job);

        // Assert
        Assert.Equal("InitialProcessing", reference.Kind);
        Assert.Equal(job.ProcessingResultSha256, reference.Sha256);
        blob.Verify(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
        blob.Verify(b => b.DownloadStreamingAsync(
            It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static DocumentJob ContentJob(LargeDocumentCosmosFixture fixture)
    {
        var job = LargeDocumentCosmosFixture.Job("tenant", fixture.Source);
        job.Status = JobStatus.Running;
        job.ArtifactDocumentId = "one-a1";
        job.ExecutionAttempts = 1;
        job.LayoutSha256 = new string('C', 64);
        job.Segments = [new DocumentSlice { SegmentIndex = 0, StartPage = 1, EndPage = 1,
            BodyText = "Complete source and tail." }];
        return job;
    }

    private static string ContentPath(LargeDocumentCosmosFixture fixture, DocumentContentReference reference) =>
        Path.Combine(fixture.ArtifactDirectory, "tenant-tenant", "one-a1", $"content-{reference.Sha256}.json");
}
