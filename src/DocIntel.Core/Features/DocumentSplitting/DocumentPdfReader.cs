using System.Security.Cryptography;
using DocIntel.Core.Jobs;
using DocIntel.Core.Storage;

namespace DocIntel.Core.Features.DocumentSplitting;

/// <summary>Reads stored PDFs and recovers missing source slices only from verified original bytes.</summary>
public static class DocumentPdfReader
{
    /// <summary>Reads the accepted original and checks its recorded content hash when available.</summary>
    public static async Task<byte[]?> ReadOriginalAsync(
        DocumentJob job, IArtifactStore artifacts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(artifacts);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = await artifacts.GetOriginalPdfAsync(job.TenantId, job.JobId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes is null) return null;
        LocalFileArtifactStore.ValidateOriginal(bytes);
        if (job.OriginalSha256 is not null &&
            !String.Equals(job.OriginalSha256, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.Ordinal))
            throw new InvalidDataException("The persisted source PDF does not match its accepted hash.");
        return bytes;
    }

    /// <summary>
    /// Reads only the active attempt's requested slice, or copies its exact original page range in memory.
    /// Missing translations and unverified legacy sources are never replaced with generated content.
    /// </summary>
    public static async Task<byte[]?> ReadSliceAsync(
        DocumentJob job, int sliceIndex, bool translated, IArtifactStore artifacts, IPdfSlicer slicer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(slicer);
        LocalFileArtifactStore.ValidateDocumentKey(job.JobId, sliceIndex);
        cancellationToken.ThrowIfCancellationRequested();
        var slice = job.Segments.FirstOrDefault(item => item.SegmentIndex == sliceIndex);
        if (slice is null) return null;
        if (translated && (!slice.WasTranslated || slice.TranslationErrorCode is not null)) return null;

        var bytes = await artifacts.GetSlicePdfAsync(job.TenantId, job.ArtifactDocumentId ?? job.JobId,
            sliceIndex, cancellationToken, isTranslated: translated);
        cancellationToken.ThrowIfCancellationRequested();
        bytes ??= translated ? slice.TranslatedPdfBytes : slice.SplitPdfBytes;
        if (bytes is { Length: > 0 })
        {
            if (!bytes.AsSpan().StartsWith("%PDF-"u8))
                throw new InvalidDataException("The stored slice artifact is not a PDF.");
            return bytes;
        }
        if (translated || String.IsNullOrWhiteSpace(job.OriginalSha256)) return null;
        if (slice.StartPage < 1 || slice.EndPage < slice.StartPage ||
            (job.TotalPages > 0 && slice.EndPage > job.TotalPages))
            throw new InvalidDataException("The stored PDF slice has an invalid source page range.");
        var original = await ReadOriginalAsync(job, artifacts, cancellationToken);
        if (original is null) return null;
        var recovered = await slicer.SlicePdfAsync(original, [(slice.StartPage, slice.EndPage)], cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (recovered.Count != 1 || recovered[0].Length < 5 || !recovered[0].AsSpan().StartsWith("%PDF-"u8))
            throw new InvalidDataException("The source PDF slice could not be recovered.");
        return recovered[0];
    }
}
