namespace DocIntel.Core.Jobs;

/// <summary>Deterministic document limits, distinct from a competing owner's version conflict.</summary>
public sealed class DocumentStorageLimitException(string code, string message) : InvalidOperationException(message)
{
    public const string SearchMetadataTooLarge = "DocumentSearchMetadataLimitExceeded";
    public const string ContentTooLarge = "DocumentContentLimitExceeded";
    public const string CosmosPayloadTooLarge = "DocumentMetadataLimitExceeded";

    public string Code { get; } = code;

    /// <summary>True only after a fenced terminal document record was durably acknowledged.</summary>
    public bool IsDurablyPersisted { get; internal set; }
}
