using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.DocumentTranslation;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
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

        if (!string.IsNullOrWhiteSpace(harnessFilePath))
        {
            services.TryAddSingleton<IHarnessStore>(_ => new FileHarnessStore(harnessFilePath));
        }

        var descriptors = modelDescriptors ?? ModelCatalog.CreateDefaultDescriptors(
            azureOptions.FoundryInferenceEndpoint,
            azureOptions.AzureOpenAIEndpoint,
            azureOptions.LocalOpenAIEndpoint);

        services.TryAddSingleton<IModelCatalog>(_ => new ModelCatalog(descriptors, customModelClients));
        services.TryAddSingleton<IDocumentTranslationService, AzureDocumentTranslationService>();

        services.TryAddSingleton<PdfPigLayoutExtractor>();
        services.TryAddSingleton<IPageLayoutExtractor>(sp => sp.GetRequiredService<PdfPigLayoutExtractor>());
        services.TryAddSingleton<IPdfSlicer>(sp => sp.GetRequiredService<PdfPigLayoutExtractor>());

        services.TryAddSingleton<DocumentSplittingWorkflow>();
        services.TryAddSingleton<IJobStore, InMemoryJobStore>();
        services.TryAddSingleton<IJobQueue, ChannelJobQueue>();

        return services;
    }
}
