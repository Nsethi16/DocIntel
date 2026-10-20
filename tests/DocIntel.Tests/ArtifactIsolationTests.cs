using DocIntel.Core.Storage;
using Xunit;

namespace DocIntel.Tests;

public sealed class ArtifactIsolationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DocIntel.Artifacts.Tests",
        Guid.NewGuid().ToString("N"));

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
