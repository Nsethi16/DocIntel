using DocIntel.Core.Authorization;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Features.DataExtraction;
using DocIntel.Core.Features.DocumentIdentification;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.DocumentTranslation;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using DocIntel.Core.Ocr;
using DocIntel.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocIntel.Api.Diagnostics;

public static partial class CustomerAcceptanceDiagnostic
{
    private static (string Type, string Site) SchemaFailureMetadata(Exception exception)
    {
        var root = exception.GetBaseException();
        var type = root.GetType().Name is
            "ArgumentException" or "InvalidOperationException" or "InvalidDataException" or "JsonException" or
            "FormatException" or "RequestFailedException" or "ClientResultException" or "AuthenticationFailedException" or
            "CosmosException" or "InvalidCastException" or "NullReferenceException"
            ? root.GetType().Name : "OtherException";
        var sites = new System.Diagnostics.StackTrace(root).GetFrames()
            .Select(frame => frame.GetMethod()?.DeclaringType?.FullName ?? String.Empty).ToArray();
        var site = sites.Any(value => value.Contains(nameof(ExtractDocumentDataExecutor), StringComparison.Ordinal))
            ? "DataExtraction" : sites.Any(value => value.Contains(nameof(IdentifyDocumentsExecutor), StringComparison.Ordinal))
                ? "Identification" : sites.Any(value => value.Contains("ModelCatalog", StringComparison.Ordinal))
                    ? "ModelConfiguration" : sites.Any(value => value.StartsWith("Microsoft.Agents.", StringComparison.Ordinal))
                        ? "Workflow" : "ControlledDiagnostic";
        return (type, site);
    }

    private static async Task<int> SchemaAsync(string customerCase, ActorIdentity actor, SourceGuard guard,
        IModelCatalog models, IJobStore jobs, IArtifactStore artifacts, TextWriter output, CancellationToken token)
    {
        var scenarioId = customerCase switch
        {
            "schema-signature" => "B", "schema-price" => "G", "schema-long" => "E",
            _ => throw new ArgumentOutOfRangeException(nameof(customerCase))
        };
        var scenario = Scenarios.Single(item => item.Id == scenarioId);
        var source = await FindAsync(scenario, actor, jobs, guard, token);
        if (source is null || !Eligible(source, scenario, actor))
            throw new AcceptanceGuardException("FixtureUnavailable");
        guard.Track(source, scenario);
        await guard.CheckAsync(token);
        await VerifyOriginalAsync(source, artifacts, token);
        await guard.CheckAsync(token);
        var sourceStamp = AskAiConversationContext.Fingerprint(source);
        var current = guard.CurrentHarness;
        var model = models.ResolveModelId(current,
            current.Capabilities.GetValueOrDefault(FeatureHarness.DataExtractionKey)?.ModelIdOverride);
        var defaults = new TenantHarness { TenantId = Workspace, DisplayName = "Controlled QA catalogue" };
        var rules = defaults.Capabilities[FeatureHarness.IdentifyDocumentsKey].IdentifyDocuments!.Rules;
        var profile = customerCase == "schema-signature"
            ? new DocumentDataExtractionProfile
            {
                DocumentName = "Program Signature Form",
                Fields =
                [
                    new() { FieldName = "CustomerEntityName", Instruction = "Extract the exact customer legal entity name." },
                    new() { FieldName = "MbaMbsaNumber", Instruction = "Extract the original MBA/MBSA number." },
                    new() { FieldName = "ContractNumber", Instruction = "Extract the original contract number." }
                ]
            }
            : PriceProfile();
        var tenant = new TenantHarness
        {
            TenantId = Workspace, DisplayName = "Controlled QA catalogue", DefaultModelId = model,
            DecisionModelId = current.DecisionModelId, AllowedModels = [model, current.DecisionModelId],
            Ocr = new() { Mode = OcrMode.Never },
            Capabilities = new(StringComparer.OrdinalIgnoreCase)
            {
                [FeatureHarness.SplitAndGroupKey] = new() { Enabled = true, SplitAndGroup = new() },
                [FeatureHarness.IdentifyDocumentsKey] = new()
                {
                    Enabled = true, IdentifyDocuments = new() { DecisionModelId = current.DecisionModelId, Rules = rules }
                },
                [FeatureHarness.DataExtractionKey] = new()
                {
                    Enabled = true, ModelIdOverride = model, DataExtraction = new() { Profiles = [profile] }
                }
            }
        };
        var bytes = await DocumentPdfReader.ReadOriginalAsync(source, artifacts, token)
            ?? throw new AcceptanceGuardException("OriginalUnavailable");
        await guard.CheckAsync(token);
        var extractor = new PdfPigLayoutExtractor();
        var result = await new DocumentSplittingWorkflow(extractor, extractor, models,
            new DisabledSchemaTranslation(), NullLogger<DocumentSplittingWorkflow>.Instance)
            .ExecuteAsync(new()
            {
                Tenant = tenant, DocumentId = source.JobId, FileName = source.FileName, SourcePdfBytes = bytes
            }, onStepCompleted: (_, cancellation) => guard.CheckAsync(cancellation), cancellationToken: token);
        await guard.CheckAsync(token);
        var checks = SchemaAssertions(customerCase, result.Slices);
        checks.Add(Check("SchemaNativeDecision", result.ExecutionTrace.Any(step =>
            step.ExecutorName == nameof(IdentifyDocumentsExecutor) && step.Succeeded &&
            step.ModelIdUsed == current.DecisionModelId)));
        checks.Add(Check("SchemaNoSimulation", !result.HasDevelopmentResults));
        checks.Add(Check("SchemaSourceUnchanged", sourceStamp == AskAiConversationContext.Fingerprint(source)));
        var passed = checks.All(check => check.Status != "Failed");
        var report = new DocumentJob
        {
            JobId = source.JobId, TenantId = Workspace, TenantDisplayName = "Controlled QA catalogue",
            AppliedHarnessVersion = source.AppliedHarnessVersion, ProcessingHarness = tenant,
            FileName = source.FileName, Status = JobStatus.Completed, OriginalSha256 = source.OriginalSha256,
            TotalPages = source.TotalPages, Segments = result.Slices, DataExtractions = result.DataExtractions
        };
        await WriteAsync(output, customerCase, "Summary", passed ? "Completed" : "Failed",
            passed ? "None" : "AcceptanceMismatch", report, assertions: checks, sources: [source]);
        return passed ? 0 : 1;
    }

    private static DocumentDataExtractionProfile PriceProfile() => new()
    {
        DocumentName = "Customer Price Sheet",
        Fields =
        [
            new() { FieldName = "CustomerName", Instruction = "Extract the exact customer legal entity name." },
            new() { FieldName = "QuoteNumber", Instruction = "Extract the original quote number." },
            new() { FieldName = "BillingCurrency", Instruction = "Extract the three-letter billing currency." },
            new() { FieldName = "TotalDealValue", Instruction =
                "Extract the stated quote-level total, preserving a minus sign. Return empty string if not provided. " +
                "Do not infer a total or sum unit prices." }
        ],
        Tables =
        [
            new()
            {
                TableName = "SubscriptionPricing",
                Instruction = "Extract every product pricing row across the complete source. Preserve SKU, product " +
                    "and unit price exactly. Do not truncate, summarize or insert an aggregate row.",
                Columns =
                [
                    new() { ColumnName = "PartNumber", Instruction = "Original SKU / part number." },
                    new() { ColumnName = "ProductName", Instruction = "Original product or service name." },
                    new() { ColumnName = "UnitPrice", Instruction = "Original numeric unit price." }
                ]
            }
        ]
    };

    internal static List<Assertion> SchemaAssertions(string customerCase, IReadOnlyList<DocumentSlice> slices)
    {
        var checks = new List<Assertion>();
        if (customerCase == "schema-signature")
        {
            var signature = slices.SingleOrDefault(slice => slice.StartPage == 3 && slice.EndPage == 3);
            checks.Add(Check("SchemaClassification", slices.Count == 3 &&
                slices.OrderBy(slice => slice.SegmentIndex).Select(slice => slice.IdentifiedDocumentName)
                    .SequenceEqual(["Customer Price Sheet", "Program Signature Form", "Enterprise Enrollment"])));
            checks.Add(Check("SchemaFields", signature?.DataExtraction is { } data &&
                ExactFields(data.Fields, ["CustomerEntityName", "MbaMbsaNumber", "ContractNumber"]) &&
                Value(data.Fields, "CustomerEntityName") == Customer &&
                Value(data.Fields, "MbaMbsaNumber") == "QA-MBSA-62001" &&
                Value(data.Fields, "ContractNumber") == "QA-AG-62001" && data.Tables.Count == 0));
            return checks;
        }
        var expectedCount = customerCase == "schema-long" ? 1 : 4;
        checks.Add(Check("SchemaClassification", slices.Count == expectedCount &&
            slices.All(slice => slice.IdentifiedDocumentName == "Customer Price Sheet") &&
            slices.Select(slice => slice.SegmentIndex).Order().SequenceEqual(Enumerable.Range(0, expectedCount))));
        var fieldsValid = slices.Count == expectedCount;
        var fieldShape = fieldsValid;
        var customers = fieldsValid;
        var customerFormatting = fieldsValid;
        var quoteIdsValid = fieldsValid;
        var quoteFormatting = fieldsValid;
        var currencies = fieldsValid;
        var amountsValid = fieldsValid;
        var rowsValid = slices.Count == expectedCount;
        var missingValid = customerCase != "schema-price";
        var quoteIds = new[] { "QA-CREDIT-001", "QA-POSITIVE-002", "QA-EURO-003", "QA-MISSING-004" };
        decimal[] amounts = [-500m, 3_500m, 2_000m];
        foreach (var slice in slices)
        {
            if (slice.DataExtraction is not { } data)
            {
                fieldsValid = rowsValid = false;
                fieldShape = customers = customerFormatting = quoteIdsValid = quoteFormatting =
                    currencies = amountsValid = false;
                continue;
            }
            var index = slice.SegmentIndex;
            fieldsValid &= ExactFields(data.Fields, ["CustomerName", "QuoteNumber", "BillingCurrency", "TotalDealValue"]) &&
                Value(data.Fields, "CustomerName") == Customer;
            fieldShape &= ExactFields(data.Fields, ["CustomerName", "QuoteNumber", "BillingCurrency", "TotalDealValue"]);
            customers &= Value(data.Fields, "CustomerName") == Customer;
            customerFormatting &= String.Equals(Compact(Value(data.Fields, "CustomerName")).TrimEnd('.', ','),
                Compact(Customer), StringComparison.OrdinalIgnoreCase);
            if (customerCase == "schema-long")
            {
                fieldsValid &= Value(data.Fields, "QuoteNumber") == "QA-LONG-001" &&
                    Value(data.Fields, "BillingCurrency") == "USD" && HasAmount(Value(data.Fields, "TotalDealValue"), 45_000m);
                quoteIdsValid &= Value(data.Fields, "QuoteNumber") == "QA-LONG-001";
                quoteFormatting &= Compact(Value(data.Fields, "QuoteNumber")).TrimEnd('.', ',') == "QA-LONG-001";
                currencies &= Value(data.Fields, "BillingCurrency") == "USD";
                amountsValid &= HasAmount(Value(data.Fields, "TotalDealValue"), 45_000m);
            }
            else if (index is >= 0 and < 4)
            {
                fieldsValid &= Value(data.Fields, "QuoteNumber") == quoteIds[index] &&
                    Value(data.Fields, "BillingCurrency") == (index == 2 ? "EUR" : "USD") &&
                    (index == 3 || HasAmount(Value(data.Fields, "TotalDealValue"), amounts[index]));
                if (index == 3) missingValid = String.IsNullOrWhiteSpace(Value(data.Fields, "TotalDealValue"));
                quoteIdsValid &= Value(data.Fields, "QuoteNumber") == quoteIds[index];
                quoteFormatting &= Compact(Value(data.Fields, "QuoteNumber")).TrimEnd('.', ',') == quoteIds[index];
                currencies &= Value(data.Fields, "BillingCurrency") == (index == 2 ? "EUR" : "USD");
                amountsValid &= index == 3 || HasAmount(Value(data.Fields, "TotalDealValue"), amounts[index]);
            }
            else
            {
                fieldsValid = quoteIdsValid = quoteFormatting = currencies = amountsValid = false;
            }
            var table = data.Tables.Count == 1 ? data.Tables[0] : null;
            var expectedRows = customerCase == "schema-long" ? 300 : 2;
            rowsValid &= table is { TableName: "SubscriptionPricing" } && table.Rows.Count == expectedRows;
            if (table is null) continue;
            var expectedParts = Enumerable.Range(1, expectedRows).Select(number =>
                customerCase == "schema-long" ? $"QA-SKU-{number:0000}" : $"QA-SKU-{number:000}")
                .ToHashSet(StringComparer.Ordinal);
            rowsValid &= table.Rows.Select(row => Value(row.Fields, "PartNumber")).ToHashSet(StringComparer.Ordinal)
                .SetEquals(expectedParts) &&
                table.Rows.All(row => ExactFields(row.Fields, ["PartNumber", "ProductName", "UnitPrice"]) &&
                    String.Equals(Compact(Value(row.Fields, "ProductName")), Compact(customerCase == "schema-long"
                        ? "Managed service line " + Value(row.Fields, "PartNumber")[7..].TrimStart('0').PadLeft(3, '0')
                        : Value(row.Fields, "PartNumber") == "QA-SKU-001"
                            ? "Managed operations subscription" : "Reporting and support service"),
                        StringComparison.OrdinalIgnoreCase) &&
                    HasAmount(Value(row.Fields, "UnitPrice"), customerCase == "schema-long" ||
                        Value(row.Fields, "PartNumber") == "QA-SKU-001" ? 150m : 75m));
        }
        checks.Add(Check("SchemaFields", fieldsValid));
        checks.Add(Check("SchemaFieldShape", fieldShape));
        checks.Add(Check("SchemaCustomer", customers));
        checks.Add(Check("SchemaCustomerFormatting", customerFormatting));
        checks.Add(Check("SchemaQuoteIds", quoteIdsValid));
        checks.Add(Check("SchemaQuoteFormatting", quoteFormatting));
        checks.Add(Check("SchemaCurrencies", currencies));
        checks.Add(Check("SchemaAmounts", amountsValid));
        checks.Add(Check("SchemaRows", rowsValid));
        if (customerCase == "schema-price") checks.Add(Check("SchemaMissingAmount", missingValid));
        return checks;
    }

    private static bool ExactFields(IReadOnlyList<ExtractedFieldValue> fields, string[] names) =>
        fields.Count == names.Length && fields.Select(field => field.FieldName)
            .ToHashSet(StringComparer.Ordinal).SetEquals(names);

    private static string Value(IReadOnlyList<ExtractedFieldValue> fields, string name) =>
        fields.FirstOrDefault(field => field.FieldName == name)?.FieldValue ?? String.Empty;

    private sealed class DisabledSchemaTranslation : IDocumentTranslationService
    {
        public Task<DocumentSliceTranslationResult> TranslatePdfSliceAsync(byte[]? sourcePdfBytes, string sliceFileName,
            string fallbackBodyText, IReadOnlyList<string> footers, string targetLanguage, string? azureTranslatorEndpoint,
            IReadOnlyList<string> doNotTranslateTerms, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Translation is disabled in this controlled schema check.");

        public Task<(string DetectedLanguage, bool WasTranslated, string TranslatedText)> TranslateSliceAsync(string text,
            string targetLanguage, string? azureTranslatorEndpoint, IReadOnlyList<string> doNotTranslateTerms,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Translation is disabled in this controlled schema check.");
    }
}
