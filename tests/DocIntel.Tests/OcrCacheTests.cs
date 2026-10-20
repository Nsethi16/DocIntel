using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DocIntel.Core.Evidence;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Ocr;
using DocIntel.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class OcrCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DocIntel.OcrCache.Tests",
        Guid.NewGuid().ToString("N"));
    private static byte[] Source => "%PDF-accepted original"u8.ToArray();
    private static string SourceHash => Convert.ToHexString(SHA256.HashData(Source));
    private static string ProviderHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("provider:v1")));

    [Fact]
    public async Task A_Recreated_Processor_And_Store_Reuse_Actual_Words_Without_A_Second_Provider_Call()
    {
        // Arrange
        var provider = Provider();
        var first = State();
        await Processor(provider, new LocalFileArtifactStore(_directory)).EnrichAsync(first);

        // Act
        var next = State();
        await Processor(provider, new LocalFileArtifactStore(_directory)).EnrichAsync(next);

        // Assert
        Assert.False(first.Pages[0].OcrCacheHit);
        Assert.True(next.Pages[0].OcrCacheHit);
        Assert.Equal(first.Pages[0].BodyText, next.Pages[0].BodyText);
        Assert.Equal(0.77, next.Pages[0].Words[0].OcrConfidence);
        provider.Verify(p => p.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<int>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("tenant", false)]
    [InlineData("tenant", true)]
    [InlineData("other", false)]
    public async Task Changed_Source_Provider_Or_Tenant_Does_Not_Reuse_Another_Cache_Identity(
        string tenant, bool sourceChanged)
    {
        // Arrange
        var provider = Provider();
        var artifacts = new LocalFileArtifactStore(_directory);
        await Processor(provider, artifacts).EnrichAsync(State());
        if (!sourceChanged && tenant == "tenant") provider.SetupGet(p => p.CacheIdentity).Returns("provider:v2");
        var next = State(tenant, sourceChanged ? "%PDF-different original"u8.ToArray() : Source);

        // Act
        await Processor(provider, artifacts).EnrichAsync(next);

        // Assert
        Assert.False(next.Pages[0].OcrCacheHit);
        provider.Verify(p => p.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<int>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Only_Uncached_Pages_Are_Submitted_To_The_Native_Provider()
    {
        // Arrange
        var provider = Provider();
        var artifacts = new LocalFileArtifactStore(_directory);
        var first = State();
        first.Pages.Add(Page(2, scanned: false));
        await Processor(provider, artifacts).EnrichAsync(first);
        var next = State();
        next.Pages.Add(Page(2));

        // Act
        await Processor(provider, artifacts).EnrichAsync(next);

        // Assert
        Assert.True(next.Pages[0].OcrCacheHit);
        Assert.False(next.Pages[1].OcrCacheHit);
        provider.Verify(p => p.RecognizeAsync(It.IsAny<byte[]>(),
            It.Is<IReadOnlyList<int>>(pages => pages.SequenceEqual(new[] { 2 })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Concurrent_Cache_Publications_Are_Immutable_And_Have_One_Winner()
    {
        // Arrange
        var entry = Entry();

        // Act
        var writes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new LocalFileArtifactStore(_directory).TrySaveOcrCacheAsync(entry)));

        // Assert
        Assert.Single(writes, written => written);
        var result = await new LocalFileArtifactStore(_directory).GetOcrCacheAsync(
            "tenant", SourceHash, ProviderHash, 1);
        Assert.Equal("Recognized", result!.Words[0].Text);
        Assert.Empty(Directory.GetFiles(_directory, "*.upload", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Corrupt_Stored_Cache_Is_Surfaced_Instead_Of_Triggering_A_Quiet_Provider_Retry()
    {
        // Arrange
        var provider = Provider();
        var store = new LocalFileArtifactStore(_directory);
        await Processor(provider, store).EnrichAsync(State());
        var path = Assert.Single(Directory.GetFiles(_directory, "*.json", SearchOption.AllDirectories));
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace("Recognized", "Modified", StringComparison.Ordinal));

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => Processor(provider, store).EnrichAsync(State()));
        provider.Verify(p => p.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<int>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cache_Raw_Words_Still_Use_The_New_Jobs_Accepted_Footer_Policy()
    {
        // Arrange
        var provider = Provider();
        provider.Setup(p => p.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([Result(1) with { Words = [Word(1), Word(1, "MBSA", index: 1, top: 0.94, bottom: 0.96)] }]);
        var artifacts = new LocalFileArtifactStore(_directory);
        var first = State();
        first.Tenant.Capabilities[FeatureHarness.SplitAndGroupKey].SplitAndGroup!.FootersToIgnore = ["MBSA"];
        await Processor(provider, artifacts).EnrichAsync(first);
        var next = State();
        next.Tenant.Capabilities[FeatureHarness.SplitAndGroupKey].SplitAndGroup!.FootersToIgnore = [];

        // Act
        await Processor(provider, artifacts).EnrichAsync(next);

        // Assert
        Assert.Empty(first.Pages[0].FooterText);
        Assert.Equal("MBSA", next.Pages[0].FooterText);
        Assert.True(next.Pages[0].OcrCacheHit);
    }

    [Fact]
    public async Task Cancellation_Does_Not_Publish_A_Partial_Cache_Entry()
    {
        // Arrange
        var store = new LocalFileArtifactStore(_directory);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TrySaveOcrCacheAsync(Entry(), canceled.Token));
        Assert.Null(await store.GetOcrCacheAsync("tenant", SourceHash, ProviderHash, 1));
        Assert.Empty(Directory.GetFiles(_directory, "*.upload", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Cache_Keys_Cannot_Interpret_Traversal_Or_A_Different_Original_Page()
    {
        // Arrange
        var store = new LocalFileArtifactStore(_directory);
        await store.TrySaveOcrCacheAsync(Entry());

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.GetOcrCacheAsync("tenant", "../source", ProviderHash, 1));
        Assert.Null(await store.GetOcrCacheAsync("tenant", SourceHash, ProviderHash, 2));
        Assert.Null(await store.GetOcrCacheAsync("other", SourceHash, ProviderHash, 1));
    }

    [Fact]
    public async Task Native_Blob_Cache_Uses_Immutable_Json_With_Source_Bound_Reads()
    {
        // Arrange
        var service = new Mock<BlobServiceClient>();
        var container = new Mock<BlobContainerClient>();
        var blob = new Mock<BlobClient>();
        service.Setup(s => s.GetBlobContainerClient("tenant-tenant")).Returns(container.Object);
        container.Setup(c => c.GetBlobClient(It.Is<string>(name => name.StartsWith("_ocr-cache/", StringComparison.Ordinal))))
            .Returns(blob.Object);
        BinaryData? saved = null;
        BlobUploadOptions? conditions = null;
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<BinaryData, BlobUploadOptions, CancellationToken>((body, options, _) =>
            { saved = body; conditions = options; }).ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());
        blob.Setup(b => b.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobDownloadStreamingResult(new MemoryStream(saved!.ToArray()),
                    BlobsModelFactory.BlobDownloadDetails(contentLength: saved.ToMemory().Length)), Mock.Of<Response>())));
        var store = new BlobArtifactStore(service.Object);

        // Act
        Assert.True(await store.TrySaveOcrCacheAsync(Entry()));
        var result = await store.GetOcrCacheAsync("tenant", SourceHash, ProviderHash, 1);

        // Assert
        Assert.Equal(ETag.All, conditions!.Conditions.IfNoneMatch);
        Assert.Equal("application/json", conditions.HttpHeaders.ContentType);
        Assert.Equal("Recognized", result!.Words[0].Text);
    }

    private static OcrProcessor Processor(Mock<IOcrProvider> provider, IArtifactStore artifacts) =>
        new(NullLogger<OcrProcessor>.Instance, provider.Object, artifacts);

    private static Mock<IOcrProvider> Provider()
    {
        var provider = new Mock<IOcrProvider>();
        provider.SetupGet(p => p.Name).Returns("ScriptedOCR");
        provider.SetupGet(p => p.CacheIdentity).Returns("provider:v1");
        provider.Setup(p => p.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[] _, IReadOnlyList<int> pages, CancellationToken _) =>
                pages.Select(Result).ToList());
        return provider;
    }

    private static OcrCacheEntry Entry() => new("tenant", SourceHash, ProviderHash, Result(1));
    private static OcrPageResult Result(int page) => new(page, 8.5, 11, [Word(page)], "ScriptedOCR");
    private static PageWord Word(int page, string text = "Recognized",
        int index = 0, double top = 0.1, double bottom = 0.12) =>
        new($"w_{page}_{index}", text, new BoundingBox(page, [0.1, top, 0.2, top, 0.2, bottom, 0.1, bottom]), 0.77);
    private static PageLayout Page(int number, bool scanned = true) => new()
    {
        PageNumber = number,
        PageWidth = 612,
        PageHeight = 792,
        BodyText = scanned ? String.Empty : new string('x', 100),
        ImageAreaRatio = scanned ? 1 : 0
    };
    private static SplittingState State(string tenant = "tenant", byte[]? bytes = null) => new()
    {
        Tenant = new TenantHarness { TenantId = tenant, DisplayName = "Test" },
        DocumentId = "one",
        FileName = "source.pdf",
        SourcePdfBytes = bytes ?? Source,
        Pages = [Page(1)]
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
