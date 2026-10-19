using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.DocumentTranslation;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using DocIntel.Core.Storage;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocIntel.Core;

public sealed class AzureIntegrationOptions
{
    public const string SectionName = "AzureAI";

    /// <summary>
    /// Microsoft Foundry Inference endpoint (e.g. "https://aif-dociq-dev-westus.services.ai.azure.com/models").
    /// </summary>
    public string? FoundryInferenceEndpoint { get; set; }

    /// <summary>
    /// Azure OpenAI endpoint (e.g. "https://aif-dociq-dev-westus.openai.azure.com/").
    /// </summary>
    public string? AzureOpenAIEndpoint { get; set; }

    /// <summary>
    /// Azure AI Translator endpoint (e.g. "https://trn-dociq-dev-westus.cognitiveservices.azure.com/").
    /// </summary>
    public string? TranslatorEndpoint { get; set; }

    /// <summary>
    /// Optional local OpenAI-compatible endpoint (e.g. Foundry Local "http://localhost:5272/v1" or Ollama "http://localhost:11434/v1").
    /// </summary>
    public string? LocalOpenAIEndpoint { get; set; }

    /// <summary>
    /// Optional Azure Cosmos DB account endpoint (e.g. "https://cosmos-docintel-dev.documents.azure.com:443/").
    /// When set, <see cref="CosmosHarnessStore"/> and <see cref="CosmosJobStore"/> (partitioned by <c>/tenantId</c>) are used.
    /// </summary>
    public string? CosmosEndpoint { get; set; }

    public string CosmosDatabaseName { get; set; } = "DocIntel";

    public string CosmosHarnessesContainer { get; set; } = "harnesses";

    public string CosmosJobsContainer { get; set; } = "jobs";

    /// <summary>
    /// Optional Azure Blob Storage service endpoint (e.g. "https://sadocinteldev.blob.core.windows.net/").
    /// When set, <see cref="BlobArtifactStore"/> stores PDFs in per-tenant <c>tenant-{tenantId}</c> containers.
    /// </summary>
    public string? BlobEndpoint { get; set; }

    /// <summary>
    /// Resolves the Azure Translator endpoint, automatically deriving the built-in Foundry AI Services
    /// cognitive endpoint (<c>https://&lt;foundry&gt;.cognitiveservices.azure.com/</c>) when no separate Translator resource is set.
    /// </summary>
    public string? ResolveEffectiveTranslatorEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(TranslatorEndpoint))
        {
            return TranslatorEndpoint;
        }

        if (!string.IsNullOrWhiteSpace(FoundryInferenceEndpoint) &&
            Uri.TryCreate(FoundryInferenceEndpoint, UriKind.Absolute, out var foundryUri) &&
            foundryUri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var resourceName = foundryUri.Host[..^".services.ai.azure.com".Length];
            return $"https://{resourceName}.cognitiveservices.azure.com/";
        }

        return null;
    }
}

/// <summary>
/// Dependency injection registration for the DocIntel vertical slices.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDocIntelCore(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        string? harnessFilePath = null,
        IEnumerable<ModelDescriptor>? modelDescriptors = null,
        IDictionary<string, IChatClient>? customModelClients = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var azureOptions = new AzureIntegrationOptions();
        configuration?.GetSection(AzureIntegrationOptions.SectionName).Bind(azureOptions);
        services.TryAddSingleton(azureOptions);

        var managedIdentityClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        TokenCredential credential = !string.IsNullOrWhiteSpace(managedIdentityClientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId))
            : new DefaultAzureCredential();

        // 1. Cosmos DB (shared 'harnesses' and 'jobs' containers partitioned by /tenantId) vs Local Fallback
        if (!string.IsNullOrWhiteSpace(azureOptions.CosmosEndpoint) &&
            Uri.TryCreate(azureOptions.CosmosEndpoint, UriKind.Absolute, out var cosmosUri))
        {
            var cosmosClient = new CosmosClient(
                cosmosUri.ToString(),
                credential,
                new CosmosClientOptions
                {
                    Serializer = new SystemTextJsonCosmosSerializer()
                });

            services.TryAddSingleton(cosmosClient);
            services.TryAddSingleton<IHarnessStore>(_ => new CosmosHarnessStore(
                cosmosClient,
                azureOptions.CosmosDatabaseName,
                azureOptions.CosmosHarnessesContainer));
            services.TryAddSingleton<IJobStore>(_ => new CosmosJobStore(
                cosmosClient,
                azureOptions.CosmosDatabaseName,
                azureOptions.CosmosJobsContainer));
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(harnessFilePath))
            {
                services.TryAddSingleton<IHarnessStore>(_ => new FileHarnessStore(harnessFilePath));
            }

            services.TryAddSingleton<IJobStore, InMemoryJobStore>();
        }

        // 2. Azure Blob Storage (per-tenant 'tenant-{tenantId}' containers) vs Local File Artifact Fallback
        if (!string.IsNullOrWhiteSpace(azureOptions.BlobEndpoint) &&
            Uri.TryCreate(azureOptions.BlobEndpoint, UriKind.Absolute, out var blobUri))
        {
            var blobServiceClient = new BlobServiceClient(blobUri, credential);
            services.TryAddSingleton(blobServiceClient);
            services.TryAddSingleton<IArtifactStore>(_ => new BlobArtifactStore(blobServiceClient));
        }
        else if (!string.IsNullOrWhiteSpace(harnessFilePath))
        {
            var localBlobsDir = Path.Combine(Path.GetDirectoryName(harnessFilePath) ?? AppContext.BaseDirectory, "blobs");
            services.TryAddSingleton<IArtifactStore>(_ => new LocalFileArtifactStore(localBlobsDir));
        }

        var descriptors = modelDescriptors ?? ModelCatalog.CreateDefaultDescriptors(
            azureOptions.FoundryInferenceEndpoint,
            azureOptions.AzureOpenAIEndpoint,
            azureOptions.LocalOpenAIEndpoint);

        services.TryAddSingleton<IModelCatalog>(_ => new ModelCatalog(descriptors, customModelClients, credential));
        services.TryAddSingleton<IDocumentTranslationService>(_ => new AzureDocumentTranslationService(credential));

        services.TryAddSingleton<PdfPigLayoutExtractor>();
        services.TryAddSingleton<IPageLayoutExtractor>(sp => sp.GetRequiredService<PdfPigLayoutExtractor>());
        services.TryAddSingleton<IPdfSlicer>(sp => sp.GetRequiredService<PdfPigLayoutExtractor>());

        services.TryAddSingleton<DocumentSplittingWorkflow>();
        services.TryAddSingleton<IJobQueue, ChannelJobQueue>();
        services.TryAddSingleton<IAskAiQueue, ChannelAskAiQueue>();
        services.TryAddSingleton<AskAiOrchestrator>();
        var askAiOptions = new AskAiExecutionOptions();
        configuration?.GetSection(AskAiExecutionOptions.SectionName).Bind(askAiOptions);
        askAiOptions.Validate();
        services.TryAddSingleton(askAiOptions);
        var runDirectory = askAiOptions.RunStorageDirectory;
        if (String.IsNullOrWhiteSpace(runDirectory) && !String.IsNullOrWhiteSpace(harnessFilePath))
        {
            runDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(harnessFilePath))!, "ask-ai-runs");
        }
        if (!String.IsNullOrWhiteSpace(runDirectory))
        {
            services.TryAddSingleton<IAskAiRunStore>(sp => new FileAskAiRunStore(
                runDirectory, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileAskAiRunStore>>()));
        }
        else
        {
            services.TryAddSingleton<IAskAiRunStore, InMemoryAskAiRunStore>();
        }
        services.TryAddSingleton<AskAiRunProcessor>();

        return services;
    }
}
