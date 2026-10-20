using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DocIntel.Core.Evidence;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Storage;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class PageEvidenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DocIntel.Layout.Tests",
        Guid.NewGuid().ToString("N"));
    private static string OriginalHash => Convert.ToHexString(SHA256.HashData("%PDF-source"u8));

    [Fact]
    public async Task Native_Words_Have_Stable_Page_Ids_And_Actual_Geometry_Not_Ocr_Confidence()
    {
        // Arrange
        var pdf = PdfPigLayoutExtractor.CreateSamplePdf([("Customer Name: Woodgrove", "Agreement-1")]);
        var extractor = new PdfPigLayoutExtractor();

        // Act
        var first = Assert.Single(await extractor.ExtractPagesAsync(pdf, new SplittingConfig()));
        var repeated = Assert.Single(await extractor.ExtractPagesAsync(pdf, new SplittingConfig()));

        // Assert
        Assert.Equal("EmbeddedText", first.TextSource);
        Assert.Equal("PdfPig", first.TextProvider);
        Assert.Equal(0, first.ImageAreaRatio);
        Assert.Equal(first.Words.Select(w => w.Id), repeated.Words.Select(w => w.Id));
        var word = Assert.Single(first.Words, w => w.Text == "Customer");
        Assert.Equal("w_1_0", word.Id);
        Assert.Null(word.OcrConfidence);
        Assert.Equal(1, word.Box.Page);
        Assert.InRange(word.Box.Polygon[0], 49.9 / 612, 50.1 / 612);
        Assert.InRange(word.Box.Polygon[1], 0.07, 0.10);
        Assert.Equal(word.Box.Polygon, repeated.Words[0].Box.Polygon);
        Assert.All(first.Words, w => Assert.All(w.Box.Polygon, coordinate => Assert.InRange(coordinate, 0, 1)));
    }

    [Theory]
    [InlineData(0, 0.12, 0.09)]
    [InlineData(90, 0.91, 0.12)]
    [InlineData(180, 0.88, 0.91)]
    [InlineData(270, 0.09, 0.88)]
    public async Task Word_Geometry_Follows_The_Displayed_Page_Rotation(int rotation, double expectedX, double expectedY)
    {
        // Arrange
        var pdf = PdfWithRotation(rotation);

        // Act
        var page = Assert.Single(await new PdfPigLayoutExtractor().ExtractPagesAsync(pdf, new SplittingConfig()));
        var word = Assert.Single(page.Words, w => w.Text == "Customer");

        // Assert
        Assert.Equal(rotation is 90 or 270 ? 792 : 612, page.PageWidth);
        Assert.Equal(rotation is 90 or 270 ? 612 : 792, page.PageHeight);
        var x = word.Box.Polygon.Where((_, index) => index % 2 == 0).Average();
        var y = word.Box.Polygon.Where((_, index) => index % 2 == 1).Average();
        Assert.InRange(x, expectedX - 0.04, expectedX + 0.04);
        Assert.InRange(y, expectedY - 0.04, expectedY + 0.04);
    }

    [Fact]
    public async Task Native_Word_Geometry_Respects_A_Nonzero_Page_Origin()
    {
        // Arrange
        var pdf = PdfWithRotation(0, offsetOrigin: true);

        // Act
        var page = Assert.Single(await new PdfPigLayoutExtractor().ExtractPagesAsync(pdf, new SplittingConfig()));
        var word = Assert.Single(page.Words, w => w.Text == "Customer");

        // Assert
        Assert.InRange(word.Box.Polygon[0], 49.9 / 612, 50.1 / 612);
        Assert.InRange(word.Box.Polygon[1], 0.07, 0.10);
    }

    [Fact]
    public async Task An_Image_Only_Page_Preserves_Image_Coverage_Without_Fabricating_Words()
    {
        // Arrange
        var pdf = PdfWithRotation(0, imageOnly: true);

        // Act
        var page = Assert.Single(await new PdfPigLayoutExtractor().ExtractPagesAsync(pdf, new SplittingConfig()));

        // Assert
        Assert.Equal(1, page.ImageAreaRatio);
        Assert.Empty(page.Words);
        Assert.Empty(page.BodyText);
    }

    [Fact]
    public async Task Oversized_Page_Collections_Are_Rejected_Before_Extracting_Every_Page()
    {
        // Arrange
        var pdf = PdfPigLayoutExtractor.CreateSamplePdf(
            Enumerable.Repeat(("Page", String.Empty), 1_001).ToList());

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PdfPigLayoutExtractor().ExtractPagesAsync(pdf, new SplittingConfig()));
    }

    [Fact]
    public void Polygon_Validation_Cannot_Be_Bypassed_By_Mutating_The_Input_Array()
    {
        // Arrange
        double[] polygon = [0.1, 0.1, 0.5, 0.1, 0.5, 0.2, 0.1, 0.2];
        var box = new BoundingBox(1, polygon);

        // Act
        polygon[0] = Double.NaN;

        // Assert
        Assert.Equal(0.1, box.Polygon[0]);
        Assert.Throws<ArgumentException>(() => new BoundingBox(1, polygon));
        Assert.Throws<ArgumentException>(() => new BoundingBox(1, [0, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(0, box.Polygon));
    }

    [Fact]
    public async Task Layout_Evidence_Roundtrips_After_Store_Recreation_With_Both_Hashes_Verified()
    {
        // Arrange
        var store = new LocalFileArtifactStore(_directory);
        var hash = await store.SaveLayoutAsync(Layout());

        // Act
        var recovered = await new LocalFileArtifactStore(_directory).GetLayoutAsync("tenant", "one-a1",
            hash, OriginalHash);

        // Assert
        Assert.Equal(64, hash.Length);
        Assert.Equal(1, recovered!.SchemaVersion);
        Assert.Equal("Customer", Assert.Single(Assert.Single(recovered.Pages).Words).Text);
        Assert.Null(recovered.Pages[0].Words[0].OcrConfidence);
        Assert.Null(await store.GetLayoutAsync("other", "one-a1", hash, OriginalHash));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetLayoutAsync("tenant", "one-a1",
            hash, new string('0', 64)));
    }

    [Fact]
    public async Task Immutable_Layout_Writes_And_Content_Hashes_Prevent_Silent_Replacements()
    {
        // Arrange
        var store = new LocalFileArtifactStore(_directory);
        var hash = await store.SaveLayoutAsync(Layout());

        // Act / Assert
        await Assert.ThrowsAsync<IOException>(() => store.SaveLayoutAsync(Layout()));
        var path = Path.Combine(_directory, "tenant-tenant", "one-a1", "layout.json");
        await File.WriteAllTextAsync(path, "{\"not\":\"the accepted layout\"}");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetLayoutAsync("tenant", "one-a1", hash, OriginalHash));
        Assert.Empty(Directory.GetFiles(_directory, "*.upload", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Canceled_Layout_Writes_Do_Not_Publish_Partial_Evidence()
    {
        // Arrange
        var store = new LocalFileArtifactStore(_directory);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveLayoutAsync(Layout(), canceled.Token));
        Assert.Null(await store.GetLayoutAsync("tenant", "one-a1", new string('0', 64), OriginalHash));
        Assert.Empty(Directory.GetFiles(_directory, "*.upload", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_Copied_Layout_Cannot_Be_Read_Under_A_Different_Tenant_Identity()
    {
        // Arrange
        var store = new LocalFileArtifactStore(_directory);
        var hash = await store.SaveLayoutAsync(Layout());
        var other = Path.Combine(_directory, "tenant-other", "one-a1");
        Directory.CreateDirectory(other);
        File.Copy(Path.Combine(_directory, "tenant-tenant", "one-a1", "layout.json"), Path.Combine(other, "layout.json"));

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.GetLayoutAsync("other", "one-a1", hash, OriginalHash));
    }

    [Fact]
    public async Task Invalid_Or_Duplicate_Word_References_Do_Not_Get_Persisted()
    {
        // Arrange
        var store = new LocalFileArtifactStore(_directory);
        var duplicate = Layout();
        duplicate.Pages[0].Words = [duplicate.Pages[0].Words[0], duplicate.Pages[0].Words[0]];

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveLayoutAsync(duplicate));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveLayoutAsync(Layout() with { SchemaVersion = 2 }));
    }

    [Fact]
    public async Task Azure_Layout_Publication_Is_Immutable_And_Preserves_Json_And_Word_Identity()
    {
        // Arrange
        var service = new Mock<BlobServiceClient>();
        var container = new Mock<BlobContainerClient>();
        var blob = new Mock<BlobClient>();
        service.Setup(s => s.GetBlobContainerClient("tenant-tenant")).Returns(container.Object);
        container.Setup(c => c.GetBlobClient("one-a1/layout.json")).Returns(blob.Object);
        BinaryData? saved = null;
        BlobUploadOptions? upload = null;
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<BinaryData, BlobUploadOptions, CancellationToken>((body, options, _) =>
            { saved = body; upload = options; }).ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());

        // Act
        var hash = await new BlobArtifactStore(service.Object).SaveLayoutAsync(Layout());

        // Assert
        Assert.Equal(ETag.All, upload!.Conditions.IfNoneMatch);
        Assert.Equal("application/json", upload.HttpHeaders.ContentType);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(saved!.ToMemory().Span)), hash);
        using var json = JsonDocument.Parse(saved.ToMemory());
        Assert.Equal("w_1_0", json.RootElement.GetProperty("pages")[0].GetProperty("words")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Azure_Layout_Reads_Check_The_Accepted_Content_And_Source_Hashes()
    {
        // Arrange
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Layout(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var service = new Mock<BlobServiceClient>();
        var container = new Mock<BlobContainerClient>();
        var blob = new Mock<BlobClient>();
        service.Setup(s => s.GetBlobContainerClient("tenant-tenant")).Returns(container.Object);
        container.Setup(c => c.GetBlobClient("one-a1/layout.json")).Returns(blob.Object);
        blob.Setup(b => b.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobDownloadStreamingResult(new MemoryStream(bytes),
                    BlobsModelFactory.BlobDownloadDetails(contentLength: bytes.Length)), Mock.Of<Response>())));
        var store = new BlobArtifactStore(service.Object);

        // Act / Assert
        Assert.NotNull(await store.GetLayoutAsync("tenant", "one-a1", hash, OriginalHash));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetLayoutAsync("tenant", "one-a1",
            hash, new string('0', 64)));
        bytes[0] = (byte)'x';
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetLayoutAsync("tenant", "one-a1", hash, OriginalHash));
    }

    [Fact]
    public void Job_Metadata_Does_Not_Contain_Layout_Words_Or_Pdf_Byte_Arrays()
    {
        // Arrange
        var job = new DocumentJob
        {
            TenantId = "tenant",
            JobId = "one",
            TenantDisplayName = "Test",
            AppliedHarnessVersion = 1,
            FileName = "source.pdf",
            LayoutSha256 = new string('A', 64),
            Segments = [new DocumentSlice { SplitPdfBytes = [1, 2], TranslatedPdfBytes = [3, 4] }]
        };

        // Act
        var json = JsonSerializer.Serialize(job);

        // Assert
        Assert.DoesNotContain("Words", json);
        Assert.DoesNotContain("PdfBytes", json);
        Assert.Contains("LayoutSha256", json);
    }

    private static DocumentLayout Layout() => new("tenant", "one-a1", OriginalHash,
    [
        new PageLayout
        {
            PageNumber = 1, PageWidth = 612, PageHeight = 792, BodyText = "Customer", TextSource = "EmbeddedText",
            TextProvider = "PdfPig",
            Words = [new PageWord("w_1_0", "Customer", new BoundingBox(1, [0.1, 0.1, 0.5, 0.1, 0.5, 0.2, 0.1, 0.2]))]
        }
    ]);

    internal static byte[] PdfWithRotation(int rotation, bool offsetOrigin = false, bool imageOnly = false)
    {
        var content = imageOnly ? "q 612 0 0 792 0 0 cm /Im1 Do Q" : offsetOrigin
            ? "BT /F1 12 Tf 150 920 Td (Customer Name) Tj ET" : "BT /F1 12 Tf 50 720 Td (Customer Name) Tj ET";
        var mediaBox = offsetOrigin ? "[100 200 712 992]" : "[0 0 612 792]";
        var images = imageOnly ? "/XObject << /Im1 6 0 R >>" : String.Empty;
        List<string> objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox} /Rotate {rotation} " +
                $"/Resources << /Font << /F1 5 0 R >> {images} >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        ];
        if (imageOnly)
            objects.Add("<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB " +
                "/BitsPerComponent 8 /Length 3 >>\nstream\nrgb\nendstream");
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
