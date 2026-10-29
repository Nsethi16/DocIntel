using System.Text.Json;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Jobs;

namespace DocIntel.Api.Diagnostics;

/// <summary>Operator-only CLI diagnostics; no public endpoint, stored-run mutation or channel delivery.</summary>
public static class AskAiRunDiagnostic
{
    private const string Prefix = "DOCINTEL_DIAGNOSTIC ";

    /// <summary>Inspects one/recent stored runs or replays an explicitly selected failed web question in memory.</summary>
    public static async Task<int> ExecuteAsync(string tenantId, string runId, bool replay,
        IAskAiRunStore store, AskAiOrchestrator orchestrator, TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (tenantId.Length is < 1 or > 128 ||
            tenantId.Any(character => !Char.IsAsciiLetterOrDigit(character) && character != '-') ||
            !(runId == "recent" && !replay) && !Guid.TryParseExact(runId, "N", out _))
            throw new ArgumentException("A valid tenant ID and opaque run ID are required.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(replay ? 300 : 120));
        if (runId == "recent")
        {
            var recent = (await store.ListRunsByTenantAsync(tenantId, deadline.Token))
                .Where(run => run.Status == JobStatus.Failed && run.CreatedAtUtc >= DateTimeOffset.UtcNow.AddDays(-7))
                .OrderByDescending(run => run.CreatedAtUtc).Take(10).ToArray();
            if (recent.Any(run => run.TenantId != tenantId || !Guid.TryParseExact(run.RunId, "N", out _)))
                throw new InvalidDataException("A stored diagnostic run does not match the selected workspace.");
            foreach (var snapshot in recent) await WriteAsync(output, "Stored", snapshot, StoredCode(snapshot));
            if (recent.Length == 0)
                await output.WriteLineAsync(Prefix + """{"phase":"Stored","status":"NotFound"}""");
            return recent.Length > 0 ? 0 : 2;
        }
        var source = await store.GetRunAsync(tenantId, runId, deadline.Token);
        if (source is null)
        {
            await output.WriteLineAsync(Prefix + """{"phase":"Stored","status":"NotFound"}""");
            return 2;
        }
        if (source.TenantId != tenantId || source.RunId != runId)
            throw new InvalidDataException("The stored diagnostic run does not match the selected tenant/run.");
        await WriteAsync(output, "Stored", source, StoredCode(source));
        if (!replay) return 0;
        if (source.Status != JobStatus.Failed || source.Actor is null ||
            source.ReplyChannel.ChannelType != AskAiChannelType.Web ||
            !String.IsNullOrWhiteSpace(source.ReplyChannel.CallbackWebhookUrl) ||
            source.ChannelDelivery is not null || source.PendingEmail is not null ||
            !orchestrator.CanAutomaticallyReplay)
        {
            await output.WriteLineAsync(Prefix + """{"phase":"Replay","status":"Refused"}""");
            return 2;
        }
        var run = new AskAiRun
        {
            RunId = Guid.NewGuid().ToString("N"),
            TenantId = source.TenantId,
            Actor = source.Actor,
            Question = source.Question,
            ReplyChannel = new AskAiReplyChannel { ChannelType = AskAiChannelType.Web }
        };
        try
        {
            await orchestrator.ExecuteRunAsync(run, cancellationToken: deadline.Token);
            await WriteAsync(output, "Replay", run, "None");
            return 0;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await WriteAsync(output, "Replay", run, "TimedOut");
            return 1;
        }
        catch (UnauthorizedAccessException)
        {
            await WriteAsync(output, "Replay", run, "AccessRevoked");
            return 1;
        }
        catch (Exception exception)
        {
            run.ProviderFailure = AskAiProviderFailure.FromException(exception);
            await WriteAsync(output, "Replay", run, AskAiFailureDiagnostics.GetCode(exception));
            return 1;
        }
    }

    private static string StoredCode(AskAiRun source) =>
        source.ProviderFailure?.FailureCode ?? (source.ErrorCode is
            "ExecutionFailed" or "MissingEvidence" or "CitationMismatch" or
            "InvalidModelAnswer" or "UnknownCitation" or "InvalidToolArguments" or "RetrievalFailed" or "ToolCallLimit" or
            "EvidenceLimit" or "TimedOut" or "AccessRevoked" or "SourceChanged" or "ResponseTokenLimit" or
            "DataQueryRejected" or "DataAccessDenied" or "DataRateLimited" or "DataUnavailable" or
            "DataRequestFailed" ? source.ErrorCode : "StoredFailure");

    private static Task WriteAsync(TextWriter output, string phase, AskAiRun run, string failureCode) =>
        output.WriteLineAsync(Prefix + JsonSerializer.Serialize(new
        {
            phase,
            runId = run.RunId,
            createdAtUtc = run.CreatedAtUtc,
            completedAtUtc = run.CompletedAtUtc,
            status = phase == "Replay" && failureCode != "None" ? "Failed" : run.Status.ToString(),
            failureCode,
            httpStatus = run.ProviderFailure?.HttpStatus is >= 100 and <= 599 ? run.ProviderFailure.HttpStatus : null,
            lastStep = run.Steps.LastOrDefault()?.StepName is
                "search_documents" or "browse_documents" or "inspect_document_slice" or "analyze_evidence"
                ? run.Steps[^1].StepName : null,
            searches = run.Steps.Count(step => step.StepName == "search_documents"),
            inspections = run.Steps.Count(step => step.StepName == "inspect_document_slice"),
            analyticalAttempts = run.Steps.Count(step => step.StepName == "analyze_evidence"),
            analyses = run.Analyses.Count,
            hasBarChart = run.Analyses.Any(analysis => analysis.Visualization == "bar"),
            matchedDocuments = run.DocumentsEvaluated,
            citations = run.Citations.Count
        }));
}
