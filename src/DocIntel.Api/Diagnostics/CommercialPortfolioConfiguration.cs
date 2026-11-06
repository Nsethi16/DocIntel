using System.Text.Json;
using DocIntel.Core.Features.PromptColumns;
using DocIntel.Core.Harness;
using DocIntel.Core.Ocr;

namespace DocIntel.Api.Diagnostics;

/// <summary>Adds commercial processing definitions while retaining existing identity, channel, retention and model choices.</summary>
public static class CommercialPortfolioConfiguration
{
    public static TenantHarness Apply(TenantHarness source)
    {
        var tenant = JsonSerializer.Deserialize<TenantHarness>(JsonSerializer.SerializeToUtf8Bytes(source))
            ?? throw new InvalidDataException("Workspace configuration is unavailable.");
        TenantHarness.EnsureDefaultCapabilities(tenant);
        if (tenant.Ocr.Mode == OcrMode.Never) tenant.Ocr.Mode = OcrMode.Auto;
        tenant.DisplayName = "Deal Velocity";
        foreach (var key in new[] { FeatureHarness.SplitAndGroupKey, FeatureHarness.IdentifyDocumentsKey,
            FeatureHarness.DocumentTranslationKey, FeatureHarness.DocumentInsightKey, FeatureHarness.EntityExtractionKey,
            FeatureHarness.DataExtractionKey, FeatureHarness.PromptColumnsKey, FeatureHarness.DocumentGraphKey,
            FeatureHarness.AskAiKey }) tenant.Capabilities[key].Enabled = true;
        var identification = tenant.Capabilities[FeatureHarness.IdentifyDocumentsKey].IdentifyDocuments
            ??= new() { DecisionModelId = tenant.DecisionModelId };
        (string Name, string Rule)[] rules =
        [
            ("Commercial Quote", "A priced or budgetary sales offer proposing services or subscriptions to a customer. " +
                "Includes English commercial offers, Spanish propuestas/ofertas, French offres/devis and German Angebote. " +
                "A quote may have pending pricing. Excludes accepted purchase orders, invoices, master agreements and amendments."),
            ("Master Services Agreement", "A contract establishing general services, payment, confidentiality, liability, " +
                "renewal and termination terms between supplier and customer. " +
                "Excludes a standalone quotation, purchase order or amendment."),
            ("Statement of Work", "A project scope describing deliverables, milestones, acceptance criteria, responsibilities " +
                "and implementation dependencies under an existing agreement/quotation."),
            ("Purchase Order", "A customer procurement order accepting a referenced quotation, identifying a purchase-order " +
                "number, approved amount and invoice routing. Not a new supplier sales offer or invoice."),
            ("Invoice", "A supplier billing document with an invoice number, issue/due dates and amount due against " +
                "a quotation or order. Includes mobilization/deposit invoices. Not a new quoted deal."),
            ("Contract Amendment", "An amendment/addendum modifying an identified agreement or quote, with an effective date " +
                "and a stated incremental/revised value. Bilingual versions of the same amendment are one document."),
            ("Data Processing Addendum", "Data-processing terms covering instructions, hosting, subprocessors, transfers, " +
                "retention, deletion, incidents and privacy approval. Includes French accords de traitement des donnees.")
        ];
        foreach (var (name, instruction) in rules)
            if (!identification.Rules.Any(rule => String.Equals(rule.DocumentName, name, StringComparison.OrdinalIgnoreCase)))
                identification.Rules.Add(new() { DocumentName = name, IdentificationPrompt = instruction });
        var translation = tenant.Capabilities[FeatureHarness.DocumentTranslationKey].DocumentTranslation ??= new();
        translation.TargetLanguage = "en";
        foreach (var term in new[] { "Deal Velocity", "Arbor Ridge", "Summit Harbor", "Crescent Vale", "Redwood Lane", "DVX" })
            if (!translation.DoNotTranslateTerms.Contains(term, StringComparer.Ordinal))
                translation.DoNotTranslateTerms.Add(term);
        var entities = tenant.Capabilities[FeatureHarness.EntityExtractionKey].EntityExtraction ??= new();
        const string entitySuffix = "\nCommercial portfolio fields: Customer Name, Quote Number, Contract Number, " +
            "Purchase Order Number, Invoice Number, Billing Currency, Total Deal Value, Effective Date and Expiry Date. " +
            "Preserve legal names and exact identifiers in all source languages. Never infer missing amounts or approvals.";
        if (!entities.ExtractionPrompt.Contains(entitySuffix, StringComparison.Ordinal)) entities.ExtractionPrompt += entitySuffix;
        var extraction = tenant.Capabilities[FeatureHarness.DataExtractionKey].DataExtraction ??= new();
        foreach (var (name, _) in rules)
        {
            var profile = extraction.Profiles.FirstOrDefault(item =>
                String.Equals(item.DocumentName, name, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                profile = new() { DocumentName = name };
                extraction.Profiles.Add(profile);
            }
            foreach (var field in Fields(name))
                if (!profile.Fields.Any(item => String.Equals(item.FieldName, field.FieldName, StringComparison.OrdinalIgnoreCase)))
                    profile.Fields.Add(field);
            if (name == "Commercial Quote" && !profile.Tables.Any(item => item.TableName == "CommercialLineItems"))
                profile.Tables.Add(new()
                {
                    TableName = "CommercialLineItems",
                    Instruction = "Extract the actual commercial line items, not a duplicate of summary totals or repeated page headers. " +
                        "Preserve raw currency/amounts and missing values; do not derive unstated unit prices.",
                    Columns =
                    [
                        Column("Description", "Original service/SKU description."),
                        Column("QuantityOrTerm", "Source quantity or subscription term, if stated."),
                        Column("NetAmount", "Source extended net amount for this line; exclude tax and summary totals."),
                        Column("Currency", "Explicit currency for the line; never infer USD from a symbol.")
                    ]
                });
        }
        var prompts = tenant.Capabilities[FeatureHarness.PromptColumnsKey].PromptColumns ??= new();
        AddPrompt(prompts, "dvxclose", "Closing Readiness",
            PromptColumnBriefs.ClosingInstructions);
        AddPrompt(prompts, "dvxterms", "Commercial Exceptions",
            PromptColumnBriefs.ExceptionsInstructions);
        var error = tenant.ValidateAndNormalizeDocumentCatalog();
        if (error is not null) throw new InvalidDataException(error);
        return tenant;
    }

    private static IEnumerable<DataExtractionFieldSchema> Fields(string name)
    {
        var fields = new List<DataExtractionFieldSchema>
        {
            Field("DocumentNumber", "Exact identifier of this document, not a referenced document."),
            Field("CustomerLegalName", "Exact customer legal entity, preserving spelling in the original language."),
            Field("QuoteReference", "Referenced or current quote number, if stated."),
            Field("AgreementReference", "Referenced or current master agreement number, if stated."),
            Field("BillingCurrency", "Explicit source billing currency; do not infer it from symbols."),
            Field("ApprovalStatus", "Exact stated approval/payment/workflow status; an empty signature block is not approval.")
        };
        if (name == "Commercial Quote")
            fields.AddRange([
                Field("QuoteNumber", "Exact quotation identifier."),
                Field("IssueDate", "Source quotation issue date."),
                Field("ValidUntil", "Source quotation expiration date."),
                Field("NetDealValue", "Whole quotation value excluding tax; null for unpriced scope, not zero."),
                Field("TaxAmount", "Explicit tax amount, if stated; do not calculate it."),
                Field("GrossValue", "Explicit total including tax, if stated."),
                Field("PaymentTerms", "Stated payment term and milestone schedule.")
            ]);
        else if (name == "Contract Amendment")
            fields.AddRange([
                Field("EffectiveDate", "Stated amendment effective date."),
                Field("IncrementalNetValue", "The incremental fee increase, not the original or revised total."),
                Field("RevisedContractValue", "Explicit revised net contract value."),
                Field("PurchaseOrderDependency", "Explicit outstanding purchase-order increase or approval.")
            ]);
        else if (name == "Invoice")
            fields.AddRange([
                Field("InvoiceNumber", "Exact invoice identifier."),
                Field("PurchaseOrderNumber", "Referenced PO identifier."),
                Field("InvoiceDate", "Invoice issue date."),
                Field("DueDate", "Invoice due date."),
                Field("AmountDue", "Stated unpaid/due amount, preserving currency."),
                Field("PaymentStatus", "Explicit payment status; do not infer payment.")
            ]);
        else if (name == "Purchase Order")
            fields.AddRange([
                Field("PurchaseOrderNumber", "Exact PO identifier."),
                Field("NetOrderValue", "Explicit approved net purchase-order amount."),
                Field("ApprovalDate", "Stated procurement approval date."),
                Field("AmendmentCoverage", "Whether a stated amendment is included or still requires a PO increase.")
            ]);
        else
            fields.AddRange([
                Field("EffectiveDate", "Stated effective/start date."),
                Field("TerminationNotice", "Exact convenience termination notice, if stated."),
                Field("LiabilityCap", "Source liability cap and exceptions, if stated."),
                Field("Availability", "Source availability objective and measurement period."),
                Field("DataResidency", "Stated hosting/processing location and transfer restrictions."),
                Field("RetentionAndDeletion", "Source export, retention and deletion windows."),
                Field("OpenDependencies", "Explicit outstanding implementation, legal or privacy approvals.")
            ]);
        return fields;
    }

    private static DataExtractionFieldSchema Field(string name, string instruction) =>
        new() { FieldName = name, Instruction = instruction + " Use original source evidence; absent means null." };
    private static DataExtractionColumnSchema Column(string name, string instruction) =>
        new() { ColumnName = name, Instruction = instruction };
    private static void AddPrompt(PromptColumnsConfig prompts, string id, string name, string instruction)
    {
        if (!prompts.Columns.Any(item => item.ColumnId == id))
            prompts.Columns.Add(new() { ColumnId = id, ColumnName = name, PromptInstructions = instruction, FilterMode = "All" });
    }
}
