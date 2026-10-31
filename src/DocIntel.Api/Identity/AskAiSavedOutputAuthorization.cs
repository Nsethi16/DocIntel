using DocIntel.Core.Authorization;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Jobs;
using Microsoft.AspNetCore.Http;

namespace DocIntel.Api.Identity;

/// <summary>Suppresses unavailable saved output with fixed, content-free API errors.</summary>
internal static class AskAiSavedOutputAuthorization
{
    internal static bool HasSavedOutput(AskAiRun run) =>
        run.Status == JobStatus.Completed || !String.IsNullOrEmpty(run.Answer) ||
        run.Citations.Count > 0 || run.Analyses.Count > 0;

    internal static async Task<int> GetStatusAsync(
        AskAiRun run, IJobStore jobs, DocumentAccessScope access, CancellationToken cancellationToken = default)
    {
        if (!access.CanRead) return StatusCodes.Status404NotFound;
        if (!HasSavedOutput(run)) return 0;
        try
        {
            await AskAiSavedOutputValidator.ValidateAsync(run, jobs, access, cancellationToken);
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCodes.Status404NotFound;
        }
        catch (AskAiSourceChangedException)
        {
            return StatusCodes.Status409Conflict;
        }
    }

    internal static IResult Denied(int status) => status == StatusCodes.Status409Conflict
        ? Results.Conflict(new
        {
            error = AskAiFailureDiagnostics.GetMessage("SourceChanged"),
            errorCode = "SourceChanged"
        })
        : Results.Json(new
        {
            error = status == StatusCodes.Status404NotFound
                ? "The requested resource is not available."
                : "Access to this operation is not permitted."
        }, statusCode: status);
}
