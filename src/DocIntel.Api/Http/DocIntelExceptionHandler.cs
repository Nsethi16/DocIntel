using DocIntel.Core.Harness;
using DocIntel.Core.Ocr;
using DocIntel.Core.Review;
using Microsoft.AspNetCore.Diagnostics;

namespace DocIntel.Api.Http;

/// <summary>Returns actionable status codes without exposing source data, credentials or provider diagnostics.</summary>
public sealed class DocIntelExceptionHandler(ILogger<DocIntelExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            return true;
        }
        var status = exception switch
        {
            BadHttpRequestException request => request.StatusCode,
            OcrBudgetExceededException => StatusCodes.Status429TooManyRequests,
            ReviewConflictException or HarnessConflictException => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };
        var cause = exception.GetBaseException();
        if (status >= 500)
            logger.LogError("Request {TraceId} failed with {ErrorType}; code {ErrorCode}; site {ErrorSite}.",
                context.TraceIdentifier, cause.GetType().Name, cause.HResult & 0xffff,
                cause.TargetSite?.DeclaringType?.Name + "." + cause.TargetSite?.Name);
        else
            logger.LogWarning("Request {TraceId} was rejected with status {StatusCode} and {ErrorType}.",
                context.TraceIdentifier, status, exception.GetType().Name);

        await CreateProblem(context, status).ExecuteAsync(context);
        return true;
    }

    public static IResult CreateProblem(HttpContext context, int status) =>
        Results.Problem(statusCode: status, title: status switch
        {
            400 => "Invalid request.",
            401 => "Sign-in is required.",
            403 => "Access is denied.",
            404 => "The resource is unavailable.",
            409 => "The resource changed.",
            413 => "The request body is too large.",
            429 => "The configured request allowance was exceeded.",
            _ => "The request could not be completed."
        },
            detail: status >= 500
                ? "Use the correlation ID when contacting your administrator. Internal diagnostics were not returned."
                : "Check the request or refresh the resource before trying again.",
            extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier });
}
