using System.Text.Json;
using Azure;
using Azure.Storage.Blobs.Models;
using DocIntel.Core.Export;

namespace DocIntel.Core.Storage;

internal static class ExportAuditCodec
{
    public static byte[] Serialize(ExportAuditEvent audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        LocalFileArtifactStore.ValidateDocumentKey(audit.JobId, 0);
        if (audit.ExportId is not { Length: 32 } || !audit.ExportId.All(Char.IsAsciiHexDigit) ||
            String.IsNullOrWhiteSpace(audit.TenantId) || audit.Format is not ("csv" or "xlsx" or "json") ||
            audit.ContentSha256 is not { Length: 64 } || !audit.ContentSha256.All(Char.IsAsciiHexDigit))
            throw new ArgumentException("The export audit identity, format or content hash is invalid.");
        return JsonSerializer.SerializeToUtf8Bytes(audit, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}

public sealed partial class LocalFileArtifactStore
{
    public async Task SaveExportAuditAsync(ExportAuditEvent audit, CancellationToken cancellationToken = default)
    {
        var bytes = ExportAuditCodec.Serialize(audit);
        await WriteImmutableAsync(audit.TenantId, audit.JobId, $"export-event-{audit.ExportId}.json",
            bytes, cancellationToken);
    }
}

public sealed partial class BlobArtifactStore
{
    public async Task SaveExportAuditAsync(ExportAuditEvent audit, CancellationToken cancellationToken = default)
    {
        var bytes = ExportAuditCodec.Serialize(audit);
        var container = _blobServiceClient.GetBlobContainerClient(LocalFileArtifactStore.SanitizeContainerName(audit.TenantId));
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await container.GetBlobClient($"{audit.JobId}/export-event-{audit.ExportId}.json")
            .UploadAsync(BinaryData.FromBytes(bytes), new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            }, cancellationToken);
    }
}
