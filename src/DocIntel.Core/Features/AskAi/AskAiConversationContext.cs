using System.Security.Cryptography;
using System.Text.Json;
using DocIntel.Core.Authorization;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using Microsoft.Extensions.AI;

namespace DocIntel.Core.Features.AskAi;

/// <summary>Loads only same-actor, same-workspace, source-current prior turns from server-owned snapshots.</summary>
public static class AskAiConversationContext
{
    /// <summary>Fingerprints source and derived content, excluding mutable database leases and storage ETags.</summary>
    public static string Fingerprint(DocumentJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            job.OriginalSha256, job.ArtifactDocumentId, job.ProcessingResultSha256,
            job.AppliedHarnessVersion, job.PacketSummary, job.Segments
        })));
    }

    /// <summary>Returns up to four verified prior turns; browser-supplied messages are never accepted as history.</summary>
    public static async Task<IReadOnlyList<AskAiRun>> ReadHistoryAsync(
        AskAiRun run, IAskAiRunStore store, IJobStore jobs, DocumentAccessScope access,
        CancellationToken cancellationToken = default)
    {
        var history = new List<AskAiRun>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { run.RunId };
        var id = run.PreviousRunId;
        while (id is not null && history.Count < 4)
        {
            if (!visited.Add(id)) throw new InvalidOperationException("The Ask AI conversation contains a cycle.");
            var previous = await store.GetRunAsync(run.TenantId, id, cancellationToken);
            if (previous is null || previous.TenantId != run.TenantId || previous.Actor != run.Actor ||
                previous.Status != JobStatus.Completed || String.IsNullOrWhiteSpace(previous.Answer))
                throw new UnauthorizedAccessException("The prior conversation is unavailable to this actor.");
            if (previous.Citations.Any(citation => !previous.SourceVersions.ContainsKey(citation.JobId)) ||
                previous.DocumentsEvaluated > 0 && previous.SourceVersions.Count == 0)
                throw new AskAiSourceChangedException();
            var documentIds = previous.ReferencedJobIds.Concat(previous.Citations.Select(citation => citation.JobId))
                .Concat(previous.SourceVersions.Keys).Distinct(StringComparer.Ordinal);
            foreach (var documentId in documentIds)
            {
                if (!access.CanReadDocument(documentId))
                    throw new UnauthorizedAccessException("A prior conversation document is no longer authorized.");
                var job = await jobs.GetJobAsync(documentId, cancellationToken, run.TenantId);
                if (job is not { Status: JobStatus.Completed } || job.TenantId != run.TenantId)
                    throw new AskAiSourceChangedException();
                if (previous.SourceVersions.TryGetValue(documentId, out var version) && Fingerprint(job) != version)
                    throw new AskAiSourceChangedException();
            }
            history.Add(previous);
            id = previous.PreviousRunId;
        }
        history.Reverse();
        return history;
    }

    /// <summary>Prior answers are labeled, bounded, untrusted context, not fresh citation evidence.</summary>
    public static List<ChatMessage> BuildMessages(AskAiRun run, IReadOnlyList<AskAiRun> history)
    {
        var messages = new List<ChatMessage>();
        foreach (var previous in history)
        {
            var previousAnswer = previous.Answer
                ?? throw new InvalidDataException("A prior conversation answer is unavailable.");
            var context = new
            {
                previousQuestion = previous.Question,
                previousAnswer = previousAnswer[..Math.Min(previousAnswer.Length, 4_000)],
                answerShortened = previousAnswer.Length > 4_000,
                previousAnalyses = previous.Analyses.Take(4).Select(analysis => new
                {
                    analysis.Title, analysis.Operation, analysis.Notes, analysis.Coverage, analysis.Columns,
                    rowsPreview = analysis.Rows.Take(5).Select(row => row.Cells.ToDictionary(cell => cell.Key, cell =>
                    {
                        var text = Convert.ToString(cell.Value, System.Globalization.CultureInfo.InvariantCulture);
                        return text is { Length: > 120 } ? text[..117] + "..." : text;
                    })),
                    rowsShortened = analysis.Rows.Count > 5
                }),
                documentReferences = previous.Citations.Select(citation => new
                    { citation.JobId, citation.SliceIndex, citation.DocumentName, citation.PageRange })
            };
            messages.Add(new ChatMessage(ChatRole.User, DocumentPromptBoundary.Wrap(JsonSerializer.Serialize(context))));
        }
        messages.Add(new ChatMessage(ChatRole.User, run.Question));
        return messages;
    }
}

/// <summary>A conversation or active answer lost the source version on which its facts depended.</summary>
public sealed class AskAiSourceChangedException : InvalidOperationException
{
    public AskAiSourceChangedException()
        : base("A document used by this conversation changed; start a new question without prior context.") { }
}
