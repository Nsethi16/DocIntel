namespace DocIntel.Core.Harness;

/// <summary>A stale configuration cannot overwrite the version another administrator saved.</summary>
public sealed class HarnessConflictException() : InvalidOperationException(
    "The harness changed. Compare your unsaved draft with the current server configuration before saving again.");
