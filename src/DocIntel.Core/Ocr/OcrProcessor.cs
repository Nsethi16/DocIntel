using System.Diagnostics;
using System.Security.Cryptography;
using DocIntel.Core.Evidence;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Storage;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Ocr;

/// <summary>Recognizes only the pages required by the accepted policy, then stages validated source text.</summary>
public sealed class OcrProcessor(ILogger<OcrProcessor> logger, IOcrProvider? provider = null)
{
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
        var results = await provider.RecognizeAsync(original,
            selected.Select(page => page.PageNumber).ToList(), cancellationToken);
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
        DocumentLayoutCodec.Validate(new DocumentLayout(state.Tenant.TenantId, state.DocumentId,
            Convert.ToHexString(SHA256.HashData(original)), pages));
        cancellationToken.ThrowIfCancellationRequested();
        state.Pages = pages;
        state.ExecutionTrace.Add(new WorkflowStepTrace("RecognizeScannedPages", true, true, provider.Name,
            watch.Elapsed.TotalMilliseconds, $"Recognized {selected.Count} source page(s) using the configured provider."));
        logger.LogInformation("Recognized {PageCount} document pages using {OcrProvider}.", selected.Count, provider.Name);
    }
}
