using System.Security.Cryptography;
using System.Text.Json;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Jobs;

namespace DocIntel.Core.Channels;

/// <summary>Private service checkpoint for a recipient's bounded automatic email admission window.</summary>
public sealed record EmailAdmissionWindow(
    ActorIdentity? Recipient, DateTimeOffset StartedAtUtc, List<string> RunIds);

internal static class AssignedEmailAdmission
{
    internal static async Task RequireRecipientAsync(
        IAssignedEmailAuthorization? authorization, AskAiRun run, CancellationToken cancellationToken)
    {
        if (authorization is null || run.Actor is null ||
            run.PendingEmail is not { AssignedAtUtc: not null } pending ||
            pending.AuthorizationMode != EmailAuthorizationMode.ApprovedUsers ||
            pending.ConfirmedAtUtc is not null || pending.AuthenticatedAtUtc is not null ||
            !EmailChannelOptions.IsAddress(pending.Sender))
            throw new UnauthorizedAccessException("Approved-recipient email authorization is unavailable.");
        var current = await authorization.ResolveAsync(run.TenantId, pending.Sender, cancellationToken);
        if (current is null || current.Actor != run.Actor ||
            !String.Equals(current.Address, pending.Sender, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The email recipient no longer has this workspace assignment.");
    }

    internal static async Task<bool> ReserveAsync(
        IAskAiRunStore runs, EmailChannelOptions options, ActorIdentity recipient, string runId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var started = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        await using var global = await OpenAsync(runs, options, null, started, cancellationToken);
        await using var personal = await OpenAsync(runs, options, recipient, started, cancellationToken);
        var workspaceIds = global.Run.EmailAdmission!.RunIds;
        var recipientIds = personal.Run.EmailAdmission!.RunIds;
        var workspaceReserved = workspaceIds.Contains(runId, StringComparer.Ordinal);
        var recipientReserved = recipientIds.Contains(runId, StringComparer.Ordinal);
        if (!workspaceReserved && workspaceIds.Count >= options.AutomaticQuestionsPerWorkspaceHour ||
            !recipientReserved && recipientIds.Count >= options.AutomaticQuestionsPerSenderHour) return false;
        if (!workspaceReserved)
        {
            workspaceIds.Add(runId);
            await global.SaveAsync(cancellationToken);
        }
        if (!recipientReserved)
        {
            recipientIds.Add(runId);
            await personal.SaveAsync(cancellationToken);
        }
        return true;
    }

    private static async Task<IAskAiRunLease> OpenAsync(
        IAskAiRunStore runs, EmailChannelOptions options, ActorIdentity? recipient, DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        var key = "mail-budget-" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { options.ApplicationTenantId, recipient })));
        await runs.TryCreateRunAsync(new AskAiRun
        {
            RunId = key,
            TenantId = options.ApplicationTenantId,
            Question = "Private automatic email admission checkpoint.",
            Status = JobStatus.Completed,
            CreatedAtUtc = started,
            CompletedAtUtc = started,
            EmailAdmission = new EmailAdmissionWindow(recipient, started, [])
        }, cancellationToken);
        var lease = await runs.TryAcquireAsync(options.ApplicationTenantId, key, cancellationToken)
            ?? throw new InvalidOperationException("Automatic email admission is busy; retain the mailbox checkpoint.");
        try
        {
            var window = lease.Run.EmailAdmission;
            if (lease.Run.Actor is not null || lease.Run.Status != JobStatus.Completed || window is null ||
                window.Recipient != recipient || window.StartedAtUtc > started ||
                window.RunIds is null || window.RunIds.Count > 2_000 ||
                window.RunIds.Distinct(StringComparer.Ordinal).Count() != window.RunIds.Count)
                throw new InvalidDataException("The automatic email admission checkpoint is invalid.");
            if (window.StartedAtUtc < started)
                lease.Run.EmailAdmission = new EmailAdmissionWindow(recipient, started, []);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }
}
