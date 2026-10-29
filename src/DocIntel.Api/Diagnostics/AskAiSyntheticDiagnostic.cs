using System.Security.Cryptography;
using System.Text;
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
        CancellationToken cancellationToken = default, AskAiExecutionOptions? executionOptions = null)
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
        deadline.CancelAfter(TimeSpan.FromSeconds(300));
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
                ("synthetic-quote-1", "Northwind Demo", "SYN-001", "1,000.00", "USD"),
                ("synthetic-quote-2", "Northwind Demo", "SYN-002", "4,000.00", "USD"),
                ("synthetic-quote-3", "Fabrikam Demo", "SYN-003", "2,500.00", "USD"),
                ("synthetic-quote-4", "Fabrikam Demo", "SYN-004", "7,500.00", "USD"),
                ("synthetic-quote-5", "Adventure Demo", "SYN-005", "5,000.00", "USD"),
                ("synthetic-quote-6", "Adventure Demo", "SYN-006", "5,000.00", "USD"),
                ("synthetic-quote-7", "Northwind Demo", "SYN-001", "1,000.00", "USD"),
                ("synthetic-quote-8", "Euro Demo", "SYN-008", "9,000.00", "EUR"),
                ("synthetic-quote-9", "Unknown Demo", "SYN-009", "Not available", "USD")
            };
            foreach (var (id, customer, quote, amount, currency) in fixtures)
            {
                var sourceText = $"Synthetic diagnostic fixture; not a customer document.\n" +
                    $"Quote Number: {quote}\nCustomer Name: {customer}\nBilling currency: {currency}\n" +
                    $"Total deal value: {amount} {currency}.\n" + String.Join("\n", Enumerable.Repeat(
                        "Synthetic terms: this authored record is a diagnostic only, not a binding offer or customer transaction.", 8));
                await jobs.UpsertJobAsync(new DocumentJob
                {
                    JobId = id, TenantId = tenantId, TenantDisplayName = "Synthetic diagnostic",
                    AppliedHarnessVersion = 1, FileName = id + ".pdf", Status = JobStatus.Completed,
                    TotalPages = 1, HasDevelopmentResults = false,
                    OriginalSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceText))),
                    Segments =
                    [
                        new()
                        {
                            SegmentIndex = 0, StartPage = 1, EndPage = 1, IdentifiedDocumentName = "Quote",
                            BodyText = sourceText
                        }
                    ]
                }, deadline.Token);
            }
            var modelId = models.ResolveModelId(tenant, feature.ModelIdOverride);
            using var bounded = new BoundedClient(models.ResolveRequiredChatClient(tenant, feature.ModelIdOverride));
            var probeModels = new ModelCatalog(descriptors: models.ListModels(),
                customClients: new Dictionary<string, IChatClient> { [modelId] = bounded });
            var orchestrator = new AskAiOrchestrator(harness, jobs, probeModels, [],
                NullLogger<AskAiOrchestrator>.Instance, executionOptions: executionOptions);
            var run = await orchestrator.ExecuteRunAsync(new AskAiRun
            {
                RunId = Guid.NewGuid().ToString("N"), TenantId = tenantId,
                Question = "Compare total deal value by customer for all USD-denominated synthetic quotes. " +
                    "Avoid double-counting repeated copies of the same quote. Show a ranked bar chart and " +
                    "supporting table, calculate each customer's share, and cite the source for every amount. " +
                    "Inspect the original records and use analyze_evidence, not mental arithmetic. " +
                    "Include USD records with unavailable amounts as null input rows so coverage counts them " +
                    "as missing, never zero. Exclude EUR records. Explain missing data and analysis limits."
            }, cancellationToken: deadline.Token);
            var expected = new Dictionary<string, decimal>(StringComparer.Ordinal)
                { ["Northwind Demo"] = 5_000m, ["Fabrikam Demo"] = 10_000m, ["Adventure Demo"] = 10_000m };
            var valid = run.Status == JobStatus.Completed && run.Citations.Count >= 7 &&
                run.Analyses.Any(analysis => analysis.Visualization == "bar" && analysis.Rows.Count == 3 &&
                    analysis.Coverage.RecordsIncluded == 6 && analysis.Coverage.MissingValues == 1 &&
                    analysis.Rows.All(row => row.Cells.GetValueOrDefault("unit") as string == "USD") &&
                    analysis.Rows.All(row => row.Cells.GetValueOrDefault("category") is string category &&
                        expected.TryGetValue(category, out var value) &&
                        row.Cells.GetValueOrDefault("value") is decimal actual && actual == value &&
                        row.Cells.GetValueOrDefault("sharePercent") is decimal share && share == value / 250m));
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
            if (++Calls > 24) throw new InvalidOperationException("Synthetic model-call budget exceeded.");
            return base.GetResponseAsync(messages, options, cancellationToken);
        }
    }
}
