using Azure.Storage.Blobs;
using Azure.Storage.Queues;

namespace DocIntel.Core.Features.AskAi;

/// <summary>Service-owned storage resources provisioned separately from request processing.</summary>
public sealed record CloudAskAiClients(BlobContainerClient Runs, QueueClient Dispatch, QueueClient Poison);
