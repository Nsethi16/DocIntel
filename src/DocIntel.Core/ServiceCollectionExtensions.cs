using Azure.Core;
using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.DocumentTranslation;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using DocIntel.Core.Ocr;
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

    public string? DocumentIntelligenceEndpoint { get; set; }

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

    public string? QueueEndpoint { get; set; }

    /// <summary>Only loopback storage emulators may use this setting; production uses managed identity.</summary>
    public string? DevelopmentStorageConnectionString { get; set; }

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
        IDictionary<string, IChatClient>? customModelClients = null,
        bool allowDevelopmentSimulation = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        var simulationAllowed = configuration?.GetValue<bool?>("Models:AllowDevelopmentSimulation")
            ?? allowDevelopmentSimulation;

        var azureOptions = new AzureIntegrationOptions();
        configuration?.GetSection(AzureIntegrationOptions.SectionName).Bind(azureOptions);
        services.TryAddSingleton(azureOptions);
        var askAiOptions = new AskAiExecutionOptions();
        configuration?.GetSection(AskAiExecutionOptions.SectionName).Bind(askAiOptions);
        askAiOptions.Validate();
        services.TryAddSingleton(askAiOptions);
        var documentOptions = new DocumentProcessingOptions();
        configuration?.GetSection(DocumentProcessingOptions.SectionName).Bind(documentOptions);
        documentOptions.Validate();
        services.TryAddSingleton(documentOptions);

        var managedIdentityClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        TokenCredential credential = !string.IsNullOrWhiteSpace(managedIdentityClientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId))
            : new DefaultAzureCredential();
        services.TryAddSingleton(credential);
        var ocrUri = Endpoint(azureOptions.DocumentIntelligenceEndpoint);
        if (ocrUri is not null)
        {
            services.TryAddSingleton(_ => new DocumentIntelligenceClient(ocrUri, credential,
                new DocumentIntelligenceClientOptions(DocumentIntelligenceClientOptions.ServiceVersion.V2024_11_30)));
            services.TryAddSingleton<IOcrProvider>(sp => new AzureDocumentIntelligenceOcrProvider(
                sp.GetRequiredService<DocumentIntelligenceClient>(), ocrUri));
        }
        services.TryAddSingleton<OcrProcessor>();
        var cosmosUri = Endpoint(azureOptions.CosmosEndpoint);
        var blobUri = Endpoint(azureOptions.BlobEndpoint);
        var queueUri = Endpoint(azureOptions.QueueEndpoint);
        if (new[] { askAiOptions.DispatchQueueName, askAiOptions.PoisonQueueName,
            documentOptions.QueueName, documentOptions.PoisonQueueName }.Distinct(StringComparer.Ordinal).Count() != 4)
            throw new InvalidOperationException("Ask AI and document dispatch/poison queues must have distinct names.");
        if (queueUri is not null && blobUri is null &&
            String.IsNullOrWhiteSpace(azureOptions.DevelopmentStorageConnectionString))
            throw new InvalidOperationException("A durable dispatch endpoint requires shared run Blob storage.");

        // 1. Cosmos DB (shared 'harnesses' and 'jobs' containers partitioned by /tenantId) vs Local Fallback
        if (cosmosUri is not null)
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
            services.TryAddSingleton<IJobStore>(sp => new CosmosJobStore(
                cosmosClient,
                azureOptions.CosmosDatabaseName,
                azureOptions.CosmosJobsContainer,
                logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CosmosJobStore>>()));
            services.TryAddSingleton<IOcrUsageStore>(_ => new CosmosOcrUsageStore(
                cosmosClient.GetContainer(azureOptions.CosmosDatabaseName, "ocr-usage")));
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(harnessFilePath))
            {
                services.TryAddSingleton<IHarnessStore>(_ => new FileHarnessStore(harnessFilePath));
            }

            if (!String.IsNullOrWhiteSpace(harnessFilePath) || !String.IsNullOrWhiteSpace(documentOptions.DatabasePath))
            {
                var database = documentOptions.DatabasePath ?? Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(harnessFilePath!))!, "document-jobs.sqlite");
                services.TryAddSingleton<IJobStore>(sp => new SqliteJobStore(
                    database, logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqliteJobStore>>()));
                services.TryAddSingleton<IOcrUsageStore>(sp => new SqliteJobStore(
                    database, logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqliteJobStore>>()));
            }
            else services.TryAddSingleton<IJobStore, InMemoryJobStore>();
        }
        services.TryAddSingleton<IOcrUsageStore, InMemoryOcrUsageStore>();

        // 2. Azure Blob Storage (per-tenant 'tenant-{tenantId}' containers) vs Local File Artifact Fallback
        BlobServiceClient? sharedBlobs = null;
        QueueServiceClient? sharedQueues = null;
        if (!String.IsNullOrWhiteSpace(azureOptions.DevelopmentStorageConnectionString))
        {
            sharedBlobs = new BlobServiceClient(azureOptions.DevelopmentStorageConnectionString);
            sharedQueues = new QueueServiceClient(azureOptions.DevelopmentStorageConnectionString,
                new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None });
            if (!sharedBlobs.Uri.IsLoopback || !sharedQueues.Uri.IsLoopback)
            {
                throw new InvalidOperationException("Development storage connections must use loopback endpoints.");
            }
        }
        else if (blobUri is not null)
        {
            sharedBlobs = new BlobServiceClient(blobUri, credential);
            if (queueUri is null)
            {
                var host = blobUri.Host.Replace(".blob.", ".queue.", StringComparison.OrdinalIgnoreCase);
                if (host == blobUri.Host)
                    throw new InvalidOperationException("Configure QueueEndpoint for a nonstandard BlobEndpoint.");
                queueUri = new UriBuilder(blobUri) { Host = host }.Uri;
            }
            sharedQueues = new QueueServiceClient(queueUri, credential,
                new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None });
        }
        if (sharedBlobs is not null && sharedQueues is not null)
        {
            if (cosmosUri is null)
                throw new InvalidOperationException("Shared document execution requires Cosmos metadata storage.");
            if (!String.IsNullOrWhiteSpace(askAiOptions.RunStorageDirectory))
                throw new InvalidOperationException("Shared storage cannot be combined with a local run directory.");
            services.TryAddSingleton(sharedBlobs);
            services.TryAddSingleton(sharedQueues);
            services.TryAddSingleton<IArtifactStore>(_ => new BlobArtifactStore(sharedBlobs));
            var clients = new CloudAskAiClients(sharedBlobs.GetBlobContainerClient(askAiOptions.RunContainerName),
                sharedQueues.GetQueueClient(askAiOptions.DispatchQueueName),
                sharedQueues.GetQueueClient(askAiOptions.PoisonQueueName));
            services.TryAddSingleton(clients);
            services.TryAddSingleton<IAskAiRunStore>(sp => new AzureBlobAskAiRunStore(
                clients.Runs, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AzureBlobAskAiRunStore>>()));
            services.TryAddSingleton<IAskAiQueue>(sp => new AzureQueueAskAiQueue(
                clients.Dispatch, clients.Poison, askAiOptions,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AzureQueueAskAiQueue>>()));
            var documents = new CloudDocumentClients(
                sharedQueues.GetQueueClient(documentOptions.QueueName),
                sharedQueues.GetQueueClient(documentOptions.PoisonQueueName));
            services.TryAddSingleton(documents);
            services.TryAddSingleton<IJobQueue>(sp => new AzureQueueJobQueue(
                documents.Dispatch, documents.Poison, documentOptions,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AzureQueueJobQueue>>()));
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

        services.TryAddSingleton<IModelCatalog>(_ => new ModelCatalog(
            descriptors, customModelClients, credential, simulationAllowed));
        services.TryAddSingleton<IDocumentTranslationService>(_ => new AzureDocumentTranslationService(
            credential, allowDevelopmentSimulation: simulationAllowed));

        services.TryAddSingleton<PdfPigLayoutExtractor>();
        services.TryAddSingleton<IPageLayoutExtractor>(sp => sp.GetRequiredService<PdfPigLayoutExtractor>());
        services.TryAddSingleton<IPdfSlicer>(sp => sp.GetRequiredService<PdfPigLayoutExtractor>());

        services.TryAddSingleton<DocumentSplittingWorkflow>();
        services.TryAddSingleton<IJobQueue, ChannelJobQueue>();
        services.TryAddSingleton<IAskAiQueue, ChannelAskAiQueue>();
        services.TryAddSingleton<AskAiOrchestrator>();
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
        services.TryAddSingleton<DocumentJobProcessor>();

        return services;
    }

    private static Uri? Endpoint(string? value)
    {
        if (String.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Query) ||
            !String.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("Configured Azure data endpoints must use HTTPS without embedded credentials.");
        }
        return uri;
    }
}
