namespace DocIntel.Core.Harness;

/// <summary>Optional create-only workspace initialization; this configuration never grants tenant access.</summary>
public sealed record HarnessBootstrapOptions
{
    public const string SectionName = "HarnessBootstrap";

    /// <summary>A lowercase application workspace slug, not an Entra directory tenant ID.</summary>
    public string? TenantId { get; init; }

    /// <summary>The initial workspace name; configuration changes do not rename a persisted tenant.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Rejects incomplete or invalid customer settings before storage is accessed.</summary>
    public void Validate()
    {
        if (String.IsNullOrWhiteSpace(TenantId) && String.IsNullOrWhiteSpace(DisplayName)) return;
        if (String.IsNullOrWhiteSpace(TenantId) || String.IsNullOrWhiteSpace(DisplayName))
            throw new InvalidOperationException("HarnessBootstrap requires TenantId and DisplayName together.");
        if (TenantId.Length > 128 || TenantId[0] == '-' || TenantId[^1] == '-' ||
            TenantId.Contains("--", StringComparison.Ordinal) ||
            TenantId.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            throw new InvalidOperationException(
                "HarnessBootstrap:TenantId must be a lowercase alphanumeric slug of at most 128 characters, " +
                "with single hyphens between segments.");
        if (DisplayName.Length > 128 || DisplayName.Any(Char.IsControl))
            throw new InvalidOperationException(
                "HarnessBootstrap:DisplayName must contain at most 128 characters and no control characters.");
    }

    internal IReadOnlyList<TenantHarness> CreateMissingTenants(IEnumerable<string> persistedTenantIds)
    {
        var existing = persistedTenantIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!String.IsNullOrWhiteSpace(TenantId))
        {
            if (existing.Contains(TenantId)) return [];
            var tenant = new TenantHarness
            {
                TenantId = TenantId,
                DisplayName = DisplayName!.Trim(),
                AllowedModels = ["decision-1", "gpt-5-mini"]
            };
            TenantHarness.EnsureDefaultCapabilities(tenant);
            return [tenant];
        }

        var samples = FileHarnessStore.CreateDefaultTenants();
        // Resume an interrupted sample bootstrap, but do not inject samples into an existing customer installation.
        if (existing.Except(samples.Select(tenant => tenant.TenantId), StringComparer.OrdinalIgnoreCase).Any())
            return [];
        return samples.Where(tenant => !existing.Contains(tenant.TenantId)).ToList();
    }
}
