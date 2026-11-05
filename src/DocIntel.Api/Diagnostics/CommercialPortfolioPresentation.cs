using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocIntel.Api.Channels;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Features.DocumentInsight;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.PromptColumns;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using DocIntel.Core.Storage;

namespace DocIntel.Api.Diagnostics;

/// <summary>Refreshes only concise insights and a document-scoped privacy check on the fixed, owned portfolio.</summary>
public static class CommercialPortfolioPresentation
{
    public const string PrivacyColumnId = "dvxprivacy";
    public const string PrivacyColumnName = "Privacy Readiness";
    private const string RefreshStep = "RefreshCommercialPresentation";
    internal const string PrivacyInstructions =
        "Check only the production-data privacy prerequisites explicitly stated in this document. " +
        "This is document readiness, not a legal certification; never claim GDPR, SOC 2 or overall regulatory compliance. " +
        "Return at most 75 words, exactly four Markdown bullets, one line per bullet, with these bold labels: " +
        "**Decision:** exactly HOLD, REVIEW, READY or NOT APPLICABLE. " +
        "HOLD means a required privacy/security approval, subprocessor list, transfer safeguard or DPA is explicitly " +
        "pending or blocking production. REVIEW means relevant terms exist but complete privacy clearance is not evidenced. " +
        "READY requires explicit completed privacy clearance and no stated outstanding privacy prerequisite; " +
        "a region, security control or commercial approval alone is not clearance. " +
        "NOT APPLICABLE means this document contains no privacy decision, for example an invoice or administrative order; " +
        "it says nothing about readiness under other agreements. " +
        "**Why:** one short sentence naming the specific missing approval or prerequisite, not generic compliance language. " +
        "**Evidence:** one verbatim excerpt of 3-18 words, in ASCII double quotes, copied from the supplied source text; " +
        "do not translate, paraphrase or invent a quotation or page number. " +
        "Use exactly Not stated in this document. only when no relevant source statement exists; " +
        "HOLD and READY always require a quote. " +
        "**Next:** one concise recommended action and responsible role, not an invented person. " +
        "Do not infer signatures, payments, approvals or missing-document contents. " +
        "Do not treat a conditional future transfer restriction as evidence that an unapproved transfer occurred. " +
        "Write the explanation in plain English. No introduction, clause inventory or conclusion.";

    /// <summary>Changes only the requested insight instructions and the owned privacy column.</summary>
    public static TenantHarness Configure(TenantHarness source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.TenantId != DealVelocityWorkspaceSetup.WorkspaceId)
            throw new ArgumentException("Only the fixed commercial workspace can be configured.", nameof(source));
        var tenant = Clone(source);
        TenantHarness.EnsureDefaultCapabilities(tenant);
        var insights = tenant.Capabilities[FeatureHarness.DocumentInsightKey];
        insights.Enabled = true;
        insights.CustomSystemPrompt = null;
        (insights.DocumentInsight ??= new()).SummaryInstructions = DocumentInsightConfig.DefaultSummaryInstructions;
        var prompts = tenant.Capabilities[FeatureHarness.PromptColumnsKey];
        prompts.Enabled = true;
        var columns = (prompts.PromptColumns ??= new()).Columns;
        var existing = columns.Where(column =>
            String.Equals(column.ColumnId, PrivacyColumnId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (existing.Length > 1 || existing.Any(column => column.ColumnId != PrivacyColumnId ||
            column.ColumnName != PrivacyColumnName ||
            column.PromptInstructions != PrivacyInstructions || column.FilterMode != "All" ||
            !column.IsActive || column.EntityFilters.Count != 0))
            throw new HarnessConflictException();
        if (existing.Length == 0)
            columns.Insert(0, new() { ColumnId = PrivacyColumnId, ColumnName = PrivacyColumnName,
                PromptInstructions = PrivacyInstructions, FilterMode = "All" });
        return tenant;
    }

    /// <summary>Apply requires POLISH and live opt-in; inspection never generates or changes results.</summary>
    public static async Task<int> ExecuteAsync(
        string tenantId, string basisRunId, bool apply, bool confirmed, bool live,
        IHarnessStore harnesses, IAskAiRunStore runs, ITenantAccessResolver access, IJobStore jobs,
        IArtifactStore artifacts, IModelCatalog models, TextWriter output,
        CancellationToken cancellationToken = default)
    {
        if (tenantId != DealVelocityWorkspaceSetup.WorkspaceId || !PersonalEmailState.ValidRun(basisRunId) ||
            apply && (!confirmed || !live))
        {
            await WriteAsync(output, "Summary", "Refused", "InvalidPresentationInput");
            return 2;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(330));
        try
        {
            var basis = await runs.GetRunAsync(tenantId, basisRunId, deadline.Token);
            if (basis is not { Status: JobStatus.Completed, Actor: { } actor, PendingEmail: not null } ||
                basis.TenantId != tenantId || basis.RunId != basisRunId ||
                basis.ReplyChannel.ChannelType != AskAiChannelType.Email ||
                actor.DirectoryTenantId == Guid.Empty || actor.ObjectId == Guid.Empty)
                throw new UnauthorizedAccessException();
            var current = await DealVelocityWorkspaceSetup.RequireAdministratorAsync(
                actor, harnesses, access, deadline.Token);
            var sources = new List<(DealVelocityDemoCorpus.Document Document, DocumentJob Job)>();
            foreach (var document in CommercialPortfolioCorpus.Build())
            {
                var id = DealVelocityWorkspaceSetup.JobId(document, CommercialPortfolioCorpus.Version);
                var job = await jobs.GetJobAsync(id, deadline.Token, tenantId);
                if (job is not { Status: JobStatus.Completed, HasDevelopmentResults: false } ||
                    job.TenantId != tenantId || job.JobId != id || job.SubmittedBy != actor ||
                    job.OriginalSha256 != Convert.ToHexString(SHA256.HashData(document.Bytes)) ||
                    job.TotalPages != document.Pages || job.Segments.Count == 0 ||
                    job.Segments.Any(slice => String.IsNullOrWhiteSpace(DocumentPromptBoundary.SourceText(slice))) ||
                    DealVelocityWorkspaceSetup.PortfolioFailure(document.Key, job) != "None")
                {
                    await WriteAsync(output, "Summary", "Pending", "PortfolioNotReady");
                    return 3;
                }
                var original = await artifacts.GetOriginalPdfAsync(tenantId, id, deadline.Token);
                if (original is null || Convert.ToHexString(SHA256.HashData(original)) != job.OriginalSha256)
                    throw new InvalidDataException("A fixed original is unavailable or changed.");
                sources.Add((document, job));
            }
            var desired = Configure(current);
            if (apply && JsonSerializer.Serialize(current) != JsonSerializer.Serialize(desired))
            {
                await RequireCurrentAsync(current, actor, harnesses, access, deadline.Token);
                current = await harnesses.UpsertTenantAsync(desired, deadline.Token);
                if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(Configure(current)))
                    throw new HarnessConflictException();
            }
            var configured = JsonSerializer.Serialize(current) == JsonSerializer.Serialize(Configure(current));
            var pending = !configured;
            var failed = false;
            foreach (var (document, source) in sources)
            {
                await RequireCurrentAsync(current, actor, harnesses, access, deadline.Token);
                var input = InputFingerprint(source, desired, models);
                var ready = source.ExecutionTrace.Any(step =>
                    step.ExecutorName == RefreshStep && step.Executed && step.Succeeded && step.Notes == input) &&
                    QualityFailure(document.Key, source) == "None";
                if (!ready && apply)
                {
                    var working = Clone(source);
                    working.StoreVersion = source.StoreVersion;
                    var timer = Stopwatch.StartNew();
                    await GenerateDocumentInsightsExecutor.EvaluateJobInsightsAsync(
                        working, current, models, deadline.Token);
                    if (working.Segments.Any(slice => !TryBrief(
                        slice.InsightSummary, ["What matters", "Key terms", "Watch out", "Next action"], 90, out _)))
                    {
                        var refinement = Clone(current);
                        refinement.Capabilities[FeatureHarness.DocumentInsightKey].DocumentInsight!.SummaryInstructions +=
                            " Refinement: write at most 60 words including the four labels and bullet markers. " +
                            "Keep the exact four-bullet format; retain the main sourced fact, risk and action. " +
                            "Do not pad the answer or repeat identifiers.";
                        await GenerateDocumentInsightsExecutor.EvaluateJobInsightsAsync(
                            working, refinement, models, deadline.Token);
                    }
                    await EvaluatePrivacyAsync(working, current, artifacts, models, deadline.Token);
                    var failure = QualityFailure(document.Key, working);
                    if (failure != "None" || working.HasDevelopmentResults != false)
                    {
                        failed = true;
                        await WriteAsync(output, document.Key, "Failed",
                            failure != "None" ? failure : "ProductionResultsRequired", working);
                        continue;
                    }
                    await RequireCurrentAsync(current, actor, harnesses, access, deadline.Token);
                    var latest = await jobs.GetJobAsync(source.JobId, deadline.Token, tenantId);
                    if (latest?.Status != JobStatus.Completed || latest.SubmittedBy != actor ||
                        AskAiConversationContext.Fingerprint(latest) != AskAiConversationContext.Fingerprint(source))
                        throw new AskAiSourceChangedException();
                    working.ExecutionTrace.Add(new(nameof(GenerateDocumentInsightsExecutor), true, true,
                        models.ResolveModelId(current,
                            current.Capabilities[FeatureHarness.DocumentInsightKey].ModelIdOverride),
                        timer.Elapsed.TotalMilliseconds,
                        $"Refreshed the concise document brief with workspace v{current.HarnessVersion}."));
                    working.ExecutionTrace.Add(new(nameof(EvaluatePromptColumnsExecutor), true, true,
                        models.ResolveModelId(current,
                            current.Capabilities[FeatureHarness.PromptColumnsKey].ModelIdOverride),
                        timer.Elapsed.TotalMilliseconds,
                        $"Evaluated only Privacy Readiness with workspace v{current.HarnessVersion}; other columns retained."));
                    working.ExecutionTrace.Add(new(RefreshStep, true, true, null,
                        timer.Elapsed.TotalMilliseconds, input));
                    await DealVelocityWorkspaceSetup.SaveFencedAsync(jobs, working, deadline.Token);
                    ready = true;
                    await WriteAsync(output, document.Key, "Updated", "None", working);
                }
                else
                {
                    await WriteAsync(output, document.Key, ready ? "Ready" : "Pending", "None", source);
                }
                pending |= !ready;
            }
            await RequireCurrentAsync(current, actor, harnesses, access, deadline.Token);
            await WriteAsync(output, "Summary", failed ? "Failed" : pending ? "Pending" : "Completed",
                failed ? "ResultQualityFailed" : "None");
            return failed ? 1 : pending ? 3 : 0;
        }
        catch (UnauthorizedAccessException)
        {
            await WriteAsync(output, "Summary", "Refused", "CurrentAdministratorRequired");
            return 2;
        }
        catch (HarnessConflictException)
        {
            await WriteAsync(output, "Summary", "Failed", "WorkspaceChanged");
            return 1;
        }
        catch (InvalidOperationException exception) when (
            exception is DocumentLifecycleConflictException or AskAiSourceChangedException)
        {
            await WriteAsync(output, "Summary", "Failed", "DocumentChanged");
            return 1;
        }
        catch (InvalidDataException)
        {
            await WriteAsync(output, "Summary", "Failed", "SourceOrGenerationUnavailable");
            return 1;
        }
        catch (JsonException)
        {
            await WriteAsync(output, "Summary", "Failed", "InvalidGeneratedOutput");
            return 1;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await WriteAsync(output, "Summary", "Failed", "TimedOut");
            return 1;
        }
        catch (Exception exception) when (
            exception is Azure.RequestFailedException or System.ClientModel.ClientResultException or HttpRequestException)
        {
            await WriteAsync(output, "Summary", "Failed", "ProviderUnavailable");
            return 1;
        }
    }

    internal static string QualityFailure(string key, DocumentJob job)
    {
        foreach (var slice in job.Segments)
        {
            if (!TryBrief(slice.InsightSummary, ["What matters", "Key terms", "Watch out", "Next action"], 90, out _))
                return "InsightNotConcise";
            var results = slice.PromptColumnResults.Where(result => result.ColumnId == PrivacyColumnId).ToArray();
            if (results.Length != 1 || results[0].Status != "Evaluated" ||
                !TryBrief(results[0].ResultText, ["Decision", "Why", "Evidence", "Next"], 75, out var fields))
                return "PrivacyNotConcise";
            var decision = fields[0];
            if (decision is not ("HOLD" or "REVIEW" or "READY" or "NOT APPLICABLE") ||
                (key is "DVXPrivacyFR" or "DVXQuoteFR" or "DVXQuoteDE") && decision != "HOLD" ||
                (key is "DVXInvoice" or "DVXScannedInvoice") && decision != "NOT APPLICABLE")
                return "PrivacyDecisionMismatch";
            if (fields[2] == "Not stated in this document." && decision is not ("HOLD" or "READY")) continue;
            var quote = fields[2];
            if (quote.Length < 4 || quote[0] != '"' || quote[^1] != '"' ||
                Regex.Matches(quote[1..^1], @"\S+").Count is < 3 or > 18 ||
                !Normalize(DocumentPromptBoundary.SourceText(slice)).Contains(
                    Normalize(quote[1..^1]), StringComparison.Ordinal))
                return "PrivacyEvidenceMismatch";
        }
        return job.Segments.Count == 0 ? "PortfolioNotReady" : "None";
    }

    private static bool TryBrief(string? text, string[] labels, int words, out string[] fields)
    {
        fields = [];
        if (String.IsNullOrWhiteSpace(text) || Regex.Matches(text, @"\S+").Count > words) return false;
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (lines.Length != labels.Length) return false;
        var values = new List<string>();
        for (var index = 0; index < labels.Length; index++)
        {
            var prefix = "- **" + labels[index] + ":** ";
            if (!lines[index].StartsWith(prefix, StringComparison.Ordinal) || lines[index].Length <= prefix.Length)
                return false;
            values.Add(lines[index][prefix.Length..].Trim());
        }
        fields = values.ToArray();
        return true;
    }

    private static async Task EvaluatePrivacyAsync(
        DocumentJob job, TenantHarness tenant, IArtifactStore artifacts, IModelCatalog models, CancellationToken token)
    {
        var prior = job.PromptColumnResults.Where(result => result.ColumnId != PrivacyColumnId).ToArray();
        var perSlice = job.Segments.ToDictionary(slice => slice.SegmentIndex,
            slice => slice.PromptColumnResults.Where(result => result.ColumnId != PrivacyColumnId).ToArray());
        var privacyOnly = Clone(tenant);
        privacyOnly.Capabilities[FeatureHarness.PromptColumnsKey].PromptColumns!.Columns.RemoveAll(
            column => column.ColumnId != PrivacyColumnId);
        var generated = (await EvaluatePromptColumnsExecutor.EvaluateJobPromptsAsync(
            job, privacyOnly, models, artifacts, token)).Select(NormalizeMissingEvidence).ToArray();
        job.PromptColumnResults = generated.Concat(prior).ToList();
        foreach (var slice in job.Segments)
            slice.PromptColumnResults = generated.Where(result => result.SegmentIndex == slice.SegmentIndex)
                .Concat(perSlice[slice.SegmentIndex]).ToList();
    }

    internal static PromptColumnResult NormalizeMissingEvidence(PromptColumnResult result)
    {
        if (!TryBrief(result.ResultText, ["Decision", "Why", "Evidence", "Next"], 75, out var fields) ||
            fields[0] is not ("REVIEW" or "NOT APPLICABLE"))
            return result;
        const string missing = "Not stated in this document.";
        if (fields[2] != "\"" + missing + "\"" && fields[2] != "\u201c" + missing + "\u201d")
            return result;
        return result with
        {
            ResultText = $"- **Decision:** {fields[0]}\n- **Why:** {fields[1]}\n" +
                $"- **Evidence:** {missing}\n- **Next:** {fields[3]}"
        };
    }

    private static async Task RequireCurrentAsync(
        TenantHarness expected, ActorIdentity actor, IHarnessStore harnesses,
        ITenantAccessResolver access, CancellationToken token)
    {
        var current = await DealVelocityWorkspaceSetup.RequireAdministratorAsync(actor, harnesses, access, token);
        if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(expected)) throw new HarnessConflictException();
    }

    private static string InputFingerprint(DocumentJob job, TenantHarness tenant, IModelCatalog models) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, job.OriginalSha256,
            insightModel = models.ResolveModelId(tenant,
                tenant.Capabilities[FeatureHarness.DocumentInsightKey].ModelIdOverride),
            privacyModel = models.ResolveModelId(tenant,
                tenant.Capabilities[FeatureHarness.PromptColumnsKey].ModelIdOverride),
            DocumentInsightConfig.DefaultSummaryInstructions, PrivacyInstructions,
            documents = job.Segments.Select(slice => new
            {
                slice.SegmentIndex, slice.StartPage, slice.EndPage, slice.IdentifiedDocumentName,
                text = DocumentPromptBoundary.SourceText(slice), slice.ExtractedEntities
            })
        })));

    private static string Normalize(string value) =>
        Regex.Replace(value.Normalize(System.Text.NormalizationForm.FormKC), @"\s+", " ").Trim();

    private static T Clone<T>(T source) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(source))
        ?? throw new InvalidDataException("A presentation snapshot is unavailable.");

    private static Task WriteAsync(
        TextWriter output, string stage, string status, string failureCode, DocumentJob? job = null) =>
        output.WriteLineAsync("DOCINTEL_DIAGNOSTIC " + JsonSerializer.Serialize(new
        {
            phase = "CommercialPresentation", stage, status, failureCode, jobId = job?.JobId,
            slices = job?.Segments.Count,
            insightWordsMax = job?.Segments.Select(slice =>
                Regex.Matches(slice.InsightSummary ?? String.Empty, @"\S+").Count).DefaultIfEmpty(0).Max(),
            privacyWordsMax = job?.Segments.SelectMany(slice => slice.PromptColumnResults)
                .Where(result => result.ColumnId == PrivacyColumnId)
                .Select(result => Regex.Matches(result.ResultText ?? String.Empty, @"\S+").Count).DefaultIfEmpty(0).Max(),
            decisions = job?.Segments.Select(slice => new
            {
                slice.SegmentIndex,
                decision = TryBrief(slice.PromptColumnResults.FirstOrDefault(
                    result => result.ColumnId == PrivacyColumnId)?.ResultText,
                    ["Decision", "Why", "Evidence", "Next"], 75, out var fields) &&
                    fields[0] is "HOLD" or "REVIEW" or "READY" or "NOT APPLICABLE" ? fields[0] : "Unknown"
            }).ToArray()
        }));
}
