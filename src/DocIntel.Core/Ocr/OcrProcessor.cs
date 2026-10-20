using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DocIntel.Core.Evidence;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Storage;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Ocr;

/// <summary>Recognizes only the pages required by the accepted policy, then stages validated source text.</summary>
public sealed class OcrProcessor(
    ILogger<OcrProcessor> logger, IOcrProvider? provider = null, IArtifactStore? artifacts = null,
    IOcrUsageStore? usage = null, TimeProvider? clock = null)
{
    private readonly IOcrUsageStore _usage = usage ?? new InMemoryOcrUsageStore();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task EnrichAsync(SplittingState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var policy = state.Tenant.Ocr ?? throw new InvalidDataException("The accepted OCR policy is missing.");
        policy.Validate();
        if (policy.Mode == OcrMode.Never) return;
        var selected = state.Pages.Where(page => policy.Mode == OcrMode.Always ||
            page.BodyText.Length < 50 && page.ImageAreaRatio > 0.5).ToList();
        if (selected.Count == 0) return;
        if (provider is null)
            throw new InvalidOperationException("Scanned pages require a server-configured OCR provider.");
        var original = state.SourcePdfBytes
            ?? throw new InvalidDataException("OCR requires the actual original PDF.");
        LocalFileArtifactStore.ValidateOriginal(original);
        var watch = Stopwatch.StartNew();
        var originalSha256 = Convert.ToHexString(SHA256.HashData(original));
        var fingerprint = artifacts is not null && !String.IsNullOrWhiteSpace(provider.CacheIdentity)
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(provider.CacheIdentity))) : null;
        var results = new List<OcrPageResult>();
        var hits = new HashSet<int>();
        if (fingerprint is not null)
        {
            foreach (var page in selected)
            {
                var cached = await artifacts!.GetOcrCacheAsync(state.Tenant.TenantId, originalSha256,
                    fingerprint, page.PageNumber, cancellationToken);
                if (cached is null) continue;
                results.Add(cached);
                hits.Add(page.PageNumber);
            }
        }
        var requested = selected.Where(page => !hits.Contains(page.PageNumber)).Select(page => page.PageNumber).ToList();
        IReadOnlyList<OcrPageResult> recognized = [];
        if (requested.Count > 0)
        {
            OcrAllowance allowance;
            try
            {
                allowance = await _usage.ReserveAsync(state.Tenant.TenantId,
                    _clock.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture), Guid.NewGuid().ToString("N"),
                    requested.Count, provider.Name, state.DocumentId, cancellationToken);
            }
            catch (OcrBudgetExceededException)
            {
                logger.LogWarning("OCR request rejected because the tenant's monthly page allowance is exhausted.");
                throw;
            }
            var threshold = (long)allowance.Budget.MonthlyPageLimit * allowance.Budget.WarningPercent;
            if (allowance.Usage.ReservedPages * 100 >= threshold &&
                (allowance.Usage.ReservedPages - requested.Count) * 100 < threshold)
                logger.LogWarning("OCR usage crossed its configured {WarningPercent} percent monthly warning threshold.",
                    allowance.Budget.WarningPercent);
            recognized = await provider.RecognizeAsync(original, requested, cancellationToken);
            if (recognized is null || recognized.Count != requested.Count || recognized.Any(result => result is null) ||
                recognized.Select(result => result.PageNumber).Distinct().Count() != requested.Count ||
                recognized.Any(result => !requested.Contains(result.PageNumber)))
                throw new InvalidDataException("OCR did not return exactly the requested uncached source pages.");
            results.AddRange(recognized);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (results is null || results.Count != selected.Count || results.Any(result => result is null) ||
            results.Select(result => result.PageNumber).Distinct().Count() != selected.Count ||
            results.Any(result => !selected.Any(page => page.PageNumber == result.PageNumber)))
            throw new InvalidDataException("OCR did not return exactly the requested source pages.");
        var staged = new Dictionary<int, PageLayout>();
        var footerRules = state.Tenant.Capabilities.TryGetValue(FeatureHarness.SplitAndGroupKey, out var split)
            ? split.SplitAndGroup?.FootersToIgnore ?? [] : [];
        foreach (var result in results)
        {
            var source = selected.Single(page => page.PageNumber == result.PageNumber);
            if (!Double.IsFinite(result.PageWidth) || result.PageWidth <= 0 ||
                !Double.IsFinite(result.PageHeight) || result.PageHeight <= 0 || result.ProviderName != provider.Name ||
                Math.Abs(result.PageWidth / result.PageHeight / (source.PageWidth / source.PageHeight) - 1) > 0.02)
                throw new InvalidDataException("OCR geometry does not match the original page orientation or provider.");
            var page = new PageLayout
            {
                PageNumber = source.PageNumber,
                PageWidth = source.PageWidth,
                PageHeight = source.PageHeight,
                ImageAreaRatio = source.ImageAreaRatio,
                TextSource = "Ocr",
                TextProvider = result.ProviderName,
                OcrCacheHit = hits.Contains(result.PageNumber),
                Words = result.Words,
                IsAmendment = source.IsAmendment,
                AmendmentId = source.AmendmentId
            };
            if (page.Words is null || page.Words.Any(word => word?.Box is null))
                throw new InvalidDataException("OCR returned incomplete source words.");
            PageTextLayout.Apply(page, page.Words.Select(word => new LayoutTextWord(word.Text,
                word.Box.Polygon.Where((_, index) => index % 2 == 0).Min() * page.PageWidth,
                (1 - word.Box.Polygon.Where((_, index) => index % 2 == 1).Max()) * page.PageHeight)), footerRules);
            staged.Add(page.PageNumber, page);
        }
        var pages = state.Pages.Select(page => staged.GetValueOrDefault(page.PageNumber) ?? page).ToList();
        DocumentLayoutCodec.Validate(new DocumentLayout(state.Tenant.TenantId, state.DocumentId, originalSha256, pages));
        if (fingerprint is not null)
        {
            foreach (var result in recognized)
                await artifacts!.TrySaveOcrCacheAsync(new OcrCacheEntry(
                    state.Tenant.TenantId, originalSha256, fingerprint, result), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        state.Pages = pages;
        state.ExecutionTrace.Add(new WorkflowStepTrace("RecognizeScannedPages", true, true, provider.Name,
            watch.Elapsed.TotalMilliseconds,
            $"Recognized {recognized.Count} source page(s); reused {hits.Count} cached page(s)."));
        logger.LogInformation("Recognized {PageCount} pages using {OcrProvider}; reused {CachedPageCount} cached pages.",
            recognized.Count, provider.Name, hits.Count);
    }
}
