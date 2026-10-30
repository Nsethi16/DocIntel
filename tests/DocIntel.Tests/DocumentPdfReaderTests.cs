using System.Security.Cryptography;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Jobs;
using DocIntel.Core.Storage;
using Moq;
using UglyToad.PdfPig;
using Xunit;

namespace DocIntel.Tests;

public sealed class DocumentPdfReaderTests
{
    [Fact]
    public async Task Existing_Slices_Use_Only_The_Current_Tenant_And_Processing_Attempt()
    {
        // Arrange
        var job = Job();
        var artifacts = new Mock<IArtifactStore>(MockBehavior.Strict);
        var bytes = "%PDF-existing slice"u8.ToArray();
        artifacts.Setup(store => store.GetSlicePdfAsync("workspace", "job-a2", 0,
            It.IsAny<CancellationToken>(), false)).ReturnsAsync(bytes);

        // Act
        var actual = await DocumentPdfReader.ReadSliceAsync(job, 0, false, artifacts.Object, Mock.Of<IPdfSlicer>());

        // Assert
        Assert.Equal(bytes, actual);
        artifacts.VerifyAll();
        artifacts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Missing_Slices_Copy_Only_Their_Exact_Pages_From_The_Verified_Original_Without_Saving()
    {
        // Arrange
        var original = PdfPigLayoutExtractor.CreateSamplePdf(
            [("EXCLUDED_FIRST_PAGE", "One"), ("INCLUDED_SECOND_PAGE", "Two"),
                ("INCLUDED_THIRD_PAGE", "Three"), ("EXCLUDED_LAST_PAGE", "Four")]);
        var job = Job();
        job.OriginalSha256 = Convert.ToHexString(SHA256.HashData(original));
        var artifacts = MissingSlice(original);

        // Act
        var recovered = await DocumentPdfReader.ReadSliceAsync(
            job, 0, false, artifacts.Object, new PdfPigLayoutExtractor());

        // Assert
        Assert.NotNull(recovered);
        using var pdf = PdfDocument.Open(recovered);
        Assert.Equal(2, pdf.NumberOfPages);
        Assert.Contains("INCLUDED_SECOND_PAGE", pdf.GetPage(1).Text);
        Assert.Contains("INCLUDED_THIRD_PAGE", pdf.GetPage(2).Text);
        Assert.DoesNotContain("EXCLUDED_", String.Join("\n", pdf.GetPages().Select(page => page.Text)));
        artifacts.VerifyAll();
        artifacts.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_Translations_And_Unverified_Legacy_Sources_Are_Not_Replaced_With_Invented_Pdfs(
        bool translated)
    {
        // Arrange
        var job = Job();
        job.Segments[0].WasTranslated = translated;
        if (!translated) job.OriginalSha256 = null;
        var artifacts = new Mock<IArtifactStore>(MockBehavior.Strict);
        artifacts.Setup(store => store.GetSlicePdfAsync("workspace", "job-a2", 0,
            It.IsAny<CancellationToken>(), translated)).ReturnsAsync((byte[]?)null);
        var slicer = new Mock<IPdfSlicer>(MockBehavior.Strict);

        // Act
        var actual = await DocumentPdfReader.ReadSliceAsync(job, 0, translated, artifacts.Object, slicer.Object);

        // Assert
        Assert.Null(actual);
        artifacts.VerifyAll();
        artifacts.VerifyNoOtherCalls();
        slicer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_Failed_Translation_Cannot_Expose_A_Previous_Translated_Artifact()
    {
        // Arrange
        var job = Job();
        job.Segments[0].WasTranslated = true;
        job.Segments[0].TranslationErrorCode = "TranslationProviderFailed";
        var artifacts = new Mock<IArtifactStore>(MockBehavior.Strict);

        // Act
        var actual = await DocumentPdfReader.ReadSliceAsync(job, 0, true, artifacts.Object, Mock.Of<IPdfSlicer>());

        // Assert
        Assert.Null(actual);
        artifacts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_Missing_Original_Is_Reported_Without_Using_Old_Attempt_Artifacts()
    {
        // Arrange
        var artifacts = MissingSlice(null);
        var slicer = new Mock<IPdfSlicer>(MockBehavior.Strict);

        // Act
        var actual = await DocumentPdfReader.ReadSliceAsync(Job(), 0, false, artifacts.Object, slicer.Object);

        // Assert
        Assert.Null(actual);
        artifacts.VerifyAll();
        artifacts.VerifyNoOtherCalls();
        slicer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_Changed_Original_Cannot_Be_Used_To_Recover_A_Source_Slice()
    {
        // Arrange
        var artifacts = MissingSlice("%PDF-changed original"u8.ToArray());
        var slicer = new Mock<IPdfSlicer>(MockBehavior.Strict);

        // Act
        var action = DocumentPdfReader.ReadSliceAsync(Job(), 0, false, artifacts.Object, slicer.Object);

        // Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => action);
        slicer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_Corrupt_Stored_Slice_Is_Not_Silently_Substituted()
    {
        // Arrange
        var artifacts = new Mock<IArtifactStore>(MockBehavior.Strict);
        artifacts.Setup(store => store.GetSlicePdfAsync("workspace", "job-a2", 0,
            It.IsAny<CancellationToken>(), false)).ReturnsAsync("not a PDF"u8.ToArray());

        // Act
        var action = DocumentPdfReader.ReadSliceAsync(Job(), 0, false, artifacts.Object, Mock.Of<IPdfSlicer>());

        // Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => action);
        artifacts.VerifyAll();
        artifacts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Slice_Indexes_Absent_From_The_Current_Result_Cannot_Read_Artifacts()
    {
        // Arrange
        var artifacts = new Mock<IArtifactStore>(MockBehavior.Strict);

        // Act
        var actual = await DocumentPdfReader.ReadSliceAsync(Job(), 1, false, artifacts.Object, Mock.Of<IPdfSlicer>());

        // Assert
        Assert.Null(actual);
        artifacts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Canceled_Preview_Requests_Do_Not_Read_Artifacts()
    {
        // Arrange
        var artifacts = new Mock<IArtifactStore>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var action = DocumentPdfReader.ReadSliceAsync(
            Job(), 0, false, artifacts.Object, Mock.Of<IPdfSlicer>(), cancellation.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        artifacts.VerifyNoOtherCalls();
    }

    private static DocumentJob Job() => new()
    {
        JobId = "job", TenantId = "workspace", TenantDisplayName = "Workspace", AppliedHarnessVersion = 1,
        FileName = "source.pdf", ArtifactDocumentId = "job-a2", OriginalSha256 = new string('A', 64),
        TotalPages = 4, Segments = [new() { SegmentIndex = 0, StartPage = 2, EndPage = 3 }]
    };

    private static Mock<IArtifactStore> MissingSlice(byte[]? original)
    {
        var artifacts = new Mock<IArtifactStore>(MockBehavior.Strict);
        artifacts.Setup(store => store.GetSlicePdfAsync("workspace", "job-a2", 0,
            It.IsAny<CancellationToken>(), false)).ReturnsAsync((byte[]?)null);
        artifacts.Setup(store => store.GetOriginalPdfAsync("workspace", "job", It.IsAny<CancellationToken>()))
            .ReturnsAsync(original);
        return artifacts;
    }
}
