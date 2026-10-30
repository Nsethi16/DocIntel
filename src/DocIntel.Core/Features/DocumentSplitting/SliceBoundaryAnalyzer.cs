using System.Text;
using System.Text.RegularExpressions;
using DocIntel.Core.Harness;

namespace DocIntel.Core.Features.DocumentSplitting;

/// <summary>
/// Deterministic and hybrid algorithms for normalizing OCR-garbled footers, detecting amendments,
/// grouping contiguous pages into <see cref="DocumentSlice"/> items, and reconciling with the tenant catalog.
/// </summary>
public static class SliceBoundaryAnalyzer
{
    private static readonly Regex PageFooterCommentRegex = new(
        @"<!--\s*PageFooter[:""\s]\s*([^"">]+?)\s*""?\s*-->",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ValidIdRegex = new(
        @"^(?:P-)?[A-Za-z]+(?:-[A-Za-z]+)*(?:-?[A-Za-z]*\d+[A-Za-z]*)?(?:\([A-Za-z]*\d+\))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AmendmentPrimaryRegex = new(
        @"(?:Amendment|Avenant|Enmienda|Aditamento|Nachtrag|Modificación)\s+ID\s+([A-Za-z]+-?[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*(?:\s*\([A-Za-z]\d+\))?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AmendmentSecondaryRegex = new(
        @"(?:Amendment|Avenant|Enmienda|Aditamento|Nachtrag|Modificación)\s+([A-Za-z]+-?[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*(?:\s*\([A-Za-z]\d+\))?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> ExcludedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Amendment", "Avenant", "Enmienda", "Aditamento", "Nachtrag",
        "to", "The", "will", "must", "be", "is", "of", "and", "or", "this", "that", "not", "ID"
    };

    public static List<DocumentSlice> GroupPagesHeuristically(
        IReadOnlyList<PageLayout> pages,
        SplittingConfig config,
        IReadOnlyList<string>? documentNames = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(config);

        if (pages.Count == 0)
        {
            return [];
        }

        BackfillCoverPageFooters(pages);

        IdentifyAmendmentPages(pages, config.AmendmentIndicators);

        if (config.EnableFooterNormalization)
        {
            NormalizePageFootersBySimilarity(pages, config.FooterNormalizationThreshold);
        }

        return ExtractSlices(pages, config.FooterBoundarySimilarityThreshold,
            documentNames ?? config.ExpectedDocumentTypes.Where(entry => entry.IsActive).Select(entry => entry.Name).ToArray());
    }

    /// <summary>
    /// Assigns footerless cover pages (e.g. Page 1 of a Customer Price Sheet that omits the footer on page 1
    /// while page 2 says "Page 2 of 6", or follows a page that said "Page 3 of 3") to the following document's footer.
    /// </summary>
    public static void BackfillCoverPageFooters(IReadOnlyList<PageLayout> pages)
    {
        for (var i = 0; i < pages.Count - 1; i++)
        {
            var current = pages[i];
            if (!string.IsNullOrWhiteSpace(current.FooterText))
            {
                continue;
            }

            var next = pages[i + 1];
            if (string.IsNullOrWhiteSpace(next.FooterText))
            {
                continue;
            }

            var prevEndedSubDoc = i > 0 &&
                                  pages[i - 1].SubDocPageNumber.HasValue &&
                                  pages[i - 1].SubDocTotalPages.HasValue &&
                                  pages[i - 1].SubDocPageNumber == pages[i - 1].SubDocTotalPages;

            var nextIsPageTwo = next.SubDocPageNumber == 2;

            var orientationShiftedToNext = i > 0 &&
                                           pages[i - 1].PageHeight > 0 &&
                                           current.PageHeight > 0 &&
                                           Math.Abs(current.PageHeight - pages[i - 1].PageHeight) > 50 &&
                                           Math.Abs(current.PageHeight - next.PageHeight) < 10;

            if (prevEndedSubDoc || nextIsPageTwo || orientationShiftedToNext)
            {
                current.FooterText = next.FooterText;
                current.Markdown = string.IsNullOrEmpty(current.Markdown)
                    ? $"<!-- PageFooter=\"{next.FooterText}\" -->"
                    : $"{current.Markdown}\n<!-- PageFooter=\"{next.FooterText}\" -->";
            }
        }
    }

    public static void IdentifyAmendmentPages(
        IReadOnlyList<PageLayout> pages,
        IReadOnlyList<string> amendmentIndicators)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(amendmentIndicators);

        var currentAmendmentType = string.Empty;
        var previousAmendmentFooter = string.Empty;

        foreach (var page in pages)
        {
            var compactFooter = page.FooterText.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            var isAmendment = amendmentIndicators.Any(ind =>
                !string.IsNullOrWhiteSpace(ind) &&
                (ind.Trim().Length <= 3
                    ? Regex.IsMatch(page.FooterText,
                        $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(ind.Trim())}(?=[-_,;\s]|\p{{L}}?\d|$)",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    : compactFooter.Contains(ind.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant(),
                        StringComparison.Ordinal)));

            if (!isAmendment)
            {
                continue;
            }

            page.IsAmendment = true;
            var footerIds = ExtractFooterIds(page.Markdown);

            if (footerIds.Length == 0 && !string.IsNullOrEmpty(page.FooterText))
            {
                var rawParts = page.FooterText.Split([',', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var validIds = rawParts.Where(IsValidFooterId).ToArray();
                if (validIds.Length > 0)
                {
                    footerIds = validIds;
                }
            }

            var extractedType = ExtractAmendmentType(page.Markdown.Length > 0 ? page.Markdown : page.BodyText, footerIds);

            if (!string.IsNullOrEmpty(extractedType))
            {
                currentAmendmentType = extractedType;
            }
            else if (!string.IsNullOrEmpty(page.FooterText) && !AreFootersSimilar(page.FooterText, previousAmendmentFooter))
            {
                currentAmendmentType = footerIds.Length > 0 ? footerIds[0] : page.FooterText;
            }

            if (!string.IsNullOrEmpty(page.FooterText))
            {
                previousAmendmentFooter = page.FooterText;
            }

            page.AmendmentId = currentAmendmentType;
        }
    }

    public static void NormalizePageFootersBySimilarity(
        IReadOnlyList<PageLayout> pages,
        double similarityThreshold = 0.85)
    {
        ArgumentNullException.ThrowIfNull(pages);

        var candidates = pages.Where(p => !p.IsAmendment && p.FooterText.Length <= 240 &&
            DocumentFooterKey(p.FooterText).Length > 0).ToList();
        if (candidates.Count <= 1)
        {
            return;
        }

        var clusters = new List<List<(string Footer, List<PageLayout> Pages)>>();

        foreach (var page in candidates)
        {
            var footer = page.FooterText;
            var matched = false;

            foreach (var cluster in clusters)
            {
                if (cluster.Any(e => AreFootersSimilar(footer, e.Footer, similarityThreshold)))
                {
                    var idx = cluster.FindIndex(e => string.Equals(e.Footer, footer, StringComparison.Ordinal));
                    if (idx >= 0)
                    {
                        cluster[idx].Pages.Add(page);
                    }
                    else
                    {
                        cluster.Add((footer, [page]));
                    }

                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                clusters.Add([(footer, [page])]);
            }
        }

        foreach (var cluster in clusters)
        {
            if (cluster.Count <= 1) continue;

            var canonical = cluster.MaxBy(e => e.Pages.Count).Footer;
            foreach (var entry in cluster)
            {
                if (string.Equals(entry.Footer, canonical, StringComparison.Ordinal)) continue;
                foreach (var page in entry.Pages)
                {
                    page.FooterText = canonical;
                }
            }
        }
    }

    public static List<DocumentSlice> ExtractSlices(
        IReadOnlyList<PageLayout> pages,
        double boundarySimilarityThreshold = 0.90,
        IReadOnlyList<string>? documentNames = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0) return [];
        var slices = new List<DocumentSlice>();
        var currentStart = pages[0].PageNumber;
        var currentIsAmendment = pages[0].IsAmendment;
        var currentFooter = pages[0].FooterText;
        var currentFooterKey = DocumentFooterKey(currentFooter);
        var currentAmendmentType = pages[0].AmendmentId;
        var currentType = HeaderDocumentType(pages[0].BodyText, documentNames ?? []);
        var currentRecords = HeaderRecordIds(pages[0].BodyText);
        var bodyBuilder = new StringBuilder();
        var markdownBuilder = new StringBuilder();
        var sliceFooters = new List<string>();

        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            var previous = index > 0 ? pages[index - 1] : null;
            var footerKey = DocumentFooterKey(page.FooterText);
            var type = HeaderDocumentType(page.BodyText, documentNames ?? []);
            var records = HeaderRecordIds(page.BodyText);
            var continuesPagination = previous?.SubDocPageNumber is { } previousNumber &&
                page.SubDocPageNumber == previousNumber + 1 &&
                page.SubDocTotalPages is { } total && total == previous.SubDocTotalPages;
            var paginationReset = page.SubDocPageNumber == 1 && previous?.SubDocPageNumber is { } preceding &&
                (preceding > 1 || previous.SubDocTotalPages == 1);
            var footerChanged = footerKey.Length > 0 && currentFooterKey.Length > 0 &&
                !continuesPagination && !AreFootersSimilar(footerKey, currentFooterKey, boundarySimilarityThreshold);
            var amendmentChanged = page.IsAmendment && !String.IsNullOrWhiteSpace(page.AmendmentId) &&
                                   !string.Equals(page.AmendmentId, currentAmendmentType, StringComparison.OrdinalIgnoreCase);
            var typeChanged = type is not null && currentType is not null &&
                !String.Equals(type, currentType, StringComparison.OrdinalIgnoreCase);
            var recordChanged = records.Any(record => currentRecords.TryGetValue(record.Key, out var value) &&
                !String.Equals(record.Value, value, StringComparison.OrdinalIgnoreCase));
            var isNewBoundary = index > 0 &&
                (footerChanged || amendmentChanged || paginationReset || typeChanged || recordChanged);

            if (isNewBoundary)
            {
                var activeFooter = !string.IsNullOrEmpty(currentAmendmentType) ? currentAmendmentType : currentFooter;
                slices.Add(BuildSlice(
                    slices.Count,
                    currentStart,
                    page.PageNumber - 1,
                    activeFooter,
                    sliceFooters,
                    currentIsAmendment,
                    currentAmendmentType,
                    bodyBuilder.ToString(),
                    markdownBuilder.ToString()));

                bodyBuilder.Clear();
                markdownBuilder.Clear();
                sliceFooters.Clear();

                currentStart = page.PageNumber;
                currentIsAmendment = page.IsAmendment;
                currentFooter = page.FooterText;
                currentFooterKey = footerKey;
                currentAmendmentType = page.AmendmentId;
                currentType = type;
                currentRecords.Clear();
            }

            if (currentFooterKey.Length == 0 && footerKey.Length > 0)
            {
                currentFooterKey = footerKey;
                currentFooter = page.FooterText;
            }
            currentType ??= type;
            foreach (var record in records) currentRecords[record.Key] = record.Value;
            if (!string.IsNullOrEmpty(page.AmendmentId))
            {
                currentAmendmentType = page.AmendmentId;
            }

            if (!string.IsNullOrWhiteSpace(page.FooterText) &&
                !sliceFooters.Contains(page.FooterText, StringComparer.OrdinalIgnoreCase))
            {
                sliceFooters.Add(page.FooterText);
            }

            if (bodyBuilder.Length > 0) bodyBuilder.AppendLine();
            bodyBuilder.Append(page.BodyText);

            if (markdownBuilder.Length > 0) markdownBuilder.Append("\n<!-- PageBreak -->\n");
            markdownBuilder.Append(string.IsNullOrEmpty(page.Markdown) ? page.BodyText : page.Markdown);
        }

        var finalFooter = !string.IsNullOrEmpty(currentAmendmentType) ? currentAmendmentType : currentFooter;
        slices.Add(BuildSlice(
            slices.Count,
            currentStart,
            pages[^1].PageNumber,
            finalFooter,
            sliceFooters,
            currentIsAmendment,
            currentAmendmentType,
            bodyBuilder.ToString(),
            markdownBuilder.ToString()));

        return slices;
    }

    private static string DocumentFooterKey(string footer)
    {
        var identifiers = new List<string>();
        foreach (var part in footer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = Regex.Replace(part, @"page\d+(?:of\d+)?", String.Empty, RegexOptions.IgnoreCase).Trim('-', ' ');
            if (value.Length is < 3 or > 120 || Regex.IsMatch(value, @"^\d+$") ||
                Regex.IsMatch(value, @"^(?:fig(?:ure)?|table|section|chapter|copyright|https?|www|doi)",
                    RegexOptions.IgnoreCase)) continue;
            var structuredId = Regex.IsMatch(value, @"^(?=.*\d)(?=.*[-_])[\p{L}\p{N}()._-]+$") ||
                Regex.IsMatch(value, @"^[A-Z]{2,12}[-_]?[A-Z0-9()._-]*\d[A-Z0-9()._-]*$");
            var formCode = Regex.IsMatch(value, @"^(?:Form[-_]|Document[A-Z]?\d|MSQuote|ProgramSignForm|VLAmendments)",
                RegexOptions.IgnoreCase) || value.Any(Char.IsDigit) &&
                Regex.IsMatch(value, @"Agreement|Enrollment|SignatureForm|PriceSheet|Addendum", RegexOptions.IgnoreCase);
            if (structuredId || formCode) identifiers.Add(value);
        }
        return String.Join(",", identifiers.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string? HeaderDocumentType(string text, IReadOnlyList<string> names)
    {
        var lines = PageTextLayout.LeadingLines(text, 4)
            .Select(line => Regex.Replace(line.Trim(), @"\s+", " ")).ToArray();
        return names.Where(name => !String.IsNullOrWhiteSpace(name)).OrderByDescending(name => name.Length)
            .FirstOrDefault(name => lines.Any(line => line.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)));
    }

    private static Dictionary<string, string> HeaderRecordIds(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in PageTextLayout.LeadingLines(text, 12))
        {
            var match = Regex.Match(line.Trim(),
                @"^(?:(?<kind>Quote|Invoice|Contract|Agreement|Document)\s*(?:Number|No\.?|ID)|" +
                @"N[úu]mero\s+de\s+(?<kind>contrato|factura))\b(?:\s*[:#]\s*|\s+)(?<id>[A-Z0-9][A-Z0-9._/-]{0,79})",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var kind = match.Groups["kind"].Value;
            if (kind.Equals("contrato", StringComparison.OrdinalIgnoreCase)) kind = "Contract";
            if (kind.Equals("factura", StringComparison.OrdinalIgnoreCase)) kind = "Invoice";
            result[kind] = match.Groups["id"].Value;
        }
        return result;
    }

    public static void ReconcileWithCatalog(
        IList<DocumentSlice> slices,
        IReadOnlyList<DocumentCatalogEntry> catalog,
        bool inferNameWhenNotInCatalog)
    {
        ArgumentNullException.ThrowIfNull(slices);
        ArgumentNullException.ThrowIfNull(catalog);

        var activeCatalog = catalog
            .Where(c => c.IsActive && !string.IsNullOrWhiteSpace(c.Name))
            .ToDictionary(c => c.Name.Trim(), c => c, StringComparer.OrdinalIgnoreCase);

        foreach (var slice in slices)
        {
            if (string.IsNullOrWhiteSpace(slice.DocumentTypeName))
            {
                slice.IsInferred = false;
                continue;
            }

            var trimmed = slice.DocumentTypeName.Trim();
            if (activeCatalog.TryGetValue(trimmed, out var matched))
            {
                slice.DocumentTypeName = matched.Name;
                slice.SchemaId = matched.SchemaId;
                slice.IsInferred = false;
            }
            else if (inferNameWhenNotInCatalog)
            {
                slice.IsInferred = true;
                slice.SchemaId = null;
            }
            else
            {
                slice.DocumentTypeName = null;
                slice.IsInferred = false;
                slice.SchemaId = null;
            }
        }
    }

    public static void HydrateHeuristicSlicesWithSemantic(
        IList<DocumentSlice> heuristicSlices,
        IReadOnlyList<DocumentSlice> semanticSlices)
    {
        if (heuristicSlices.Count == 0 || semanticSlices.Count == 0)
        {
            return;
        }

        var soleSemantic = semanticSlices.Count == 1 ? semanticSlices[0] : null;

        foreach (var slice in heuristicSlices)
        {
            var match = FindBestOverlapping(slice, semanticSlices)
                        ?? FindByFooter(slice, semanticSlices)
                        ?? soleSemantic;

            if (match is null) continue;

            if (!string.IsNullOrWhiteSpace(match.DocumentTypeName))
            {
                slice.DocumentTypeName = match.DocumentTypeName;
                slice.IsInferred = match.IsInferred;
            }

            if (!string.IsNullOrWhiteSpace(match.SchemaId))
            {
                slice.SchemaId = match.SchemaId;
            }
        }
    }

    public static bool AreFootersSimilar(string footer1, string footer2, double similarityThreshold = 0.90)
    {
        if (string.IsNullOrEmpty(footer1) || string.IsNullOrEmpty(footer2)) return false;
        if (string.Equals(footer1, footer2, StringComparison.OrdinalIgnoreCase)) return true;
        static string Numbers(string value) => String.Join("|", Regex.Matches(
            Regex.Replace(value, @"(?<=\d)[Oo](?=\d)", "0"), @"\d+").Select(match => match.Value));
        var firstNumbers = Numbers(footer1);
        var secondNumbers = Numbers(footer2);
        if (firstNumbers.Length > 0 && secondNumbers.Length > 0 && firstNumbers != secondNumbers) return false;
        if (footer1.Contains(footer2, StringComparison.OrdinalIgnoreCase) ||
            footer2.Contains(footer1, StringComparison.OrdinalIgnoreCase)) return true;

        return CalculateStringSimilarity(footer1.ToLowerInvariant(), footer2.ToLowerInvariant()) >= similarityThreshold;
    }

    public static double CalculateStringSimilarity(string source, string target)
    {
        if (string.IsNullOrEmpty(source) && string.IsNullOrEmpty(target)) return 1.0;
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return 0.0;

        var maxLen = Math.Max(source.Length, target.Length);
        var dist = Levenshtein(source, target);
        return 1.0 - ((double)dist / maxLen);
    }

    private static int Levenshtein(string s, string t)
    {
        var m = new int[s.Length + 1, t.Length + 1];
        for (var i = 0; i <= s.Length; i++) m[i, 0] = i;
        for (var j = 0; j <= t.Length; j++) m[0, j] = j;

        for (var i = 1; i <= s.Length; i++)
        {
            for (var j = 1; j <= t.Length; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                m[i, j] = Math.Min(Math.Min(m[i - 1, j] + 1, m[i, j - 1] + 1), m[i - 1, j - 1] + cost);
            }
        }

        return m[s.Length, t.Length];
    }

    private static DocumentSlice BuildSlice(
        int index,
        int startPage,
        int endPage,
        string activeFooter,
        List<string> footers,
        bool isAmendment,
        string? amendmentId,
        string body,
        string markdown)
    {
        var resolvedFooters = footers.Count > 0
            ? footers.ToArray()
            : (!string.IsNullOrEmpty(activeFooter) ? [activeFooter] : Array.Empty<string>());

        var inferredTitle = InferHeadingTitle(body, activeFooter, amendmentId);

        return new DocumentSlice
        {
            SegmentIndex = index,
            StartPage = startPage,
            EndPage = endPage,
            Footers = resolvedFooters,
            IsAmendment = isAmendment,
            AmendmentId = amendmentId,
            DocumentTypeName = inferredTitle,
            IsInferred = true,
            BodyText = body,
            Markdown = markdown
        };
    }

    private static string? InferHeadingTitle(string body, string activeFooter, string? amendmentId)
    {
        var topLines = (body ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("<!--", StringComparison.Ordinal))
            .Take(4)
            .ToList();

        string[] docKeywords =
        [
            "Price Sheet", "Signature Form", "Formulario de Firma", "Agreement",
            "Enrollment", "Amendment", "Addendum", "Statement of Work", "Invoice"
        ];

        var matchedLine = topLines.FirstOrDefault(l =>
            docKeywords.Any(k => l.Contains(k, StringComparison.OrdinalIgnoreCase)));

        if (!string.IsNullOrWhiteSpace(matchedLine))
        {
            return matchedLine.Contains(" - ", StringComparison.Ordinal)
                ? string.Join(" - ", matchedLine.Split(" - ", StringSplitOptions.TrimEntries).Skip(1))
                : matchedLine;
        }

        if (!string.IsNullOrEmpty(amendmentId))
        {
            return $"Amendment {amendmentId}";
        }

        return topLines.FirstOrDefault() ?? (!string.IsNullOrEmpty(activeFooter) ? activeFooter : null);
    }

    private static DocumentSlice? FindBestOverlapping(DocumentSlice target, IReadOnlyList<DocumentSlice> candidates)
    {
        DocumentSlice? best = null;
        var bestOverlap = 0;

        foreach (var c in candidates)
        {
            if (c.StartPage <= 0 || c.EndPage < c.StartPage) continue;
            var overlap = Math.Min(target.EndPage, c.EndPage) - Math.Max(target.StartPage, c.StartPage) + 1;
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = c;
            }
        }

        return best;
    }

    private static DocumentSlice? FindByFooter(DocumentSlice target, IReadOnlyList<DocumentSlice> candidates)
    {
        foreach (var tf in target.Footers)
        {
            var match = candidates.FirstOrDefault(c => c.Footers.Any(cf => AreFootersSimilar(tf, cf)));
            if (match is not null) return match;
        }

        return null;
    }

    private static string[] ExtractFooterIds(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return [];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in PageFooterCommentRegex.Matches(markdown))
        {
            var content = Regex.Replace(match.Groups[1].Value.Trim(), @"(\w)-\s+(\w)", "$1-$2");
            var candidates = content
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(s => s.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

            foreach (var c in candidates)
            {
                if (IsValidFooterId(c) && ValidIdRegex.IsMatch(c))
                {
                    ids.Add(c);
                }
            }
        }

        return ids.ToArray();
    }

    private static bool IsValidFooterId(string candidate) =>
        !string.IsNullOrEmpty(candidate) &&
        (candidate.Any(char.IsDigit) || candidate.Contains('-', StringComparison.Ordinal) ||
         string.Equals(candidate, "CTM", StringComparison.OrdinalIgnoreCase));

    private static string? ExtractAmendmentType(string text, string[] footerIds)
    {
        if (string.IsNullOrEmpty(text)) return null;

        string? fallback = null;
        foreach (var regex in new[] { AmendmentPrimaryRegex, AmendmentSecondaryRegex })
        {
            foreach (Match match in regex.Matches(text))
            {
                var candidate = match.Groups[1].Value.Replace(" ", string.Empty, StringComparison.Ordinal);
                if (ExcludedWords.Contains(candidate)) continue;

                if (footerIds.Length > 0)
                {
                    var exact = footerIds.FirstOrDefault(id => string.Equals(id, candidate, StringComparison.OrdinalIgnoreCase));
                    if (exact is not null) return exact;
                }
                else if (fallback is null && IsValidFooterId(candidate))
                {
                    fallback = candidate;
                }
            }
        }

        return fallback;
    }
}
