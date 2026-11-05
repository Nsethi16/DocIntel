using System.Security.Cryptography;
using System.Text;
using DocIntel.Api.Channels;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocIntel.Api.Diagnostics;

/// <summary>Runs real, source-scoped commercial questions and saves reviewable web answers, never email delivery.</summary>
public static class CommercialPortfolioQuestions
{
    public static string Question(string scenario) => scenario switch
    {
        "commercial-values" =>
            "Across the DVX commercial portfolio, compare original net quoted deal value by customer for USD offers. " +
            "Deduplicate repeated headers and the procurement copy. Exclude invoices, purchase orders, statements of work, " +
            "master agreements and amendments as separate quoted deals. Keep the unpriced scope missing, not zero, and " +
            "exclude EUR from this USD view. Show a ranked bar chart, a supporting source-backed table, total and customer " +
            "shares. Explain the largest contributors and source/coverage limits. Use original quoted amounts and cite every amount.",
        "commercial-billing" =>
            "Reconcile Arbor Ridge quote DVX-Q-2026-1101, amendment DVX-AM-2026-1101, PO DVX-PO-2026-1101 and invoice " +
            "DVX-INV-2026-1101. Calculate original and revised net contract value, approved PO coverage, invoiced amount, " +
            "unbilled value and the PO increase still required. Distinguish unbilled from unpaid: do not infer payment from " +
            "an invoice. Do not add the agreement packet or another copy as another sale. Provide a reconciliation table, " +
            "source citations for each amount, and next actions grounded in the stated approvals.",
        "commercial-terms" =>
            "Compare the English Arbor Ridge agreement and commercial offer, the Spanish Summit Harbor proposal, the " +
            "French Crescent Vale offer and data-processing terms, and the German Redwood Lane offer. Produce a cited " +
            "matrix of payment terms, availability measurement, liability, hosting/transfers, acceptance and approval gaps. " +
            "Clearly distinguish an agreed term from a proposed/requested term and a missing agreement from a zero risk. " +
            "Explain which commercial and governance differences require attention before production. Cite original-language " +
            "source pages; translation is presentation support, not independent evidence.",
        "commercial-risk" =>
            "As of 10 November 2026, prioritize closing readiness across the DVX offers. Compare quotation expiry, purchase " +
            "order status, amendment coverage, legal/privacy approvals and delivery dependencies. Calculate days until " +
            "expiry from exact source dates. Show a ranked risk/action table with owners as role recommendations, not " +
            "invented people. Separate documented blockers from inferred recommendations. Keep currencies separate and " +
            "do not invent missing prices, executed signatures or payments. Cite each material finding and state limits.",
        _ => throw new ArgumentException("Choose a fixed commercial question scenario.", nameof(scenario))
    };

    public static async Task<int> ExecuteAsync(
        string tenantId, string basisRunId, string scenario, bool confirmed, bool live,
        IHarnessStore harnesses, IAskAiRunStore runs, ITenantAccessResolver access, IJobStore jobs,
        IModelCatalog models, AskAiExecutionOptions options, TextWriter output,
        CancellationToken cancellationToken = default)
    {
        if (tenantId != DealVelocityWorkspaceSetup.WorkspaceId || !confirmed || !live ||
            !PersonalEmailState.ValidRun(basisRunId) ||
            scenario is not ("commercial-values" or "commercial-billing" or "commercial-terms" or "commercial-risk"))
        {
            await WriteAsync(output, "Unknown", "Refused", "InvalidQuestionInput");
            return 2;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(330));
        AskAiRun? run = null;
        try
        {
            var text = Question(scenario);
            var basis = await runs.GetRunAsync(tenantId, basisRunId, deadline.Token);
            if (basis?.Actor is not { } actor || basis.TenantId != tenantId || basis.RunId != basisRunId ||
                basis.Status != JobStatus.Completed || actor.DirectoryTenantId == Guid.Empty ||
                actor.ObjectId == Guid.Empty || basis.PendingEmail is null ||
                basis.ReplyChannel.ChannelType != AskAiChannelType.Email)
                throw new UnauthorizedAccessException();
            var scope = await access.ResolveAsync(actor, tenantId, deadline.Token);
            if (!scope.CanAdmin || !scope.CanProcess || scope.AllowedJobIds is not null)
                throw new UnauthorizedAccessException();
            var tenant = await harnesses.GetTenantAsync(tenantId, deadline.Token);
            if (tenant is not { IsActive: true } || tenant.TenantId != tenantId)
                throw new UnauthorizedAccessException();
            var sources = new List<DocumentJob>();
            foreach (var document in CommercialPortfolioCorpus.Build())
            {
                var id = DealVelocityWorkspaceSetup.JobId(document, CommercialPortfolioCorpus.Version);
                var job = await jobs.GetJobAsync(id, deadline.Token, tenantId);
                if (job is not { Status: JobStatus.Completed, HasDevelopmentResults: false } ||
                    job.TenantId != tenantId || job.JobId != id || job.SubmittedBy != actor ||
                    job.OriginalSha256 != Convert.ToHexString(SHA256.HashData(document.Bytes)) ||
                    job.TotalPages != document.Pages || job.Segments.Count == 0 ||
                    !scope.CanReadDocument(id))
                {
                    await WriteAsync(output, scenario, "Pending", "PortfolioNotProcessed");
                    return 3;
                }
                sources.Add(job);
            }
            string? runId = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var candidate = RunId(tenantId, actor, scenario, sources, attempt);
                var existing = await runs.GetRunAsync(tenantId, candidate, deadline.Token);
                if (existing is null)
                {
                    runId = candidate;
                    break;
                }
                if (existing.TenantId != tenantId || existing.RunId != candidate ||
                    existing.Actor != actor || existing.Question != text || existing.PendingEmail is not null ||
                    existing.ReplyChannel.ChannelType != AskAiChannelType.Web ||
                    existing.ReplyChannel.CallbackWebhookUrl is not null || existing.ChannelDelivery is not null)
                    throw new InvalidDataException("A commercial answer conflicts with its expected owner.");
                if (existing.Status == JobStatus.Completed)
                {
                    RequireAnswer(scenario, existing, sources);
                    await WriteAsync(output, scenario, "Completed", "None", existing);
                    return 0;
                }
                if (existing.Status != JobStatus.Failed)
                {
                    await WriteAsync(output, scenario, "Pending", "ExistingRunPending", existing);
                    return 3;
                }
            }
            if (runId is null)
            {
                await WriteAsync(output, scenario, "Failed", "RetryLimitReached");
                return 1;
            }
            run = new AskAiRun { RunId = runId, TenantId = tenantId, Actor = actor, Question = text,
                ReplyChannel = new AskAiReplyChannel { ChannelType = AskAiChannelType.Web } };
            var restricted = new PortfolioAccess(actor, tenantId, access, sources.Select(job => job.JobId).ToHashSet());
            var orchestrator = new AskAiOrchestrator(harnesses, jobs, models, [],
                NullLogger<AskAiOrchestrator>.Instance, restricted, runStore: runs, executionOptions: options);
            await orchestrator.ExecuteRunAsync(run, cancellationToken: deadline.Token);
            RequireAnswer(scenario, run, sources);
            scope = await access.ResolveAsync(actor, tenantId, deadline.Token);
            if (!scope.CanAdmin || !scope.CanProcess || scope.AllowedJobIds is not null)
                throw new UnauthorizedAccessException();
            foreach (var source in sources)
            {
                var current = await jobs.GetJobAsync(source.JobId, deadline.Token, tenantId);
                if (current?.Status != JobStatus.Completed || current.SubmittedBy != actor ||
                    AskAiConversationContext.Fingerprint(current) != AskAiConversationContext.Fingerprint(source))
                    throw new AskAiSourceChangedException();
            }
            if (!await runs.TryCreateRunAsync(run, deadline.Token))
                throw new InvalidOperationException("The same commercial answer was stored concurrently.");
            await WriteAsync(output, scenario, "Completed", "None", run);
            return 0;
        }
        catch (Exception exception)
        {
            var code = exception is UnauthorizedAccessException ? "AccessRevoked" :
                exception is OperationCanceledException ? "TimedOut" : AskAiFailureDiagnostics.GetCode(exception);
            if (run is not null)
            {
                run.Status = JobStatus.Failed;
                run.ErrorCode = code;
                run.ErrorMessage = AskAiFailureDiagnostics.GetMessage(code);
                run.CompletedAtUtc = DateTimeOffset.UtcNow;
                using var save = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                if (!await runs.TryCreateRunAsync(run, save.Token))
                    code = "RunConflict";
            }
            await WriteAsync(output, scenario, "Failed", code, run);
            return 1;
        }
    }

    internal static string RunId(string tenantId, ActorIdentity actor, string scenario,
        IReadOnlyList<DocumentJob> sources, int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(attempt, 2);
        var input = CommercialPortfolioCorpus.Version + "|" + tenantId + "|" +
            actor.DirectoryTenantId + "|" + actor.ObjectId + "|" + scenario + "|" + attempt + "|" +
            String.Join("|", sources.Select(AskAiConversationContext.Fingerprint));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)).AsSpan(0, 16));
    }

    private static void RequireAnswer(string scenario, AskAiRun run, IReadOnlyList<DocumentJob> sources)
    {
        if (run.Status != JobStatus.Completed || String.IsNullOrWhiteSpace(run.Answer) || run.Citations.Count == 0)
            throw new InvalidDataException("The commercial answer has no completed original-source evidence.");
        foreach (var citation in run.Citations)
        {
            var source = sources.FirstOrDefault(job => job.JobId == citation.JobId);
            if (source is null || citation.EvidenceKind != "OriginalSourceText" ||
                !run.SourceVersions.TryGetValue(citation.JobId, out var version) ||
                AskAiConversationContext.Fingerprint(source) != version)
                throw new AskAiSourceChangedException();
        }
        if (scenario == "commercial-values")
        {
            var chart = run.Analyses.FirstOrDefault(analysis => analysis.Visualization == "bar");
            if (chart?.XColumn is null || chart.YColumn is null || chart.Rows.Count != 2)
                throw new InvalidDataException("The requested commercial bar chart was not produced.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in chart.Rows)
            {
                if (!row.Cells.TryGetValue(chart.XColumn, out var customer) ||
                    !row.Cells.TryGetValue(chart.YColumn, out var amount))
                    throw new InvalidDataException("The commercial chart has missing values.");
                var name = Convert.ToString(customer, System.Globalization.CultureInfo.InvariantCulture) ?? String.Empty;
                var key = name.Contains("Arbor Ridge", StringComparison.OrdinalIgnoreCase) ? "Arbor Ridge" :
                    name.Contains("Summit Harbor", StringComparison.OrdinalIgnoreCase) ? "Summit Harbor" : null;
                var number = System.Text.Json.JsonSerializer.SerializeToElement(amount);
                if (key is null || !seen.Add(key) || number.ValueKind != System.Text.Json.JsonValueKind.Number ||
                    !number.TryGetDecimal(out var value) || value != (key == "Arbor Ridge" ? 158_400m : 226_800m))
                    throw new InvalidDataException("The source-derived original USD quote totals do not match the chart.");
            }
        }
    }

    private static Task WriteAsync(TextWriter output, string scenario, string status, string failureCode,
        AskAiRun? run = null) => output.WriteLineAsync("DOCINTEL_DIAGNOSTIC " +
        System.Text.Json.JsonSerializer.Serialize(new
        {
            phase = "CommercialQuestion", scenario, status, failureCode, runId = run?.RunId,
            citations = run?.Citations.Count, analyses = run?.Analyses.Count,
            hasBarChart = run?.Analyses.Any(analysis => analysis.Visualization == "bar")
        }));

    private sealed class PortfolioAccess(
        ActorIdentity expected, string tenant, ITenantAccessResolver current, IReadOnlySet<string> ids) : ITenantAccessResolver
    {
        public async Task<DocumentAccessScope> ResolveAsync(
            ActorIdentity? actor, string tenantId, CancellationToken cancellationToken = default)
        {
            if (actor != expected || tenantId != tenant) throw new UnauthorizedAccessException();
            var scope = await current.ResolveAsync(actor, tenantId, cancellationToken);
            if (!scope.CanAdmin || !scope.CanProcess || ids.Any(id => !scope.CanReadDocument(id)))
                throw new UnauthorizedAccessException();
            return new(TenantRole.Reader, ids);
        }
    }
}
