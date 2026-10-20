using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Storage;

/// <summary>Shared at-least-once transport; fenced persisted work remains the recovery source of truth.</summary>
internal sealed class AzureQueueDispatch<T>(
    QueueClient queue, QueueClient poison, int maxRunSeconds, Action<T?> validate, string kind, ILogger logger)
    where T : class
{
    public async ValueTask<bool> TryEnqueueAsync(T ticket, CancellationToken ct)
    {
        validate(ticket);
        var body = BinaryData.FromObjectAsJson(ticket);
        if (body.ToMemory().Length > 4_096) throw new ArgumentException("Dispatch ticket exceeds its size limit.");
        try
        {
            await queue.SendMessageAsync(body, null, TimeSpan.FromDays(7), ct);
            return true;
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            logger.LogWarning("{DispatchKind} dispatch is unavailable; persisted work awaits recovery: {ErrorType}.",
                kind, exception.GetType().Name);
            return false;
        }
    }

    public async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            QueueMessage[] messages;
            try
            {
                messages = (await queue.ReceiveMessagesAsync(1, TimeSpan.FromSeconds(maxRunSeconds + 120), ct)).Value;
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                logger.LogWarning("{DispatchKind} receiver is unavailable: {ErrorType}.", kind, exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }
            if (messages.Length == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }
            foreach (var message in messages)
            {
                T? ticket = null;
                try
                {
                    if (message.Body.ToMemory().Length > 4_096)
                        throw new InvalidDataException("Dispatch ticket exceeds its size limit.");
                    ticket = message.Body.ToObjectFromJson<T>();
                    validate(ticket);
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
                {
                    ticket = null;
                    logger.LogError("An invalid {DispatchKind} dispatch ticket was rejected.", kind);
                }
                if (ticket is null || message.DequeueCount > 5)
                {
                    try
                    {
                        await poison.SendMessageAsync(message.Body, null, TimeSpan.FromDays(7), ct);
                        await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct);
                    }
                    catch (Exception exception) when (IsTransportFailure(exception))
                    {
                        logger.LogWarning("{DispatchKind} poison handling is unavailable: {ErrorType}.",
                            kind, exception.GetType().Name);
                    }
                    continue;
                }
                yield return ticket;
                try { await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct); }
                catch (Exception exception) when (IsTransportFailure(exception))
                {
                    logger.LogWarning("{DispatchKind} acknowledgement failed; a processing lease must fence redelivery: " +
                        "{ErrorType}.", kind, exception.GetType().Name);
                }
            }
        }
    }

    private static bool IsTransportFailure(Exception exception) =>
        exception is RequestFailedException or HttpRequestException or AuthenticationFailedException;
}
