using System.Net;
using System.Text.Json;
using DocIntel.Api.Channels;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Harness;

namespace DocIntel.Api.Diagnostics;

/// <summary>Inspects email configuration and current linked-recipient eligibility without reading or sending mail.</summary>
public static class EmailChannelDiagnostic
{
    private const int MaximumAddressChecks = 20;

    /// <summary>Emits only fixed categories and counts; never starts workers, changes access or publishes credentials.</summary>
    public static async Task<int> ExecuteAsync(
        string tenantId, EmailChannelOptions options, IHarnessStore harness,
        IPersonalEmailStateStore? personalStore, IAssignedEmailAuthorization? authorization,
        IMailboxClient? mailbox, TextWriter output,
        CancellationToken cancellationToken = default)
    {
        if (!PersonalEmailState.ValidTenant(tenantId))
            throw new ArgumentException("A valid workspace ID is required.", nameof(tenantId));
        if (!options.Enabled || options.ApplicationTenantId != tenantId)
        {
            await WriteAsync(output, "OperatorPolicy", options.Enabled ? "NotApplicable" : "Disabled");
            return 0;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var stage = "OperatorPolicy";
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(options.AuthorizationMode) || !Enum.IsDefined(options.Provider))
                throw new InvalidDataException("The configured email policy is invalid.");
            await WriteAsync(output, stage, options.AuthorizationMode.ToString());
            stage = "WorkspaceEmail";
            var tenant = await harness.GetTenantAsync(tenantId, deadline.Token).WaitAsync(deadline.Token);
            var workspace = tenant is null ? "NotFound" : !tenant.IsActive ? "Inactive" :
                !tenant.Agent.EmailEnabled ? "Disabled" :
                !tenant.Capabilities.TryGetValue(FeatureHarness.AskAiKey, out var feature) || !feature.Enabled
                    ? "AskAiDisabled" : tenant.Agent.EmailConfirmationRequired ? "ConfirmationRequired" : "Enabled";
            await WriteAsync(output, stage, workspace);
            if (workspace is not ("Enabled" or "ConfirmationRequired")) return 0;
            if (options.Provider != EmailMailboxProvider.OutlookCom)
            {
                await WriteAsync(output, "StoredMailboxConnection", "ExchangeOnline");
                return 0;
            }

            stage = "StoredMailboxConnection";
            if (personalStore is null) throw new InvalidOperationException("Personal email storage is unavailable.");
            var state = await personalStore.GetAsync(tenantId, deadline.Token).WaitAsync(deadline.Token);
            state.Validate(tenantId);
            await WriteAsync(output, stage, state.Mailbox?.Status.ToString() ?? "NotConnected",
                createdAtUtc: state.Mailbox?.VerifiedAtUtc);
            if (state.Mailbox is not { Status: PersonalEmailConnectionStatus.Connected }) return 0;
            stage = "MailboxAuthorization";
            if (mailbox is not IConnectedMailboxClient connected)
                throw new InvalidOperationException("The connected mailbox adapter is unavailable.");
            await connected.GetConnectionProfileAsync(options.ConfiguredProfile, deadline.Token)
                .WaitAsync(deadline.Token);
            await WriteAsync(output, stage, "Authorized");

            stage = "LinkedPersonalRecipients";
            var addresses = state.Senders.Where(item => item.Status == PersonalEmailConnectionStatus.Connected)
                .SelectMany(item => item.Addresses).Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumAddressChecks + 1).ToArray();
            if (options.AuthorizationMode != EmailAuthorizationMode.ApprovedUsers ||
                tenant!.Agent.EmailConfirmationRequired)
            {
                await WriteAsync(output, stage, "AutomaticPolicyNotApplicable", inspections: 0);
                return 0;
            }
            if (addresses.Length == 0)
            {
                await WriteAsync(output, stage, "NotLinked", "SenderLinkMissing", inspections: 0, matches: 0);
                return 0;
            }
            if (authorization is null)
                throw new InvalidOperationException("Approved-recipient authorization is unavailable.");
            var checkedAddresses = 0;
            var eligible = 0;
            foreach (var address in addresses.Take(MaximumAddressChecks))
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (await authorization.ResolveAsync(tenantId, address, deadline.Token).WaitAsync(deadline.Token)
                    is not null) eligible++;
                checkedAddresses++;
            }
            var limited = addresses.Length > MaximumAddressChecks;
            await WriteAsync(output, stage, limited ? "Partial" : eligible > 0 ? "Eligible" : "Ineligible",
                limited ? "InspectionLimit" : eligible > 0 ? "None" : "NoApprovedRecipient",
                inspections: checkedAddresses, matches: eligible);
            return 0;
        }
        catch (Exception exception)
        {
            var code = exception switch
            {
                OperationCanceledException => "TimedOut",
                PersonalEmailConnectionRequiredException => "ReconnectionRequired",
                UnauthorizedAccessException => "AccessRevoked",
                HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "GraphForbidden",
                HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => "GraphUnauthorized",
                HttpRequestException => "GraphUnavailable",
                InvalidDataException => "InvalidState",
                _ => "DiagnosticFailed"
            };
            await WriteAsync(output, stage, "Failed", code);
            return 1;
        }
    }

    private static Task WriteAsync(TextWriter output, string stage, string status, string failureCode = "None",
        int? inspections = null, int? matches = null, DateTimeOffset? createdAtUtc = null) =>
        output.WriteLineAsync("DOCINTEL_DIAGNOSTIC " + JsonSerializer.Serialize(new
        {
            phase = "Email",
            stage,
            status,
            failureCode,
            inspections,
            matches,
            createdAtUtc
        }));
}
