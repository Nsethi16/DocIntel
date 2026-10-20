using System.Text;
using System.Text.Json.Serialization;
using DocIntel.Core.Features.DocumentSplitting;

namespace DocIntel.Core.Evidence;

/// <summary>Describes source-text occurrence, not whether a model understood the field correctly.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GroundingStatus { Unavailable, ExactTextMatch, Ambiguous, NotFound, ReferenceLimitExceeded }

/// <summary>Server-assigned original word identities and polygons for a unique text occurrence.</summary>
public sealed record ValueProvenance(
    GroundingStatus Status, IReadOnlyList<string> WordIds, IReadOnlyList<BoundingBox> Boxes, string? Excerpt)
{
    public bool Grounded => Status == GroundingStatus.ExactTextMatch;
}

/// <summary>Separate, uncalibrated model and text-presence signals; they are deliberately not averaged.</summary>
public sealed record ConfidenceSignals(double? ModelScore, double? GroundingScore, bool Calibrated = false);

/// <summary>Finds unique whole-word occurrences within original pages, with whitespace-only normalization.</summary>
public sealed class ValueGrounding
{
    private readonly IReadOnlyList<IndexedPage> _pages;

    public ValueGrounding(IReadOnlyList<PageLayout> pages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        _pages = pages.Select(page =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = page.Words
                .GroupBy(word => Math.Round((1 - word.Box.Polygon.Where((_, index) => index % 2 == 1).Max()) *
                    page.PageHeight / 3) * 3)
                .OrderByDescending(line => line.Key)
                .SelectMany(line => line.OrderBy(word => word.Box.Polygon.Where((_, index) => index % 2 == 0).Min()))
                .ToList();
            var text = new StringBuilder();
            var words = new List<IndexedWord>();
            foreach (var word in ordered)
            {
                var value = Normalize(word.Text);
                if (value.Length == 0) continue;
                if (text.Length > 0) text.Append(' ');
                words.Add(new IndexedWord(word, text.Length, text.Length + value.Length));
                text.Append(value);
            }
            return new IndexedPage(page.PageNumber, text.ToString(), words,
                words.Select((word, index) => (word.Start, Index: index)).ToDictionary(item => item.Start, item => item.Index),
                words.Select((word, index) => (word.End, Index: index)).ToDictionary(item => item.End, item => item.Index),
                words.Count == 0 && (!String.IsNullOrEmpty(page.BodyText) ||
                    page.ImageAreaRatio > 0.5 && page.TextSource != "Ocr"));
        }).ToList();
    }

    public ValueProvenance Find(string? value, int startPage, int endPage, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pages = _pages.Where(page => page.Number >= startPage && page.Number <= endPage).ToList();
        if (startPage < 1 || endPage < startPage || pages.Count != (long)endPage - startPage + 1 ||
            pages.Any(page => page.Incomplete) || pages.All(page => page.Words.Count == 0))
            return new(GroundingStatus.Unavailable, [], [], null);
        var query = Normalize(value);
        if (query.Length == 0) return new(GroundingStatus.NotFound, [], [], null);
        List<IndexedWord>? match = null;
        string? excerpt = null;
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cursor = 0;
            while (cursor <= page.Text.Length - query.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = page.Text.IndexOf(query, cursor, StringComparison.OrdinalIgnoreCase);
                if (offset < 0) break;
                cursor = offset + 1;
                if (!page.Starts.TryGetValue(offset, out var first) ||
                    !page.Ends.TryGetValue(offset + query.Length, out var last) || last < first) continue;
                if (match is not null) return new(GroundingStatus.Ambiguous, [], [], null);
                match = page.Words.GetRange(first, last - first + 1);
                var length = Math.Min(query.Length, 300);
                if (length < query.Length && Char.IsHighSurrogate(page.Text[offset + length - 1])) length--;
                excerpt = page.Text.Substring(offset, length);
            }
        }
        if (match is { Count: > 64 }) return new(GroundingStatus.ReferenceLimitExceeded, [], [], null);
        return match is null ? new(GroundingStatus.NotFound, [], [], null) :
            new(GroundingStatus.ExactTextMatch, match.Select(word => word.Word.Id).ToArray(),
                match.Select(word => word.Word.Box).ToArray(),
                excerpt);
    }

    public static ConfidenceSignals Signals(double score, double scale, ValueProvenance provenance)
    {
        if (!Double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        return new ConfidenceSignals(
            Double.IsFinite(score) && score >= 0 && score <= scale ? score / scale : null,
            provenance.Status switch { GroundingStatus.ExactTextMatch => 1, GroundingStatus.NotFound => 0, _ => null });
    }

    private static string Normalize(string? value)
    {
        if (String.IsNullOrWhiteSpace(value)) return String.Empty;
        var result = new StringBuilder(value.Length);
        var space = false;
        foreach (var character in value)
        {
            if (Char.IsWhiteSpace(character)) { space = result.Length > 0; continue; }
            if (space) result.Append(' ');
            result.Append(character);
            space = false;
        }
        return result.ToString();
    }

    private sealed record IndexedWord(PageWord Word, int Start, int End);
    private sealed record IndexedPage(int Number, string Text, List<IndexedWord> Words,
        Dictionary<int, int> Starts, Dictionary<int, int> Ends, bool Incomplete);
}
