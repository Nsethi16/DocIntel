using System.Text.Json;
using DocIntel.Core.Harness;
using DocIntel.Core.Models;
using Microsoft.Extensions.Hosting;

namespace DocIntel.Api.Diagnostics;

/// <summary>
/// Runs one synthetic native Decision connectivity check without starting application services.
/// </summary>
public static class NativeDecisionDiagnostic
{
    private const string Flag = "--diagnose-decision";
    private const string Prefix = "DOCINTEL_DECISION_DIAGNOSTIC ";
    private const string ModelId = "decision-1";

    /// <summary>
    /// Recognizes malformed and case-variant diagnostic flags so they cannot fall through to host startup.
    /// </summary>
    /// <param name="arguments">The process arguments.</param>
    /// <returns>Whether any argument begins with the diagnostic flag.</returns>
    public static bool IsRequested(IEnumerable<string> arguments) =>
        arguments is not null && arguments.Any(argument =>
            argument is not null && argument.StartsWith(Flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes one fixed unavailable record without accepting configuration or provider error contents.
    /// </summary>
    /// <param name="output">The diagnostic output destination.</param>
    /// <returns>The unsuccessful exit code, 1.</returns>
    public static Task<int> WriteUnavailableAsync(TextWriter output) => WriteAsync(output, 0, success: false);

    /// <summary>
    /// Calls only the native Decision adapter using synthetic input and the configured runtime identity.
    /// </summary>
    /// <param name="arguments">Exactly the standalone, case-sensitive diagnostic flag.</param>
    /// <param name="models">The configured model catalog; development simulation must be disabled.</param>
    /// <param name="environment">The host environment, which must be Production.</param>
    /// <param name="runtimeClientId">The nonempty managed-identity client GUID supplied by the process.</param>
    /// <param name="output">Receives exactly one allowlisted, prefixed JSON record.</param>
    /// <param name="cancellationToken">Cancels the bounded connectivity check.</param>
    /// <returns>0 for a validated native response; otherwise 1.</returns>
    public static async Task<int> ExecuteAsync(
        string[] arguments,
        IModelCatalog models,
        IHostEnvironment environment,
        string? runtimeClientId,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        var httpStatus = 0;
        var success = false;
        try
        {
            if (arguments is [Flag] &&
                environment is not null && environment.IsProduction() &&
                Guid.TryParse(runtimeClientId, out var clientId) && clientId != Guid.Empty &&
                !cancellationToken.IsCancellationRequested &&
                models is not null && !models.AllowsDevelopmentSimulation)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(45));
                deadline.Token.ThrowIfCancellationRequested();
                var tenant = new TenantHarness
                {
                    TenantId = "native-decision-diagnostic",
                    DisplayName = "Native Decision connectivity diagnostic",
                    DecisionModelId = ModelId,
                    DefaultModelId = ModelId,
                    AllowedModels = [ModelId],
                    Capabilities = []
                };
                using var client = models.ResolveDecisionClient(tenant, ModelId);
                if (client is not null)
                {
                    var result = await client.ChooseAsync(
                        "Synthetic connectivity check: ready.",
                        "Select ready for this synthetic connectivity check; otherwise select undefined.",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["ready"] = "The synthetic connectivity check indicates readiness.",
                            ["undefined"] = "The synthetic connectivity check does not indicate readiness."
                        },
                        deadline.Token).WaitAsync(deadline.Token);
                    deadline.Token.ThrowIfCancellationRequested();
                    success = result is not null && result.Choice is "ready" or "undefined" &&
                        Double.IsFinite(result.Probability) && result.Probability is >= 0 and <= 1 &&
                        !String.IsNullOrWhiteSpace(result.DeploymentName);
                    if (success) httpStatus = 200;
                }
            }
        }
        catch (HttpRequestException exception)
        {
            httpStatus = exception.StatusCode is { } status ? (int)status : 0;
            success = false;
        }
        catch (Exception)
        {
            // Only fixed diagnostic fields leave this boundary, including on cancellation or provider failures.
            httpStatus = 0;
            success = false;
        }

        return await WriteAsync(output, httpStatus, success);
    }

    private static async Task<int> WriteAsync(TextWriter output, int httpStatus, bool success)
    {
        ArgumentNullException.ThrowIfNull(output);
        await output.WriteLineAsync(Prefix + JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            httpStatus,
            providerCode = success ? "None" : "UnclassifiedError",
            answerPresent = success,
            probabilityShape = success ? "Object" : "None"
        }));
        return success ? 0 : 1;
    }
}
