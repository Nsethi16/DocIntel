namespace DocIntel.Core.Ocr;

/// <summary>Tenant-, input-, and provider-bound recognition output; never an authorization grant.</summary>
public sealed record OcrCacheEntry(
    string TenantId, string OriginalSha256, string ProviderFingerprint, OcrPageResult Result, int SchemaVersion = 1);
