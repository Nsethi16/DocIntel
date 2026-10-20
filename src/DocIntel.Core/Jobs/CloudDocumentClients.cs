using Azure.Storage.Queues;

namespace DocIntel.Core.Jobs;

/// <summary>Service-owned durable document dispatch and poison resources.</summary>
public sealed record CloudDocumentClients(QueueClient Dispatch, QueueClient Poison);
