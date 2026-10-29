using System.Text.Json;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;
using Microsoft.Extensions.AI;

namespace DocIntel.Api.Diagnostics;

/// <summary>Operator-only provider check with synthetic input, disabled tools and no document/run-store dependency.</summary>
public static class AskAiContractDiagnostic
{
    /// <summary>Checks the configured provider only when live model probing was already explicitly enabled.</summary>
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
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var tenant = await harness.GetTenantAsync(tenantId, deadline.Token);
            if (tenant is not { IsActive: true } ||
                !tenant.Capabilities.TryGetValue(FeatureHarness.AskAiKey, out var feature) || !feature.Enabled)
            {
                await WriteAsync(output, "Refused", "AskAiUnavailable");
                return 2;
            }
            using var client = models.ResolveRequiredChatClient(tenant, feature.ModelIdOverride);
            var selectedModel = models.ResolveModelId(tenant, feature.ModelIdOverride);
            var descriptor = models.ListModels().FirstOrDefault(model => model.ModelId == selectedModel);
            var knownModel = ModelCatalog.CreateDefaultDescriptors().Any(model => model.ModelId == selectedModel)
                ? selectedModel : "OtherConfiguredModel";
            var fullAccepted = false;
            foreach (var variant in new[] { "Plain", "Json", "Tools", "Combined" })
            {
                var options = AskAiOrchestrator.CreateProviderContractOptions(executionOptions);
                if (variant is "Plain" or "Json")
                {
                    options.Tools = null;
                    options.ToolMode = null;
                    options.AllowMultipleToolCalls = null;
                }
                if (variant is "Plain" or "Tools") options.ResponseFormat = null;
                try
                {
                    var response = await client.GetResponseAsync(
                        [new ChatMessage(ChatRole.User, "Synthetic connectivity check. Say hello without calling tools.")],
                        options, deadline.Token);
                    var unexpectedCall = response.Messages.SelectMany(message => message.Contents)
                        .Any(content => content is FunctionCallContent);
                    await WriteAsync(output, unexpectedCall ? "Refused" : "Accepted",
                        unexpectedCall ? "UnexpectedToolCall" : "None", variant: variant,
                        model: knownModel, modelProvider: descriptor?.Provider.ToString());
                    if (variant == "Combined") fullAccepted = !unexpectedCall;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await WriteAsync(output, "Failed", AskAiFailureDiagnostics.GetCode(exception),
                        AskAiProviderFailure.FromException(exception), variant, knownModel,
                        descriptor?.Provider.ToString());
                }
            }
            return fullAccepted ? 0 : 1;
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
        string? variant = null, string? model = null, string? modelProvider = null) =>
        output.WriteLineAsync("DOCINTEL_DIAGNOSTIC " + JsonSerializer.Serialize(new
        {
            phase = "Contract", status, failureCode, httpStatus = provider?.HttpStatus,
            providerCode = provider?.Code, providerParameter = provider?.Parameter,
            schemaKeyword = provider?.SchemaKeyword, providerHints = provider?.Hints,
            variant, model, modelProvider
        }));
}
