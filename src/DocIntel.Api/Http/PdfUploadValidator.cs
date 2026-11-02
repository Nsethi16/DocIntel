using DocIntel.Core.Features.DocumentSplitting;
using Microsoft.AspNetCore.Http;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace DocIntel.Api.Http;

/// <summary>Rejects unreadable or encrypted uploads before retaining or dispatching a document.</summary>
internal static class PdfUploadValidator
{
    internal static void Validate(byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length is < 5 or > 25 * 1024 * 1024 || !bytes.AsSpan().StartsWith("%PDF-"u8))
            throw new BadHttpRequestException("Upload one valid PDF of at most 25 MiB.", 400);
        try
        {
            using var document = PdfDocument.Open(bytes, new ParsingOptions { ClipPaths = false });
            if (document.IsEncrypted)
                throw new BadHttpRequestException("Upload an unencrypted PDF.", 400);
            if (document.NumberOfPages is < 1 or > PdfPigLayoutExtractor.MaximumPages)
                throw new BadHttpRequestException("The PDF page count exceeds the supported limits.", 400);
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new BadHttpRequestException("Upload an unencrypted PDF.", 400);
        }
        catch (Exception exception) when (exception is PdfDocumentFormatException or InvalidDataException or IOException)
        {
            throw new BadHttpRequestException("The PDF structure is unreadable. Upload a readable original PDF.", 400);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
