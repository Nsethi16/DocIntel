using DocIntel.Core.Storage;
using Xunit;

namespace DocIntel.Tests;

public sealed class ArtifactIsolationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DocIntel.Artifacts.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Original_Uploads_Are_Readable_After_The_Artifact_Store_Is_Recreated()
    {
        // Arrange
        var bytes = "%PDF-Original bytes"u8.ToArray();
        var first = new LocalFileArtifactStore(_directory);
        await first.SaveOriginalPdfAsync("tenant", "one", bytes);

        // Act
        var restarted = new LocalFileArtifactStore(_directory);
        var original = await restarted.GetOriginalPdfAsync("tenant", "one");

        // Assert
        Assert.Equal(bytes, original);
        Assert.Null(await restarted.GetOriginalPdfAsync("other-tenant", "one"));
    }

    [Fact]
    public async Task A_Persisted_Original_Cannot_Be_Silently_Replaced()
    {
        // Arrange
        var store = new LocalFileArtifactStore(_directory);
        var original = "%PDF-Original"u8.ToArray();
        await store.SaveOriginalPdfAsync("tenant", "one", original);

        // Act / Assert
        await Assert.ThrowsAsync<IOException>(() =>
            store.SaveOriginalPdfAsync("tenant", "one", "%PDF-Replacement"u8.ToArray()));
        Assert.Equal(original, await store.GetOriginalPdfAsync("tenant", "one"));
        Assert.Empty(Directory.GetFiles(_directory, "*.upload", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Concurrent_Original_Uploads_Preserve_Exactly_One_Winners_Bytes_Across_Store_Instances()
    {
        // Arrange
        var originals = Enumerable.Range(0, 16).Select(index =>
            System.Text.Encoding.UTF8.GetBytes($"%PDF-Original candidate {index}")).ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = originals.Select(async bytes =>
        {
            await start.Task;
            try
            {
                await new LocalFileArtifactStore(_directory).SaveOriginalPdfAsync("tenant", "one", bytes);
                return true;
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 17 or 80 or 183)
            {
                return false;
            }
        }).ToArray();

        // Act
        start.SetResult();
        var writes = await Task.WhenAll(pending);
        var original = await new LocalFileArtifactStore(_directory).GetOriginalPdfAsync("tenant", "one");

        // Assert
        Assert.Single(writes, written => written);
        Assert.Equal(originals[Array.IndexOf(writes, true)], original);
        Assert.Empty(Directory.GetFiles(_directory, "*.upload", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Canceled_Original_Uploads_Do_Not_Expose_A_Partial_Source_File()
    {
        // Arrange
        var store = new LocalFileArtifactStore(_directory);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveOriginalPdfAsync("tenant", "one", "%PDF-Original"u8.ToArray(), canceled.Token));
        Assert.Null(await store.GetOriginalPdfAsync("tenant", "one"));
    }

    [Theory]
    [InlineData("a_b", "a-b")]
    [InlineData("Tenant", "tenant")]
    [InlineData("tenant one", "tenant-one")]
    public async Task Normalization_Does_Not_Alias_Distinct_Application_Tenants(string first, string second)
    {
        // Arrange
        var store = new LocalFileArtifactStore(_directory);
        var firstBytes = new byte[] { 1, 2, 3 };
        var secondBytes = new byte[] { 4, 5, 6 };

        // Act
        var firstPath = await store.SaveSlicePdfAsync(first, "same-job", 0, firstBytes);
        var secondPath = await store.SaveSlicePdfAsync(second, "same-job", 0, secondBytes);

        // Assert
        Assert.NotEqual(firstPath, secondPath);
        Assert.Equal(firstBytes, await store.GetSlicePdfAsync(first, "same-job", 0));
        Assert.Equal(secondBytes, await store.GetSlicePdfAsync(second, "same-job", 0));
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("..\\other")]
    [InlineData("C:\\other")]
    public async Task Document_Keys_Cannot_Escape_The_Tenant_Artifact_Directory(string documentId)
    {
        // Arrange
        var store = new LocalFileArtifactStore(_directory);

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.GetSlicePdfAsync("tenant", documentId, 0));
    }

    [Fact]
    public async Task Existing_Canonical_Tenant_Paths_Remain_Compatible()
    {
        // Arrange
        var store = new LocalFileArtifactStore(_directory);

        // Act
        var saved = await store.SaveSlicePdfAsync("contoso-legal", "one", 0, [1]);

        // Assert
        Assert.Equal(Path.Combine(_directory, "tenant-contoso-legal", "one", "slice-1.pdf"), saved);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
