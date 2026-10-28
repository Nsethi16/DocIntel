namespace DocIntel.Api.Endpoints;

/// <summary>Deduplicates actual graph entities without delimiter collisions in names or values.</summary>
internal sealed class EntityPairComparer : IEqualityComparer<(string Name, string Value)>
{
    internal static EntityPairComparer Instance { get; } = new();

    /// <inheritdoc />
    public bool Equals((string Name, string Value) x, (string Name, string Value) y) =>
        String.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase) &&
        String.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public int GetHashCode((string Name, string Value) pair) => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(pair.Name),
        StringComparer.OrdinalIgnoreCase.GetHashCode(pair.Value));
}
