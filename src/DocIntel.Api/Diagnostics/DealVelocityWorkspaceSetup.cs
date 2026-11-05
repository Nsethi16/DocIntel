using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocIntel.Api.Channels;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using DocIntel.Core.Storage;

namespace DocIntel.Api.Diagnostics;

/// <summary>Explicit, actor-bound workspace rename and fixed fictional document submission; never changes access or channels.</summary>
public static class DealVelocityWorkspaceSetup
{
    public const string WorkspaceId = "contoso-legal";
    public const string WorkspaceName = "Deal Velocity";

    /// <summary>Apply is a confirmed mutation; Inspect only reports current persisted workspace/document state.</summary>
    public static async Task<int> ExecuteAsync(
        string tenantId, string basisRunId, bool apply, bool confirmed, bool liveProbingEnabled,
        IHarnessStore harnesses, IAskAiRunStore runs, ITenantAccessResolver access,
        IJobStore jobs, IArtifactStore artifacts, IJobQueue queue, TextWriter output,
        CancellationToken cancellationToken = default)
    {
        if (tenantId != WorkspaceId || !PersonalEmailState.ValidRun(basisRunId) ||
            apply && (!confirmed || !liveProbingEnabled))
        {
            await WriteAsync(output, "Setup", "Refused", "InvalidSetupInput");
            return 2;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(180));
        try
        {
            var basis = await runs.GetRunAsync(tenantId, basisRunId, deadline.Token);
            if (basis is null || basis.TenantId != tenantId || basis.RunId != basisRunId ||
                basis.Status != JobStatus.Completed || basis.Actor is not { } actor ||
                actor.DirectoryTenantId == Guid.Empty || actor.ObjectId == Guid.Empty ||
                basis.PendingEmail is null || basis.ReplyChannel.ChannelType != AskAiChannelType.Email)
                throw new UnauthorizedAccessException("A saved human workspace request is required.");
            var current = await RequireAdministratorAsync(actor, harnesses, access, deadline.Token);
            var documents = DealVelocityDemoCorpus.Build();
            if (documents.Count != 9 || documents.Sum(document => (long)document.Bytes.Length) > 1024 * 1024)
                throw new InvalidDataException("The fixed demonstration corpus exceeded its limits.");
            var extractor = new PdfPigLayoutExtractor();
            foreach (var document in documents)
                if ((await extractor.ExtractGeometryAsync(document.Bytes, new(), deadline.Token)).Count != document.Pages)
                    throw new InvalidDataException("A demonstration PDF has unexpected pages.");

            if (apply && current.DisplayName != WorkspaceName)
            {
                var preserved = SettingsStamp(current);
                var updated = Clone(current);
                updated.DisplayName = WorkspaceName;
                await RequireAdministratorAsync(actor, harnesses, access, deadline.Token);
                var saved = await harnesses.UpsertTenantAsync(updated, deadline.Token);
                if (saved.TenantId != WorkspaceId || saved.DisplayName != WorkspaceName ||
                    SettingsStamp(saved) != preserved)
                    throw new InvalidDataException("The workspace update did not preserve its settings.");
                current = await RequireAdministratorAsync(actor, harnesses, access, deadline.Token);
                if (current.DisplayName != WorkspaceName)
                    throw new InvalidDataException("The workspace name was not persisted.");
            }
            await WriteAsync(output, "Workspace", current.DisplayName == WorkspaceName ? "Renamed" : "NotRenamed",
                "None", workspaceName: current.DisplayName == WorkspaceName ? WorkspaceName : "Other",
                version: current.HarnessVersion);
            var fingerprint = JsonSerializer.Serialize(current);
            var pending = false;
            var failed = false;
            foreach (var document in documents)
            {
                var checkedWorkspace = await RequireAdministratorAsync(actor, harnesses, access, deadline.Token);
                if (JsonSerializer.Serialize(checkedWorkspace) != fingerprint)
                    throw new HarnessConflictException();
                var hash = Convert.ToHexString(SHA256.HashData(document.Bytes));
                var id = JobId(document);
                var job = await jobs.GetJobAsync(id, deadline.Token, WorkspaceId);
                if (job is not null && (job.TenantId != WorkspaceId || job.JobId != id ||
                    job.FileName != document.FileName || job.OriginalSha256 != hash || job.SubmittedBy != actor))
                    throw new InvalidDataException("A demonstration job conflicts with an existing document.");
                if (job is null && apply)
                {
                    var original = await artifacts.GetOriginalPdfAsync(WorkspaceId, id, deadline.Token);
                    if (original is not null && !SHA256.HashData(original).SequenceEqual(SHA256.HashData(document.Bytes)))
                        throw new InvalidDataException("A demonstration original conflicts with its expected content.");
                    job = new DocumentJob
                    {
                        JobId = id, TenantId = WorkspaceId, TenantDisplayName = current.DisplayName,
                        AppliedHarnessVersion = current.HarnessVersion, ProcessingHarness = Clone(current),
                        FileName = document.FileName, SubmittedBy = actor, Status = JobStatus.Queued,
                        OriginalSha256 = hash, TotalPages = document.Pages
                    };
                    if (original is null)
                        job.OriginalPdfLocation = await artifacts.SaveOriginalPdfAsync(
                            WorkspaceId, id, document.Bytes, deadline.Token);
                    checkedWorkspace = await RequireAdministratorAsync(actor, harnesses, access, deadline.Token);
                    if (JsonSerializer.Serialize(checkedWorkspace) != fingerprint) throw new HarnessConflictException();
                    await jobs.UpsertJobAsync(job, deadline.Token);
                    await queue.TryEnqueueAsync(new(id, WorkspaceId), deadline.Token);
                }
                if (job is not null)
                {
                    var original = await artifacts.GetOriginalPdfAsync(WorkspaceId, id, deadline.Token);
                    if (original is null || Convert.ToHexString(SHA256.HashData(original)) != hash)
                        throw new InvalidDataException("The saved demonstration original failed its integrity check.");
                }
                pending |= job is null || job.Status is JobStatus.Queued or JobStatus.Running;
                failed |= job?.Status == JobStatus.Failed;
                if (job?.Status == JobStatus.Completed && (job.HasDevelopmentResults != false ||
                    job.TotalPages != document.Pages || job.Segments.Count == 0 ||
                    job.Segments.Sum(slice => (long)DocumentPromptBoundary.OriginalSourceText(slice).Length) == 0))
                    throw new InvalidDataException("Live demonstration processing has no verified production provenance.");
                await WriteAsync(output, document.Key, job?.Status.ToString() ?? "NotFound",
                    job?.Status == JobStatus.Failed ? "ProcessingFailed" : "None",
                    document: document, job: job);
            }
            await RequireAdministratorAsync(actor, harnesses, access, deadline.Token);
            await WriteAsync(output, "Summary", apply ? "Submitted" : failed ? "Failed" : pending ? "Pending" : "Completed",
                failed ? "ProcessingFailed" : "None");
            return failed ? 1 : !apply && pending ? 3 : 0;
        }
        catch (UnauthorizedAccessException)
        {
            await WriteAsync(output, "Summary", "Refused", "CurrentAdministratorRequired");
            return 2;
        }
        catch (HarnessConflictException)
        {
            await WriteAsync(output, "Summary", "Failed", "WorkspaceChanged");
            return 1;
        }
        catch (InvalidDataException)
        {
            await WriteAsync(output, "Summary", "Failed", "DocumentConflictOrIntegrityFailed");
            return 1;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await WriteAsync(output, "Summary", "Failed", "TimedOut");
            return 1;
        }
        catch (Exception)
        {
            await WriteAsync(output, "Summary", "Failed", "SetupFailed");
            return 1;
        }
    }

    /// <summary>Stable IDs make a confirmed retry reuse only identical, owned demonstration documents.</summary>
    public static string JobId(DealVelocityDemoCorpus.Document document) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            DealVelocityDemoCorpus.Version + "|" + WorkspaceId + "|" + document.FileName + "|" +
            Convert.ToHexString(SHA256.HashData(document.Bytes)))).AsSpan(0, 16));

    private static async Task<TenantHarness> RequireAdministratorAsync(
        ActorIdentity actor, IHarnessStore harnesses, ITenantAccessResolver access, CancellationToken token)
    {
        var scope = await access.ResolveAsync(actor, WorkspaceId, token);
        if (!scope.CanAdmin || !scope.CanProcess || scope.AllowedJobIds is not null)
            throw new UnauthorizedAccessException("Current workspace-wide administrator access is required.");
        var tenant = await harnesses.GetTenantAsync(WorkspaceId, token);
        if (tenant is not { IsActive: true } || tenant.TenantId != WorkspaceId)
            throw new UnauthorizedAccessException("The workspace is inactive or unavailable.");
        return tenant;
    }

    private static TenantHarness Clone(TenantHarness tenant) =>
        JsonSerializer.Deserialize<TenantHarness>(JsonSerializer.SerializeToUtf8Bytes(tenant))
        ?? throw new InvalidDataException("The workspace configuration is unavailable.");

    private static string SettingsStamp(TenantHarness tenant)
    {
        var node = JsonSerializer.SerializeToNode(tenant)?.AsObject()
            ?? throw new InvalidDataException("The workspace configuration is unavailable.");
        foreach (var name in new[] { "DisplayName", "HarnessVersion", "UpdatedAtUtc" }) node.Remove(name);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    private static Task WriteAsync(TextWriter output, string stage, string status, string failureCode,
        string? workspaceName = null, int? version = null, DealVelocityDemoCorpus.Document? document = null,
        DocumentJob? job = null) => output.WriteLineAsync("DOCINTEL_DIAGNOSTIC " + JsonSerializer.Serialize(new
        {
            phase = "DealVelocity", stage, status, failureCode, workspaceName, version,
            fileName = document?.FileName, jobId = job?.JobId, pages = job?.TotalPages,
            slices = job?.Segments.Count, hasWarnings = job?.HasWarnings,
            sourceCharacters = job?.Segments.Sum(slice => (long)DocumentPromptBoundary.OriginalSourceText(slice).Length),
            recognizedSlices = job?.Segments.Count(slice => !String.IsNullOrWhiteSpace(slice.IdentifiedDocumentName) &&
                !String.Equals(slice.IdentifiedDocumentName, "Undefined", StringComparison.OrdinalIgnoreCase)),
            structuredDocuments = job?.Segments.Count(slice => slice.DataExtraction is not null),
            entities = job?.ExtractedEntities.Count,
            failedStages = job?.ExecutionTrace.Where(step => step.Executed && !step.Succeeded)
                .Select(step => step.ExecutorName).Where(name => name is
                    "ExtractPageLayoutsExecutor" or "DetectSliceBoundariesExecutor" or "WritePdfSlicesExecutor" or
                    "IdentifyDocumentsExecutor" or "TranslateDocumentsExecutor" or "GenerateDocumentInsightsExecutor" or
                    "ExtractEntitiesExecutor" or "ExtractDocumentDataExecutor" or "EvaluatePromptColumnsExecutor" or
                    "GroundExtractedValuesExecutor").Distinct().ToArray()
        }));
}
