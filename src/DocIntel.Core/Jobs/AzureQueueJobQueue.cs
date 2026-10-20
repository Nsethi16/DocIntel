using Azure.Storage.Queues;
using DocIntel.Core.Storage;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Jobs;

/// <summary>Durable metadata-only document dispatch; job-store versions fence duplicate deliveries.</summary>
public sealed class AzureQueueJobQueue(
    QueueClient queue, QueueClient poison, DocumentProcessingOptions options,
    ILogger<AzureQueueJobQueue> logger) : IJobQueue
{
    private readonly AzureQueueDispatch<JobQueueTicket> _dispatch =
        new(queue, poison, options.MaxRunSeconds, Validate, "Document", logger);

    public ValueTask<bool> TryEnqueueAsync(JobQueueTicket ticket, CancellationToken cancellationToken = default) =>
        _dispatch.TryEnqueueAsync(ticket, cancellationToken);

    public IAsyncEnumerable<JobQueueTicket> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _dispatch.ReadAllAsync(cancellationToken);

    private static void Validate(JobQueueTicket? ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket.JobId);
        if (ticket.TenantId.Length > 512 || ticket.JobId.Length > 128)
            throw new ArgumentException("Document dispatch identifiers exceed their supported length.");
    }
}
