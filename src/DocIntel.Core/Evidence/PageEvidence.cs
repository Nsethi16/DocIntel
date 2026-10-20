using System.Text.Json.Serialization;
using DocIntel.Core.Features.DocumentSplitting;

namespace DocIntel.Core.Evidence;

/// <summary>A page polygon in normalized, top-left-origin coordinates, preserving its four actual corners.</summary>
public sealed record BoundingBox
{
    [JsonConstructor]
    public BoundingBox(int page, IReadOnlyList<double> polygon)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentNullException.ThrowIfNull(polygon);
        if (polygon.Count != 8 || polygon.Any(value => !Double.IsFinite(value) || value is < 0 or > 1))
            throw new ArgumentException("A page polygon requires eight finite coordinates between zero and one.");
        Page = page;
        Polygon = Array.AsReadOnly(polygon.ToArray());
    }

    public int Page { get; }
    public IReadOnlyList<double> Polygon { get; }
}

/// <summary>A source word; native PDF text has no invented OCR confidence.</summary>
public sealed record PageWord(string Id, string Text, BoundingBox Box, double? OcrConfidence = null);

/// <summary>Versioned, source-bound layout stored separately from lightweight job metadata.</summary>
public sealed record DocumentLayout(
    string TenantId, string DocumentId, string OriginalSha256, IReadOnlyList<PageLayout> Pages, int SchemaVersion = 1);
