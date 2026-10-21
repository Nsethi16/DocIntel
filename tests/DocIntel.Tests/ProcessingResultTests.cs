using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Storage;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class ProcessingResultTests
{
    [Fact]
    public async Task Completed_Json_Roundtrips_Independently_Of_The_Job_Database()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "DocIntel.Result.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var job = Job();
            job.ProcessingOwner = "private-lease-owner";
            job.ETag = "private-etag";
            job.Segments[0].SplitPdfBytes = [1, 2, 3];
            var artifacts = new LocalFileArtifactStore(directory);

            // Act
            job.ProcessingResultSha256 = await artifacts.SaveProcessingResultAsync(job);
            var bytes = await new LocalFileArtifactStore(directory).GetProcessingResultAsync(job);

            // Assert
            using var snapshot = JsonDocument.Parse(bytes!);
            Assert.Equal("InitialProcessing", snapshot.RootElement.GetProperty("snapshotKind").GetString());
            Assert.Equal("one-a1", snapshot.RootElement.GetProperty("artifactDocumentId").GetString());
            Assert.Equal("Accepted source result", snapshot.RootElement.GetProperty("packetSummary").GetString());
            Assert.Equal(3, snapshot.RootElement.GetProperty("processingHarness").GetProperty("harnessVersion").GetInt32());
            Assert.Equal(64, job.ProcessingResultSha256.Length);
            var json = System.Text.Encoding.UTF8.GetString(bytes!);
            Assert.DoesNotContain("private-lease-owner", json);
            Assert.DoesNotContain("private-etag", json);
            Assert.False(snapshot.RootElement.GetProperty("segments")[0].TryGetProperty("splitPdfBytes", out _));
            Assert.Empty(Directory.GetFiles(directory, "*.upload", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Snapshot_Publication_Is_Immutable_And_Corruption_Is_An_Error_Not_A_Database_Fallback()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "DocIntel.Result.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = new LocalFileArtifactStore(directory);
            var job = Job();
            job.ProcessingResultSha256 = await artifacts.SaveProcessingResultAsync(job);

            // Act / Assert
            await Assert.ThrowsAsync<IOException>(() => artifacts.SaveProcessingResultAsync(job));
            await File.WriteAllTextAsync(Path.Combine(directory, "tenant-tenant", "one-a1", "result.json"), "{}");
            await Assert.ThrowsAsync<InvalidDataException>(() => artifacts.GetProcessingResultAsync(job));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("source")]
    [InlineData("attempt")]
    public async Task A_Copied_Snapshot_Cannot_Be_Served_For_Another_Tenant_Source_Or_Attempt(string changed)
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "DocIntel.Result.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = new LocalFileArtifactStore(directory);
            var original = Job();
            var hash = await artifacts.SaveProcessingResultAsync(original);
            var request = Job(changed == "tenant" ? "other" : "tenant");
            request.ProcessingResultSha256 = hash;
            if (changed == "source") request.OriginalSha256 = new string('B', 64);
            if (changed == "attempt") request.ArtifactDocumentId = "one-a2";
            var copied = Path.Combine(directory, "tenant-" + request.TenantId, request.ArtifactDocumentId!);
            Directory.CreateDirectory(copied);
            if (changed != "source")
                File.Copy(Path.Combine(directory, "tenant-tenant", "one-a1", "result.json"),
                    Path.Combine(copied, "result.json"));

            // Act / Assert
            await Assert.ThrowsAsync<InvalidDataException>(() => artifacts.GetProcessingResultAsync(request));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Azure_Snapshot_Publication_Uses_Conditional_Create_And_Json_Content_Type()
    {
        // Arrange
        var service = new Mock<BlobServiceClient>();
        var container = new Mock<BlobContainerClient>();
        var blob = new Mock<BlobClient>();
        service.Setup(s => s.GetBlobContainerClient("tenant-tenant")).Returns(container.Object);
        container.Setup(c => c.GetBlobClient("one-a1/result.json")).Returns(blob.Object);
        BlobUploadOptions? upload = null;
        BinaryData? body = null;
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<BinaryData, BlobUploadOptions, CancellationToken>((bytes, options, _) =>
            { body = bytes; upload = options; })
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());
        var artifacts = new BlobArtifactStore(service.Object);

        // Act
        var hash = await artifacts.SaveProcessingResultAsync(Job());

        // Assert
        Assert.Equal(ETag.All, upload!.Conditions.IfNoneMatch);
        Assert.Equal("application/json", upload.HttpHeaders.ContentType);
        Assert.Equal(64, hash.Length);
        using var snapshot = JsonDocument.Parse(body!.ToString());
        Assert.Equal("tenant", snapshot.RootElement.GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task Failed_Or_Unbound_Jobs_Cannot_Be_Archived_As_Completed_Results()
    {
        // Arrange
        var artifacts = new LocalFileArtifactStore(Path.GetTempPath());
        var failed = Job();
        failed.Status = JobStatus.Failed;
        var unbound = Job();
        unbound.OriginalSha256 = null;

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => artifacts.SaveProcessingResultAsync(failed));
        await Assert.ThrowsAsync<InvalidDataException>(() => artifacts.SaveProcessingResultAsync(unbound));
    }

    private static DocumentJob Job(string tenant = "tenant") => new()
    {
        JobId = "one",
        TenantId = tenant,
        TenantDisplayName = "Tenant",
        AppliedHarnessVersion = 3,
        ProcessingHarness = new TenantHarness { TenantId = tenant, DisplayName = "Tenant", HarnessVersion = 3 },
        FileName = "original.pdf",
        Status = JobStatus.Completed,
        CompletedAtUtc = DateTimeOffset.UtcNow,
        ArtifactDocumentId = "one-a1",
        OriginalSha256 = new string('A', 64),
        LayoutSha256 = new string('C', 64),
        HasDevelopmentResults = false,
        PacketSummary = "Accepted source result",
        Segments = [new DocumentSlice { SegmentIndex = 0, StartPage = 1, EndPage = 1, BodyText = "Source text" }]
    };
}
