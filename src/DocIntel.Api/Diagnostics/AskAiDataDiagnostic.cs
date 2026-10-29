using System.Text.Json;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Jobs;

namespace DocIntel.Api.Diagnostics;

/// <summary>Read-only storage checks in the selected run's current authorization scope, with no model calls.</summary>
public static class AskAiDataDiagnostic
{
    /// <summary>Exercises fixed query shapes without replaying the question, returning documents or modifying a run.</summary>
    public static async Task<int> ExecuteAsync(
        string tenantId, string runId, IAskAiRunStore runs, ITenantAccessResolver access, IJobStore jobs,
        TextWriter output, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(tenantId) || tenantId.Length > 128 ||
            tenantId.Any(character => !Char.IsAsciiLetterOrDigit(character) && character != '-') ||
            !Guid.TryParseExact(runId, "N", out _))
            throw new ArgumentException("A workspace and opaque run ID are required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var variant = "Snapshot";
        try
        {
            var source = await runs.GetRunAsync(tenantId, runId, deadline.Token);
            if (source is null || source.TenantId != tenantId || source.RunId != runId || source.Actor is null)
            {
                await WriteAsync(output, variant, "Refused", "RunUnavailable");
                return 2;
            }
            var queries = new (string Name, DocumentQuery Query)[]
            {
                ("Plain", new() { PageSize = 1 }),
                ("Text", new() { PageSize = 1, SearchText = "USD" }),
                ("Type", new() { PageSize = 1, DocumentType = "Quote" }),
                ("Entity", new() { PageSize = 1, Entities = [new("Billing Currency", "USD")] }),
                ("Combined", new()
                {
                    PageSize = 1, SearchText = "USD", DocumentType = "Quote",
                    Entities = [new("Billing Currency", "USD")]
                })
            };
            var failed = false;
            foreach (var (name, query) in queries)
            {
                variant = name + "Access";
                var scope = await access.ResolveAsync(source.Actor, tenantId, deadline.Token);
                if (!scope.CanRead) throw new UnauthorizedAccessException();
                variant = name;
                try
                {
                    var page = await jobs.SearchDocumentsAsync(tenantId, query.WithAccess(scope.AllowedJobIds), deadline.Token);
                    await WriteAsync(output, variant, "Completed", "None", matches: page.Documents.Count);
                    if (page.ContinuationToken is not null)
                    {
                        scope = await access.ResolveAsync(source.Actor, tenantId, deadline.Token);
                        if (!scope.CanRead) throw new UnauthorizedAccessException();
                        variant = name + "Continuation";
                        var continuation = new DocumentQuery
                        {
                            PageSize = 1, SearchText = query.SearchText, DocumentType = query.DocumentType,
                            Entities = query.Entities, ContinuationToken = page.ContinuationToken
                        }.WithAccess(scope.AllowedJobIds);
                        var next = await jobs.SearchDocumentsAsync(tenantId, continuation, deadline.Token);
                        await WriteAsync(output, variant, "Completed", "None", matches: next.Documents.Count);
                    }
                }
                catch (Exception exception) when (AskAiDataFailure.FromException(exception) is not null)
                {
                    failed = true;
                    await WriteAsync(output, variant, "Failed", AskAiFailureDiagnostics.GetCode(exception),
                        AskAiDataFailure.FromException(exception));
                }
            }
            await WriteAsync(output, "Summary", failed ? "Failed" : "Completed", failed ? "DataCheckFailed" : "None");
            return failed ? 1 : 0;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await WriteAsync(output, variant, "Failed", "TimedOut");
            return 1;
        }
        catch (UnauthorizedAccessException)
        {
            await WriteAsync(output, variant, "Refused", "AccessRevoked");
            return 2;
        }
        catch (Exception exception)
        {
            await WriteAsync(output, variant, "Failed", AskAiFailureDiagnostics.GetCode(exception),
                AskAiDataFailure.FromException(exception));
            return 1;
        }
    }

    private static Task WriteAsync(
        TextWriter output, string variant, string status, string failureCode,
        AskAiDataFailure? failure = null, int matches = 0) =>
        output.WriteLineAsync("DOCINTEL_DIAGNOSTIC " + JsonSerializer.Serialize(new
        {
            phase = "Data", variant, status, failureCode, matches,
            httpStatus = failure?.HttpStatus, subStatus = failure?.SubStatus
        }));
}
