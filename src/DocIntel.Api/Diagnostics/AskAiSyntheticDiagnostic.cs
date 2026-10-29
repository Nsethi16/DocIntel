using System.Text.Json;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocIntel.Api.Diagnostics;

/// <summary>Exercises the real model and agent over an isolated, in-memory synthetic corpus only.</summary>
public static class AskAiSyntheticDiagnostic
{
    /// <summary>Never resolves a production job/run store and never publishes or delivers the generated answer.</summary>
    public static async Task<int> ExecuteAsync(
        string tenantId, bool liveProbingEnabled, IHarnessStore harness, IModelCatalog models, TextWriter output,
        CancellationToken cancellationToken = default)
    {
        if (tenantId.Length is < 1 or > 128 ||
            tenantId.Any(character => !Char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("Choose a valid application workspace.");
        if (!liveProbingEnabled)
        {
            await WriteAsync(output, "Refused", "LiveProbingDisabled");
            return 2;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            var tenant = await harness.GetTenantAsync(tenantId, deadline.Token);
            if (tenant is not { IsActive: true } ||
                !tenant.Capabilities.TryGetValue(FeatureHarness.AskAiKey, out var feature) || !feature.Enabled)
            {
                await WriteAsync(output, "Refused", "AskAiUnavailable");
                return 2;
            }
            var jobs = new InMemoryJobStore();
            var fixtures = new[]
            {
                ("synthetic-quote-1", "Northwind Demo", "SYN-001", "1,000.00"),
                ("synthetic-quote-2", "Fabrikam Demo", "SYN-002", "2,500.00")
            };
            foreach (var (id, customer, quote, amount) in fixtures)
                await jobs.UpsertJobAsync(new DocumentJob
                {
                    JobId = id, TenantId = tenantId, TenantDisplayName = "Synthetic diagnostic",
                    AppliedHarnessVersion = 1, FileName = id + ".pdf", Status = JobStatus.Completed,
                    TotalPages = 1, HasDevelopmentResults = false,
                    Segments =
                    [
                        new()
                        {
                            SegmentIndex = 0, StartPage = 1, EndPage = 1, IdentifiedDocumentName = "Quote",
                            BodyText = $"Synthetic diagnostic fixture; not a customer document.\n" +
                                $"Quote Number: {quote}\nCustomer Name: {customer}\nBilling currency: USD\n" +
                                $"Total deal value: {amount} USD."
                        }
                    ]
                }, deadline.Token);
            var modelId = models.ResolveModelId(tenant, feature.ModelIdOverride);
            using var bounded = new BoundedClient(models.ResolveRequiredChatClient(tenant, feature.ModelIdOverride));
            var probeModels = new ModelCatalog(descriptors: models.ListModels(),
                customClients: new Dictionary<string, IChatClient> { [modelId] = bounded });
            var orchestrator = new AskAiOrchestrator(harness, jobs, probeModels, [],
                NullLogger<AskAiOrchestrator>.Instance);
            var run = await orchestrator.ExecuteRunAsync(new AskAiRun
            {
                RunId = Guid.NewGuid().ToString("N"), TenantId = tenantId,
                Question = "Analyze only the two synthetic quote fixtures available to this diagnostic. " +
                    "Compare total deal value by customer in USD. Use tools to inspect both original texts " +
                    "and analyze_evidence to produce a bar chart and supporting table. Cite both sources. " +
                    "Do not merely describe the documents or calculate without analyze_evidence."
            }, cancellationToken: deadline.Token);
            var valid = run.Status == JobStatus.Completed && run.Citations.Count == 2 &&
                run.Analyses.Any(analysis => analysis.Visualization == "bar" && analysis.Rows.Count == 2 &&
                    analysis.Rows.All(row => row.Cells.GetValueOrDefault("unit") as string == "USD") &&
                    analysis.Rows.Select(row => row.Cells.GetValueOrDefault("value"))
                        .OfType<decimal>().Order().SequenceEqual(new[] { 1_000m, 2_500m }));
            await WriteAsync(output, valid ? "Completed" : "Failed",
                valid ? "None" : "SyntheticResultMismatch", calls: bounded.Calls,
                citations: run.Citations.Count, analyses: run.Analyses.Count);
            return valid ? 0 : 1;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await WriteAsync(output, "Failed", "TimedOut");
            return 1;
        }
        catch (Exception exception)
        {
            await WriteAsync(output, "Failed", AskAiFailureDiagnostics.GetCode(exception),
                AskAiProviderFailure.FromException(exception));
            return 1;
        }
    }

    private static Task WriteAsync(
        TextWriter output, string status, string failureCode, AskAiProviderFailure? provider = null,
        int calls = 0, int citations = 0, int analyses = 0) =>
        output.WriteLineAsync("DOCINTEL_DIAGNOSTIC " + JsonSerializer.Serialize(new
        {
            phase = "Synthetic", status, failureCode, calls, citations, analyses,
            httpStatus = provider?.HttpStatus, providerCode = provider?.Code,
            providerParameter = provider?.Parameter, schemaKeyword = provider?.SchemaKeyword,
            providerHints = provider?.Hints
        }));

    private sealed class BoundedClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public int Calls { get; private set; }
        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (++Calls > 8) throw new InvalidOperationException("Synthetic model-call budget exceeded.");
            return base.GetResponseAsync(messages, options, cancellationToken);
        }
    }
}
