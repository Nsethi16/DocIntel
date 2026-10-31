using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocIntel.Core.Jobs;
using Microsoft.Azure.Cosmos;

namespace DocIntel.Core.Storage;

public sealed partial class CosmosJobStore
{
    // Leave room for Cosmos system properties; measure with the same serializer used by the SDK.
    private const int MaximumJobBytes = 2 * 1024 * 1024 - 16 * 1024;
    private const int MaximumPreviewCharacters = 2_048;

    private sealed class PreparedJob(DocumentJob job, DocumentStorageLimitException? failure = null)
    {
        public DocumentJob Job { get; set; } = job;
        public DocumentStorageLimitException? Failure { get; set; } = failure;
    }

    private static DocumentJob CloneJob(DocumentJob job) =>
        SystemTextJsonCosmosSerializer.Deserialize<DocumentJob>(SystemTextJsonCosmosSerializer.Serialize(job));

    private async Task<DocumentJob?> GetStoredJobAsync(
        string jobId, string tenantId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _container.ReadItemAsync<DocumentJob>(
                jobId, new PartitionKey(tenantId), cancellationToken: cancellationToken);
            var job = response.Resource;
            if (job.TenantId != tenantId || job.JobId != jobId)
                throw new InvalidDataException("Stored document identity does not match its partition key.");
            job.ETag = RequireETag(response.ETag);
            return job;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    private async Task<DocumentJob> HydrateJobAsync(DocumentJob job, CancellationToken cancellationToken)
    {
        if (job.ContentSnapshot is null) return job;
        ProcessingResultCodec.ValidateReference(job, job.ContentSnapshot);
        var bytes = await RequireArtifacts().GetDocumentContentAsync(job, cancellationToken)
            ?? throw new InvalidDataException("The acknowledged document-content snapshot is unavailable.");
        return ProcessingResultCodec.Hydrate(job, bytes);
    }

    private IArtifactStore RequireArtifacts() => _jobArtifacts ??
        throw new InvalidOperationException("Artifact storage is required for complete large-document persistence.");

    private async Task<PreparedJob> PrepareJobAsync(DocumentJob job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = SystemTextJsonCosmosSerializer.Serialize(job);
        var full = SystemTextJsonCosmosSerializer.Deserialize<DocumentJob>(bytes);
        if (bytes.Length <= MaximumJobBytes && job.ContentSnapshot is null && !job.SearchMetadataUnavailable)
        {
            full.SearchTextIsPartial = false;
            return new PreparedJob(full);
        }

        try
        {
            var reference = await RequireArtifacts().SaveDocumentContentAsync(job, cancellationToken);
            ProcessingResultCodec.VerifyAcknowledgement(job, reference);
            full.ContentSnapshot = reference;
        }
        catch (DocumentStorageLimitException exception)
        {
            full.ContentSnapshot = null;
            return new PreparedJob(TerminalMetadata(full, exception), exception);
        }

        for (var characters = MaximumPreviewCharacters; ; characters /= 2)
        {
            var metadata = SearchMetadata(full, characters);
            if (job.Status == JobStatus.Deleted)
                return new PreparedJob(TerminalMetadata(metadata, failure: null));
            if (SystemTextJsonCosmosSerializer.Serialize(metadata).Length <= MaximumJobBytes)
                return new PreparedJob(metadata);
            if (characters == 0)
            {
                var failure = new DocumentStorageLimitException(DocumentStorageLimitException.SearchMetadataTooLarge,
                    "The required document search metadata exceeds the Cosmos 2 MiB item limit. " +
                    "The complete output is retained in its immutable content snapshot; " +
                    "split the document before resubmitting.");
                return new PreparedJob(TerminalMetadata(metadata, failure), failure);
            }
        }
    }

    private static DocumentJob SearchMetadata(DocumentJob full, int characters)
    {
        var node = JsonNode.Parse(SystemTextJsonCosmosSerializer.Serialize(full))!.AsObject();
        var partial = false;
        node["processingHarness"] = null;
        node["packetSummary"] = Preview(node["packetSummary"]?.GetValue<string>(), characters, ref partial);
        node["extractedEntities"] = new JsonArray();
        node["dataExtractions"] = new JsonArray();
        foreach (var item in node["segments"]!.AsArray())
        {
            var slice = item!.AsObject();
            foreach (var field in new[] { "bodyText", "markdown", "translatedText", "insightSummary" })
                slice[field] = Preview(slice[field]?.GetValue<string>(), characters, ref partial);
            slice["dataExtraction"] = null;
            slice["promptColumnResults"] = new JsonArray();
            slice["footers"] = new JsonArray();
            slice["matchedIdentificationRule"] = null;
            slice["translationErrorMessage"] = null;
            foreach (var entity in slice["extractedEntities"]!.AsArray())
            {
                var fields = entity!.AsObject();
                fields["provenance"] = null;
                fields["confidenceSignals"] = null;
            }
        }
        foreach (var item in node["promptColumnResults"]!.AsArray())
        {
            var column = item!.AsObject();
            column["resultText"] = Preview(column["resultText"]?.GetValue<string>(), characters, ref partial);
            column["promptInstructions"] = String.Empty;
            column["filterSummary"] = String.Empty;
            column["matchedEntities"] = new JsonArray();
            column["base64Preview"] = String.Empty;
        }
        foreach (var step in node["executionTrace"]!.AsArray())
            step!["notes"] = null;
        node["searchTextIsPartial"] = partial;
        node["searchMetadataUnavailable"] = false;
        return node.Deserialize<DocumentJob>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static string? Preview(string? value, int characters, ref bool partial)
    {
        if (value is null || value.Length <= characters) return value;
        partial = true;
        var length = characters;
        if (length > 0 && Char.IsHighSurrogate(value[length - 1]) && Char.IsLowSurrogate(value[length]))
            length--;
        return value[..length];
    }

    private DocumentJob TerminalMetadata(DocumentJob job, DocumentStorageLimitException? failure)
    {
        var terminal = new DocumentJob
        {
            JobId = job.JobId,
            TenantId = job.TenantId,
            TenantDisplayName = job.TenantDisplayName[..Math.Min(job.TenantDisplayName.Length, 512)],
            FileName = job.FileName[..Math.Min(job.FileName.Length, 512)],
            AppliedHarnessVersion = job.AppliedHarnessVersion,
            OriginalSha256 = job.OriginalSha256,
            ArtifactDocumentId = job.ArtifactDocumentId,
            LayoutSha256 = job.LayoutSha256,
            ProcessingResultSha256 = job.ContentSnapshot is null ? null : job.ProcessingResultSha256,
            ContentSnapshot = job.ContentSnapshot,
            Status = job.Status == JobStatus.Deleted ? JobStatus.Deleted : JobStatus.Failed,
            ErrorCode = failure?.Code ?? job.ErrorCode,
            ErrorMessage = failure?.Message ?? job.ErrorMessage,
            SearchTextIsPartial = true,
            SearchMetadataUnavailable = true,
            TotalPages = job.TotalPages,
            StrategyUsed = job.StrategyUsed,
            HasDevelopmentResults = job.HasDevelopmentResults,
            SubmittedBy = job.SubmittedBy,
            CreatedAtUtc = job.CreatedAtUtc,
            CompletedAtUtc = job.Status == JobStatus.Deleted ? job.CompletedAtUtc : _jobClock.GetUtcNow(),
            DeletedAtUtc = job.DeletedAtUtc,
            DeletedBy = job.DeletedBy,
            DeletionInDevelopment = job.DeletionInDevelopment,
            ProcessingOwner = job.ProcessingOwner,
            ProcessingLeaseUntilUtc = job.ProcessingLeaseUntilUtc,
            ExecutionAttempts = job.ExecutionAttempts,
            ETag = job.ETag
        };
        if (SystemTextJsonCosmosSerializer.Serialize(terminal).Length > MaximumJobBytes)
            throw new InvalidDataException("The terminal document identity exceeds its metadata storage limit.");
        return terminal;
    }

    private async Task<ItemResponse<DocumentJob>> WriteJobAsync(
        PreparedJob prepared, string? etag, CancellationToken cancellationToken,
        Action<DocumentJob>? fence = null, Func<DocumentJob, CancellationToken, Task>? authorize = null)
    {
        try { return await WriteAsync(); }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            var failure = new DocumentStorageLimitException(DocumentStorageLimitException.CosmosPayloadTooLarge,
                "Cosmos rejected the document metadata size. " +
                "The document cannot be indexed; split it before resubmitting.");
            if (prepared.Job.ContentSnapshot is null && prepared.Job.OriginalSha256 is not null)
            {
                try
                {
                    var reference = await RequireArtifacts().SaveDocumentContentAsync(prepared.Job, cancellationToken);
                    ProcessingResultCodec.VerifyAcknowledgement(prepared.Job, reference);
                    prepared.Job.ContentSnapshot = reference;
                }
                catch (DocumentStorageLimitException contentLimit)
                {
                    failure = contentLimit;
                }
            }
            prepared.Job = TerminalMetadata(prepared.Job, failure);
            prepared.Failure = failure;
            try { return await WriteAsync(); }
            catch (CosmosException terminalRejection) when (
                terminalRejection.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                throw failure;
            }
        }

        async Task<ItemResponse<DocumentJob>> WriteAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (authorize is not null) await authorize(prepared.Job, cancellationToken);
            fence?.Invoke(prepared.Job);
            if (SystemTextJsonCosmosSerializer.Serialize(prepared.Job).Length > MaximumJobBytes)
                throw new InvalidDataException("An unbounded job body cannot be sent to Cosmos.");
            return etag is null
                ? await _container.CreateItemAsync(prepared.Job, new PartitionKey(prepared.Job.TenantId),
                    cancellationToken: cancellationToken)
                : await _container.ReplaceItemAsync(
                    prepared.Job, prepared.Job.JobId, new PartitionKey(prepared.Job.TenantId),
                    new ItemRequestOptions { IfMatchEtag = RequireETag(etag) }, cancellationToken);
        }
    }

    private static void ApplyStoredState(DocumentJob job, DocumentJob stored, string etag)
    {
        job.ETag = etag;
        job.ProcessingOwner = stored.ProcessingOwner;
        job.ProcessingLeaseUntilUtc = stored.ProcessingLeaseUntilUtc;
        job.ContentSnapshot = stored.ContentSnapshot;
        job.SearchTextIsPartial = stored.SearchTextIsPartial;
        job.SearchMetadataUnavailable = stored.SearchMetadataUnavailable;
        job.Status = stored.Status;
        job.ErrorCode = stored.ErrorCode;
        job.ErrorMessage = stored.ErrorMessage;
        job.CompletedAtUtc = stored.CompletedAtUtc;
        job.ProcessingResultSha256 = stored.ProcessingResultSha256;
    }

    private static void ThrowIfDocumentLimit(PreparedJob prepared)
    {
        var failure = prepared.Failure;
        if (failure is null || prepared.Job.Status == JobStatus.Deleted) return;
        failure.IsDurablyPersisted = true;
        throw failure;
    }
}
