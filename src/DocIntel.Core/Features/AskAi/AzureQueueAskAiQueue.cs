using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Features.AskAi;

/// <summary>Durable at-least-once dispatch; persisted snapshots remain the recovery source of truth.</summary>
public sealed class AzureQueueAskAiQueue(
    QueueClient queue,
    QueueClient poisonQueue,
    AskAiExecutionOptions options,
    ILogger<AzureQueueAskAiQueue> logger) : IAskAiQueue
{
    public async ValueTask<bool> TryEnqueueAsync(
        AskAiQueueTicket ticket, CancellationToken cancellationToken = default)
    {
        ValidateTicket(ticket);
        try
        {
            await queue.SendMessageAsync(BinaryData.FromObjectAsJson(ticket),
                visibilityTimeout: null, timeToLive: TimeSpan.FromDays(7), cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is RequestFailedException or HttpRequestException or AuthenticationFailedException)
        {
            logger.LogWarning("Persisted run {RunId} could not be dispatched yet: {ErrorType}.",
                ticket.RunId, exception.GetType().Name);
            return false;
        }
    }

    public async IAsyncEnumerable<AskAiQueueTicket> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Azure.Storage.Queues.Models.QueueMessage[] messages;
            try
            {
                messages = (await queue.ReceiveMessagesAsync(1,
                    TimeSpan.FromSeconds(options.MaxRunSeconds + 120), cancellationToken)).Value;
            }
            catch (Exception exception) when (exception is RequestFailedException or HttpRequestException or AuthenticationFailedException)
            {
                logger.LogWarning("Ask AI dispatch is unavailable: {ErrorType}.", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                continue;
            }
            if (messages.Length == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }
            foreach (var message in messages)
            {
                AskAiQueueTicket? ticket = null;
                try
                {
                    if (message.Body.ToMemory().Length > 4_096)
                        throw new InvalidDataException("Dispatch ticket exceeds its size limit.");
                    ticket = message.Body.ToObjectFromJson<AskAiQueueTicket>();
                    ValidateTicket(ticket);
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
                {
                    ticket = null;
                    logger.LogError("An invalid Ask AI dispatch ticket was rejected.");
                }
                if (ticket is null || message.DequeueCount > 5)
                {
                    try
                    {
                        await poisonQueue.SendMessageAsync(message.Body,
                            null, TimeSpan.FromDays(7), cancellationToken);
                        await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
                    }
                    catch (Exception exception) when (exception is RequestFailedException or HttpRequestException or AuthenticationFailedException)
                    {
                        logger.LogWarning("Poison dispatch handling is unavailable: {ErrorType}.", exception.GetType().Name);
                    }
                    continue;
                }
                yield return ticket;
                try
                {
                    await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
                }
                catch (Exception exception) when (exception is RequestFailedException or HttpRequestException)
                {
                    logger.LogWarning("Could not acknowledge run {RunId}; duplicate delivery is fenced by its snapshot lease.",
                        ticket.RunId);
                }
            }
        }
    }

    private static void ValidateTicket(AskAiQueueTicket? ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket.RunId);
        if (ticket.TenantId.Length > 512 || ticket.RunId.Length > 512)
            throw new ArgumentException("Dispatch identifiers exceed their supported length.");
    }
}
