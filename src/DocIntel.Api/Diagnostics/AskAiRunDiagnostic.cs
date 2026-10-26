using System.Text.Json;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Jobs;

namespace DocIntel.Api.Diagnostics;

/// <summary>Operator-only CLI diagnostics; no public endpoint, stored-run mutation or channel delivery.</summary>
public static class AskAiRunDiagnostic
{
    private const string Prefix = "DOCINTEL_DIAGNOSTIC ";

    /// <summary>Inspects a stored run or replays a failed web question in memory with its original actor.</summary>
    public static async Task<int> ExecuteAsync(string tenantId, string runId, bool replay,
        IAskAiRunStore store, AskAiOrchestrator orchestrator, TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (tenantId.Length is < 1 or > 128 ||
            tenantId.Any(character => !Char.IsAsciiLetterOrDigit(character) && character != '-') ||
            !Guid.TryParseExact(runId, "N", out _))
            throw new ArgumentException("A valid tenant ID and opaque run ID are required.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var source = await store.GetRunAsync(tenantId, runId, deadline.Token);
        if (source is null)
        {
            await output.WriteLineAsync(Prefix + """{"phase":"Stored","status":"NotFound"}""");
            return 2;
        }
        if (source.TenantId != tenantId || source.RunId != runId)
            throw new InvalidDataException("The stored diagnostic run does not match the selected tenant/run.");
        var code = source.ErrorCode is "ExecutionFailed" or "MissingEvidence" or "CitationMismatch" or
            "InvalidModelAnswer" or "InvalidToolArguments" or "RetrievalFailed" or "ToolCallLimit" or
            "EvidenceLimit" or "TimedOut" or "AccessRevoked" ? source.ErrorCode : "StoredFailure";
        await WriteAsync(output, "Stored", source, code);
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
            await WriteAsync(output, "Replay", run, AskAiFailureDiagnostics.GetCode(exception));
            return 1;
        }
    }

    private static Task WriteAsync(TextWriter output, string phase, AskAiRun run, string failureCode) =>
        output.WriteLineAsync(Prefix + JsonSerializer.Serialize(new
        {
            phase,
            status = phase == "Replay" && failureCode != "None" ? "Failed" : run.Status.ToString(),
            failureCode,
            searches = run.Steps.Count(step => step.StepName == "search_documents"),
            inspections = run.Steps.Count(step => step.StepName == "inspect_document_slice"),
            matchedDocuments = run.DocumentsEvaluated,
            citations = run.Citations.Count
        }));
}
