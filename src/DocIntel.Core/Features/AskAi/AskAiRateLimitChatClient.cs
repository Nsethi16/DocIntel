using System.ClientModel;
using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Features.AskAi;

/// <summary>Retries only rejected, transient model requests; never repeats an agent run or a tool invocation.</summary>
public sealed class AskAiRateLimitChatClient(
    IChatClient inner, ILogger logger,
    Func<TimeSpan, CancellationToken, Task>? onThrottle = null,
    Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
    TimeProvider? clock = null) : DelegatingChatClient(inner)
{
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(180);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToArray();
        var waited = TimeSpan.Zero;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await base.GetResponseAsync(snapshot, options, cancellationToken);
                if (response.FinishReason == ChatFinishReason.Length)
                    throw new InvalidOperationException("The model exhausted the response token limit.");
                return response;
            }
            catch (Exception exception) when (
                attempt < 3 && !cancellationToken.IsCancellationRequested &&
                AskAiProviderFailure.FromException(exception)?.FailureCode == "ProviderRateLimited")
            {
                var delay = RetryDelay(exception, attempt);
                if (delay > MaximumWait - waited) throw;
                waited += delay;
                logger.LogWarning("Ask AI provider throttled a model request; waiting {Seconds} seconds for retry {Attempt}.",
                    delay.TotalSeconds, attempt + 1);
                if (onThrottle is not null) await onThrottle(delay, cancellationToken);
                if (delayAsync is not null) await delayAsync(delay, cancellationToken);
                else await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private TimeSpan RetryDelay(Exception exception, int attempt)
    {
        ClientResultException? provider = null;
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is ClientResultException { Status: 429 } result) { provider = result; break; }
        var headers = provider?.GetRawResponse()?.Headers;
        var delays = new List<TimeSpan>();
        if (headers is not null)
        {
            if (headers.TryGetValue("x-should-retry", out var retry) &&
                String.Equals(retry, "false", StringComparison.OrdinalIgnoreCase))
                return TimeSpan.MaxValue;
            foreach (var header in new[] { "retry-after-ms", "x-ms-retry-after-ms", "Retry-After" })
            {
                if (!headers.TryGetValue(header, out var value)) continue;
                if (Double.TryParse(value, NumberStyles.AllowDecimalPoint |
                    NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture,
                    out var number) && Double.IsFinite(number) && number >= 0)
                {
                    var seconds = header == "Retry-After" ? number : number / 1_000;
                    delays.Add(seconds > MaximumWait.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds));
                }
                else if (header == "Retry-After" &&
                    DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var date))
                {
                    delays.Add(date > _clock.GetUtcNow() ? date - _clock.GetUtcNow() : TimeSpan.Zero);
                }
            }
        }
        return delays.Count > 0 ? delays.Max() :
            TimeSpan.FromSeconds(5 * Math.Pow(2, attempt) + Random.Shared.NextDouble());
    }
}
