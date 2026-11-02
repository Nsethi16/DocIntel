using DocIntel.Api.Http;
using DocIntel.Core.Features.DocumentSplitting;
using Microsoft.AspNetCore.Http;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace DocIntel.Tests;

public sealed class PdfUploadValidatorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Plain text renamed as PDF")]
    [InlineData("%PDF-1.7\nThis is not a PDF object structure.\n%%EOF\n")]
    public void Invalid_Uploads_Are_Rejected_Before_Provider_Or_Artifact_Usage(string source)
    {
        // Act
        var exception = Assert.Throws<BadHttpRequestException>(() =>
            PdfUploadValidator.Validate(System.Text.Encoding.UTF8.GetBytes(source)));

        // Assert
        Assert.Equal(400, exception.StatusCode);
        Assert.DoesNotContain("object structure", exception.Message);
    }

    [Fact]
    public void An_Encrypted_Genuine_Pdf_Is_Rejected_Without_Exposing_Parser_Or_Password_Details()
    {
        // Arrange
        using var resource = typeof(PdfUploadValidatorTests).Assembly
            .GetManifestResourceStream("DocIntel.Tests.EncryptedUpload.pdf");
        Assert.NotNull(resource);
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);

        // Act
        var exception = Assert.Throws<BadHttpRequestException>(() => PdfUploadValidator.Validate(bytes.ToArray()));

        // Assert
        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("unencrypted", exception.Message);
        Assert.DoesNotContain("qa-open", exception.Message);
    }

    [Fact]
    public void A_Valid_Original_Pdf_Is_Accepted_Without_Changing_Its_Bytes()
    {
        // Arrange
        var bytes = PdfPigLayoutExtractor.CreateSamplePdf([("Original agreement", "Fixture")]);
        var expected = bytes.ToArray();

        // Act
        PdfUploadValidator.Validate(bytes);

        // Assert
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void More_Than_The_Supported_Page_Count_Is_An_Upload_Error_Not_A_Provider_Job()
    {
        // Arrange
        using var output = new MemoryStream();
        using (var builder = new PdfDocumentBuilder(output, disposeStream: false))
        {
            for (var index = 0; index < 1001; index++) builder.AddPage(PageSize.Letter);
            builder.Build();
        }

        // Act
        var exception = Assert.Throws<BadHttpRequestException>(() => PdfUploadValidator.Validate(output.ToArray()));

        // Assert
        Assert.Equal(400, exception.StatusCode);
    }

    [Fact]
    public void Canceled_Preflight_Does_Not_Open_A_Source()
    {
        // Arrange
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act / Assert
        Assert.ThrowsAny<OperationCanceledException>(() => PdfUploadValidator.Validate([], canceled.Token));
    }
}
