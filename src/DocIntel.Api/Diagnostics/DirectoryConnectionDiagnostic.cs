using System.Net;
using System.Text.Json;
using Azure.Core;
using DocIntel.Api.Identity;
using DocIntel.Core;
using DocIntel.Core.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocIntel.Api.Diagnostics;

/// <summary>
/// Operator-only, read-only runtime directory verification; never activates the directory or starts the host.
/// </summary>
public static class DirectoryConnectionDiagnostic
{
    private const string Prefix = "DOCINTEL_DIRECTORY_DIAGNOSTIC ";
    private const string OwnerFlag = "--diagnose-directory";
    private const string TenantFlag = "--diagnose-directory-tenant";
    private const string ClientFlag = "--diagnose-directory-client";
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Also recognizes incomplete or ambiguous directory invocations so they fail without starting the app.
    /// </summary>
    public static bool IsRequested(IEnumerable<string> arguments) =>
        arguments.Any(argument => argument.StartsWith(OwnerFlag, StringComparison.Ordinal));

    /// <summary>
    /// Reports an initialization failure without accepting configuration contents or exception details.
    /// </summary>
    public static Task<int> WriteUnavailableAsync(TextWriter output) =>
        WriteAsync(output, new DiagnosticResult { FailureCode = FailureCode.DirectoryUnavailable });

    /// <summary>
    /// Emits one allowlisted record using only the runtime credential and a separately enabled Graph adapter.
    /// </summary>
    public static async Task<int> ExecuteAsync(
        string[] arguments, IConfiguration configuration, IHostEnvironment environment, IServiceCollection services,
        string? runtimeClientId, TextWriter output, CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        var result = new DiagnosticResult();
        try
        {
            var identity = new IdentityOptions();
            var options = new EnterpriseDirectoryOptions();
            var validConfiguration = TryBindOptions(configuration, identity, options);
            result = result with { DirectoryEnabled = options.Enabled };
            var duration = timeout ?? MaximumDuration;
            var ownerId = Guid.Empty;
            var inputFailure = validConfiguration
                ? Validate(arguments, configuration, environment, identity, options, runtimeClientId,
                    duration, out ownerId)
                : FailureCode.InvalidInput;
            if (inputFailure != FailureCode.None)
            {
                result = result with { FailureCode = inputFailure };
            }
            else
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(duration);
                deadline.Token.ThrowIfCancellationRequested();
                // Core selects ManagedIdentityCredential from the actual process AZURE_CLIENT_ID, not CLI config.
                // Register normally, but never resolve any storage, harness, worker or model services.
                services.AddDocIntelCore(configuration: configuration);
                services.AddHttpClient(GraphEnterpriseDirectory.HttpClientName,
                        client => client.Timeout = TimeSpan.FromSeconds(15))
                    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
                await using var provider = services.BuildServiceProvider();
                var directory = new GraphEnterpriseDirectory(
                    provider.GetRequiredService<IHttpClientFactory>(),
                    provider.GetRequiredService<TokenCredential>(),
                    new EnterpriseDirectoryOptions
                    {
                        Enabled = true,
                        DevelopmentEndpoint = options.DevelopmentEndpoint
                    },
                    identity);
                var owner = await directory.GetAsync(ownerId, deadline.Token).WaitAsync(deadline.Token);
                result = result with { RuntimeIdentityVerified = true };
                var ownerFailure = owner is null || owner.ObjectId != ownerId ? FailureCode.OwnerNotFound :
                    !owner.AccountEnabled ? FailureCode.OwnerDisabled :
                    !owner.Member ? FailureCode.OwnerNotMember :
                    !EmailChannelOptions.IsAddress(owner.Email) &&
                    !EmailChannelOptions.IsAddress(owner.UserPrincipalName) ? FailureCode.OwnerNoWorkAddress :
                    FailureCode.None;
                if (ownerFailure != FailureCode.None)
                {
                    result = result with { FailureCode = ownerFailure };
                }
                else
                {
                    result = result with { OwnerEligible = true };
                    var address = EmailChannelOptions.IsAddress(owner!.Email) ? owner.Email : owner.UserPrincipalName;
                    // A valid empty first page still proves picker access; the owner need not be among its first 20.
                    await directory.SearchAsync(address[..2], deadline.Token).WaitAsync(deadline.Token);
                    result = result with
                    {
                        Status = DiagnosticStatus.Connected,
                        FailureCode = FailureCode.None,
                        SearchVerified = true
                    };
                }
            }
        }
        catch (OperationCanceledException)
        {
            result = result with { Status = DiagnosticStatus.TimedOut, FailureCode = FailureCode.TimedOut };
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Forbidden)
        {
            result = result with
            {
                Status = DiagnosticStatus.AwaitingAdminApproval,
                FailureCode = FailureCode.DirectoryForbidden
            };
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            result = result with
            {
                Status = DiagnosticStatus.ActionRequired,
                FailureCode = FailureCode.DirectoryUnauthorized
            };
        }
        catch (Exception)
        {
            result = result with
            {
                Status = DiagnosticStatus.ActionRequired,
                FailureCode = FailureCode.DirectoryUnavailable
            };
        }

        return await WriteAsync(output, result);
    }

    private static async Task<int> WriteAsync(TextWriter output, DiagnosticResult result)
    {
        await output.WriteLineAsync(Prefix + JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            status = result.Status.ToString(),
            failureCode = result.FailureCode.ToString(),
            directoryEnabled = result.DirectoryEnabled,
            runtimeIdentityVerified = result.RuntimeIdentityVerified,
            ownerEligible = result.OwnerEligible,
            searchVerified = result.SearchVerified
        }));
        return result.Status == DiagnosticStatus.Connected ? 0 : 1;
    }

    private static bool TryBindOptions(
        IConfiguration configuration, IdentityOptions identity, EnterpriseDirectoryOptions options)
    {
        try
        {
            configuration.GetSection("Identity:Directory").Bind(options);
            configuration.GetSection(IdentityOptions.SectionName).Bind(identity);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static FailureCode Validate(
        string[] arguments, IConfiguration configuration, IHostEnvironment environment,
        IdentityOptions identity, EnterpriseDirectoryOptions options, string? runtimeClientId, TimeSpan duration,
        out Guid ownerId)
    {
        ownerId = Guid.Empty;
        if (arguments.Length != 6 || duration <= TimeSpan.Zero || duration > MaximumDuration)
            return FailureCode.InvalidInput;
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var flag = arguments[index];
            if (flag is not (OwnerFlag or TenantFlag or ClientFlag) ||
                !Guid.TryParse(arguments[index + 1], out var id) || id == Guid.Empty || !ids.TryAdd(flag, id))
                return FailureCode.InvalidInput;
        }
        if (identity.Mode != IdentityMode.Entra) return FailureCode.InvalidInput;
        ownerId = ids[OwnerFlag];
        if (identity.DirectoryTenantId != ids[TenantFlag]) return FailureCode.TenantMismatch;
        if (!Guid.TryParse(identity.ManagedIdentityClientId, out var identityClientId) ||
            !Guid.TryParse(configuration["AZURE_CLIENT_ID"], out var configuredClientId) ||
            !Guid.TryParse(runtimeClientId, out var processClientId) ||
            ids[ClientFlag] != identityClientId || identityClientId != configuredClientId ||
            configuredClientId != processClientId)
            return FailureCode.RuntimeIdentityMismatch;
        if (options.DevelopmentEndpoint is not null &&
            (!environment.IsDevelopment() || !identity.AllowLoopbackAuthority ||
             !IdentityOptions.IsLoopbackUrl(identity.LoopbackAuthority) ||
             !IdentityOptions.IsLoopbackUrl(options.DevelopmentEndpoint) ||
             !options.DevelopmentEndpoint.EndsWith("/", StringComparison.Ordinal) ||
             String.IsNullOrWhiteSpace(configuration["urls"]) ||
             !configuration["urls"]!.Split(';', StringSplitOptions.RemoveEmptyEntries)
                 .All(IdentityOptions.IsLoopbackUrl)))
            return FailureCode.InvalidInput;
        return FailureCode.None;
    }

    private enum DiagnosticStatus { Connected, AwaitingAdminApproval, ActionRequired, TimedOut }
    private enum FailureCode
    {
        None, InvalidInput, TenantMismatch, RuntimeIdentityMismatch, OwnerNotFound, OwnerDisabled, OwnerNotMember,
        OwnerNoWorkAddress, DirectoryForbidden, DirectoryUnauthorized, DirectoryUnavailable, TimedOut
    }

    private sealed record DiagnosticResult
    {
        public DiagnosticStatus Status { get; init; } = DiagnosticStatus.ActionRequired;
        public FailureCode FailureCode { get; init; } = FailureCode.InvalidInput;
        public bool DirectoryEnabled { get; init; }
        public bool RuntimeIdentityVerified { get; init; }
        public bool OwnerEligible { get; init; }
        public bool SearchVerified { get; init; }
    }
}
