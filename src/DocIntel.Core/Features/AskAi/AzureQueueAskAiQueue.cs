using Azure.Storage.Queues;
using DocIntel.Core.Storage;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Features.AskAi;

/// <summary>Durable at-least-once dispatch; persisted snapshots remain the recovery source of truth.</summary>
public sealed class AzureQueueAskAiQueue(
    QueueClient queue,
    QueueClient poisonQueue,
    AskAiExecutionOptions options,
    ILogger<AzureQueueAskAiQueue> logger) : IAskAiQueue
{
    private readonly AzureQueueDispatch<AskAiQueueTicket> _dispatch =
        new(queue, poisonQueue, options.MaxRunSeconds, ValidateTicket, "Ask AI", logger);

    public ValueTask<bool> TryEnqueueAsync(AskAiQueueTicket ticket, CancellationToken cancellationToken = default) =>
        _dispatch.TryEnqueueAsync(ticket, cancellationToken);

    public IAsyncEnumerable<AskAiQueueTicket> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _dispatch.ReadAllAsync(cancellationToken);

    private static void ValidateTicket(AskAiQueueTicket? ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket.RunId);
        if (ticket.TenantId.Length > 512 || ticket.RunId.Length > 512)
            throw new ArgumentException("Dispatch identifiers exceed their supported length.");
    }
}
