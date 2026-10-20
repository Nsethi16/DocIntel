using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.AskAi;

namespace DocIntel.Core.Channels;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChannelDeliveryStatus { None, Pending, Sending, Sent, OutcomeUnknown, Failed }

/// <summary>Server-owned destination captured only by a verified channel adapter, never a user callback URL.</summary>
public sealed class ChannelDelivery
{
    public required string Transport { get; init; }
    public required string Profile { get; init; }
    public required string DestinationId { get; init; }
    public string? MessageId { get; init; }
    public ChannelDeliveryStatus Status { get; set; } = ChannelDeliveryStatus.Pending;
    public DateTimeOffset? AttemptedAtUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }
    public string? ErrorCode { get; set; }
    public string? AccessFingerprint { get; set; }

    /// <summary>Conservatively invalidates delayed answers when their original permission scope changes.</summary>
    public static string GetAccessFingerprint(DocumentAccessScope scope) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            scope.Role,
            Jobs = scope.AllowedJobIds?.Order(StringComparer.Ordinal).ToArray()
        })));
}

/// <summary>Performs one bounded external send to a trusted configured channel destination.</summary>
public interface IChannelTransport
{
    string Name { get; }
    Task SendAsync(AskAiRun run, ChannelDelivery target, CancellationToken cancellationToken = default);
}
