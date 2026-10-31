using DocIntel.Core.Authorization;
using DocIntel.Core.Jobs;

namespace DocIntel.Core.Features.AskAi;

/// <summary>Revalidates the source snapshots of saved answers without replaying their execution.</summary>
public static class AskAiSavedOutputValidator
{
    /// <summary>Requires current, authorized source versions for facts, not search-only clarifications.</summary>
    public static async Task ValidateAsync(
        AskAiRun run, IJobStore? jobs, DocumentAccessScope access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(access);
        cancellationToken.ThrowIfCancellationRequested();
        if (!access.CanRead)
            throw new UnauthorizedAccessException("This saved answer is no longer authorized.");

        var documentIds = run.ReferencedJobIds.Concat(run.Citations.Select(citation => citation.JobId))
            .Concat(run.SourceVersions.Keys).Distinct(StringComparer.Ordinal);
        foreach (var id in documentIds)
        {
            if (!access.CanReadDocument(id))
                throw new UnauthorizedAccessException("A saved answer document is no longer authorized.");
            if (jobs is null)
                throw new InvalidOperationException("Saved Ask AI output requires document availability checks.");
            var job = await jobs.GetJobAsync(id, cancellationToken, run.TenantId);
            if (job is not { Status: JobStatus.Completed } || job.TenantId != run.TenantId)
                throw new AskAiSourceChangedException();
            if (run.SourceVersions.TryGetValue(id, out var version) &&
                AskAiConversationContext.Fingerprint(job) != version)
                throw new AskAiSourceChangedException();
        }

        if (run.Citations.Any(citation => !run.SourceVersions.ContainsKey(citation.JobId)) ||
            run.Analyses.Count > 0 && run.SourceVersions.Count == 0)
            throw new AskAiSourceChangedException();
    }
}
