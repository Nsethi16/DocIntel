using DocIntel.Core;
using DocIntel.Core.Channels;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;

namespace DocIntel.Api.Endpoints;

/// <summary>
/// Projects configuration only, without creating provider clients or verifying deployments or delivery.
/// </summary>
internal static class TenantSetupReport
{
    internal static object Create(
        TenantHarness tenant, IModelCatalog catalog, AzureIntegrationOptions azure, bool directoryConfigured,
        EmailChannelOptions email, TeamsChannelOptions teams, bool hasDocuments)
    {
        var models = catalog.ListModels().Select(model => Describe(
            model, model.Provider == ModelProvider.MicrosoftDecision ? "decision" : "language",
            tenant, catalog.AllowsDevelopmentSimulation)).ToList();
        AddSelection(models, catalog, tenant, tenant.DecisionModelId, "decision");
        AddSelection(models, catalog, tenant, tenant.DefaultModelId, "language");

        var selected = models.Where(model => model.Selected).ToList();
        var modelsConfigured = selected.Count == 2 && selected.All(model => model.Configured) &&
            AskAiConnectionConfigured(tenant, catalog);
        var layoutConfigured = ValidEndpoint(azure.ContentUnderstandingEndpoint);
        var rulesConfigured = Feature(tenant, FeatureHarness.IdentifyDocumentsKey)?.IdentifyDocuments?.Rules
            .Any(rule => rule.IsActive && !String.IsNullOrWhiteSpace(rule.DocumentName) &&
                !String.IsNullOrWhiteSpace(rule.IdentificationPrompt)) == true;
        var extractionConfigured =
            Feature(tenant, FeatureHarness.DataExtractionKey)?.DataExtraction?.Profiles.Any(profile =>
                !String.IsNullOrWhiteSpace(profile.DocumentName) &&
                (profile.Fields.Any(field => !String.IsNullOrWhiteSpace(field.FieldName) &&
                    !String.IsNullOrWhiteSpace(field.Instruction)) ||
                 profile.Tables.Any(table => !String.IsNullOrWhiteSpace(table.TableName) &&
                    !String.IsNullOrWhiteSpace(table.Instruction) && table.Columns.Any(column =>
                        !String.IsNullOrWhiteSpace(column.ColumnName) &&
                        !String.IsNullOrWhiteSpace(column.Instruction))))) == true ||
            Feature(tenant, FeatureHarness.PromptColumnsKey)?.PromptColumns?.Columns.Any(column =>
                column.IsActive && !String.IsNullOrWhiteSpace(column.ColumnName) &&
                !String.IsNullOrWhiteSpace(column.PromptInstructions)) == true;
        // Enabled operator options have already passed the channel hosting validators at startup.
        var emailConfigured = email.Enabled &&
            String.Equals(email.ApplicationTenantId, tenant.TenantId, StringComparison.Ordinal);
        var teamsConfigured = teams.Enabled &&
            String.Equals(teams.ApplicationTenantId, tenant.TenantId, StringComparison.Ordinal);
        var emailEnabled = tenant.Agent?.EmailEnabled == true;
        var teamsEnabled = tenant.Agent?.TeamsEnabled == true;
        var channelsEnabled = emailEnabled || teamsEnabled;
        var channelsConfigured = (!emailEnabled || emailConfigured) && (!teamsEnabled || teamsConfigured);

        return new
        {
            workspace = new { tenant.TenantId, tenant.DisplayName, tenant.HarnessVersion },
            models,
            checks = new[]
            {
                Check("models", "Model connections", modelsConfigured ? "Configured" : "ActionRequired",
                    "Configuration only: select known native Decision and language models with valid connections. " +
                    "Ask AI also requires a tool-capable Azure OpenAI or OpenAI-compatible provider. " +
                    "A shared endpoint does not prove any deployment exists; " +
                    "inference and permissions are not verified."),
                Check("content-understanding", "Content Understanding",
                    layoutConfigured ? "Configured" : "ActionRequired",
                    layoutConfigured
                        ? "Layout endpoint configuration is present. " +
                          "Analyzer access and document processing are not verified."
                        : "Ask an operator to configure Content Understanding before processing documents."),
                Check("decision-rules", "Document identification rules",
                    rulesConfigured ? "Configured" : "ActionRequired",
                    "Define active document names and identification instructions. " +
                    "Saved rules are not a tested classifier."),
                Check("extraction", "Extraction setup", extractionConfigured ? "Configured" : "ActionRequired",
                    "Define fields, table columns or custom extraction prompts, " +
                    "then review results on a sample document."),
                Check("people", "People and membership", "NotStarted",
                    directoryConfigured
                        ? "Directory configuration is present. " +
                          "Review saved Users memberships and least-privilege roles; " +
                          "directory access and each person's permissions have not been tested."
                        : "Ask an operator to configure the directory, then assign people and roles in Users. " +
                          "Signing in alone does not grant customer access."),
                Check("data", "Saved documents", hasDocuments ? "Configured" : "NotStarted",
                    hasDocuments
                        ? "Visible completed documents are saved. " +
                          "Development results may be present; review source evidence."
                        : "No visible completed documents are available. " +
                          "Upload a document after configuring processing."),
                Check("channels", "Optional email and Teams", !channelsEnabled ? "NotStarted" :
                    channelsConfigured ? "Configured" : "ActionRequired",
                    "Channels are optional. " +
                    "Configuration and customer opt-in do not verify mailbox access, bot consent, " +
                    "sender authorization or message delivery. No test message has been sent.")
            },
            directory = new { configured = directoryConfigured },
            channels = new
            {
                email = new
                {
                    configured = emailConfigured,
                    enabled = emailEnabled,
                    address = emailConfigured && EmailChannelOptions.IsAddress(email.MailboxAddress)
                        ? email.MailboxAddress : null,
                    state = ChannelState(emailConfigured, emailEnabled),
                    verified = false,
                    setupSteps = new[]
                    {
                        "Ask an operator to bind the mailbox and application customer " +
                        "with least-privilege permissions.",
                        "Assign authorized customer members and choose the sender confirmation policy.",
                        "Enable email for this customer only after operator configuration.",
                        "Separately test an authorized email question and reply; " +
                        "this report does not verify delivery."
                    }
                },
                teams = new
                {
                    configured = teamsConfigured,
                    enabled = teamsEnabled,
                    state = ChannelState(teamsConfigured, teamsEnabled),
                    verified = false,
                    setupSteps = new[]
                    {
                        "Ask an operator to register and bind the authenticated Teams bot to this customer.",
                        "Review installation, consent and customer membership with your organization administrator.",
                        "Enable Teams for this customer only after operator configuration.",
                        "Separately test an authorized conversation and private reply; " +
                        "this report does not verify delivery."
                    }
                }
            }
        };
    }

    private static void AddSelection(
        List<SetupModel> models, IModelCatalog catalog, TenantHarness tenant, string? modelId, string purpose)
    {
        if (models.Any(model => model.Purpose == purpose &&
            String.Equals(model.ModelId, modelId, StringComparison.OrdinalIgnoreCase))) return;
        var descriptor = catalog.ListModels().FirstOrDefault(model =>
            String.Equals(model.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is not null)
        {
            models.Add(Describe(descriptor, purpose, tenant, catalog.AllowsDevelopmentSimulation));
            return;
        }
        var safeId = modelId is { Length: > 0 and <= 128 } &&
            modelId.All(character => Char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
                ? modelId : $"unknown-{purpose}-selection";
        models.Add(new SetupModel(safeId, "Unknown selected model", purpose, false, true, false, false,
            "UnknownSelection"));
    }

    private static SetupModel Describe(
        ModelDescriptor model, string purpose, TenantHarness tenant, bool allowsSimulation)
    {
        var selected = String.Equals(model.ModelId,
            purpose == "decision" ? tenant.DecisionModelId : tenant.DefaultModelId, StringComparison.OrdinalIgnoreCase);
        var native = model.Provider == ModelProvider.MicrosoftDecision;
        var development = model.Provider == ModelProvider.Simulated ||
            allowsSimulation && String.IsNullOrWhiteSpace(model.Endpoint);
        var compatible = purpose == "decision" ? native :
            model.Provider is ModelProvider.AzureOpenAI or ModelProvider.OpenAICompatible or
                ModelProvider.FoundryInference;
        var configured = compatible && !development && ValidEndpoint(model.Endpoint, native) &&
            model.DeploymentName is { Length: > 0 and <= 128 } &&
            model.DeploymentName.All(character => Char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_' or '.');
        var state = !compatible ? purpose == "decision" ? "NativeDecisionRequired" : "LanguageModelRequired" :
            development ? "DevelopmentOnly" : configured ? "EndpointConfiguredUnverified" :
            String.IsNullOrWhiteSpace(model.Endpoint) ? "NotConfigured" : "InvalidConfiguration";
        return new SetupModel(model.ModelId, model.DisplayName, purpose, configured, selected, development,
            !native && model.SupportsPdfInputs, state);
    }

    private static bool ValidEndpoint(string? value, bool resourceRoot = false) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) &&
        String.IsNullOrEmpty(uri.UserInfo) && String.IsNullOrEmpty(uri.Query) && String.IsNullOrEmpty(uri.Fragment) &&
        (!resourceRoot || uri.AbsolutePath == "/");

    private static bool AskAiConnectionConfigured(TenantHarness tenant, IModelCatalog catalog)
    {
        var feature = Feature(tenant, FeatureHarness.AskAiKey);
        if (feature is null) return true;
        var modelId = String.IsNullOrWhiteSpace(feature.ModelIdOverride)
            ? tenant.DefaultModelId : feature.ModelIdOverride;
        var descriptor = catalog.ListModels().FirstOrDefault(model =>
            String.Equals(model.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        return descriptor is not null &&
            descriptor.Provider is ModelProvider.AzureOpenAI or ModelProvider.OpenAICompatible &&
            Describe(descriptor, "language", tenant, catalog.AllowsDevelopmentSimulation).Configured;
    }

    private static FeatureHarness? Feature(TenantHarness tenant, string name) =>
        tenant.Capabilities.TryGetValue(name, out var feature) && feature.Enabled ? feature : null;

    private static object Check(string id, string title, string state, string description) =>
        new { id, title, state, description };

    private static string ChannelState(bool configured, bool enabled) =>
        !configured ? enabled ? "ActionRequired" : "NotConfigured" :
        enabled ? "ConfiguredUnverified" : "Disabled";

    private sealed record SetupModel(
        string ModelId, string DisplayName, string Purpose, bool Configured, bool Selected,
        bool Development, bool SupportsPdfInputs, string State);
}
