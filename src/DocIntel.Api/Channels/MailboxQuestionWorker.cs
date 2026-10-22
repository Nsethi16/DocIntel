using DocIntel.Core.Channels;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocIntel.Api.Channels;

/// <summary>Uses a shared leased delta checkpoint; starts from new mail only, never an existing mailbox-history blast.</summary>
public sealed class MailboxQuestionWorker(
    EmailChannelOptions options, IMailboxClient mailbox, EmailQuestionReceiver receiver,
    IAskAiRunStore runs, IHarnessStore harness, ILogger<MailboxQuestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cursorId = "mailbox-checkpoint-" + options.Profile;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds));
        do
        {
            try
            {
                var tenant = await harness.GetTenantAsync(options.ApplicationTenantId, stoppingToken);
                if (tenant is not { IsActive: true } || !tenant.Agent.EmailEnabled ||
                    !tenant.Capabilities.TryGetValue(FeatureHarness.AskAiKey, out var feature) || !feature.Enabled) continue;
                await runs.TryCreateRunAsync(new AskAiRun
                {
                    RunId = cursorId,
                    TenantId = options.ApplicationTenantId,
                    Question = "Server mailbox synchronization checkpoint",
                    Status = JobStatus.Completed
                }, stoppingToken);
                await using var lease = await runs.TryAcquireAsync(options.ApplicationTenantId, cursorId, stoppingToken);
                if (lease is null) continue;
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.OwnershipLostToken);
                poll.CancelAfter(TimeSpan.FromSeconds(90));
                for (var page = 0; page < 4; page++)
                {
                    var changes = await mailbox.ReadChangesAsync(lease.Run.MailboxContinuation, poll.Token, lease.Run.CreatedAtUtc);
                    foreach (var message in changes.Messages)
                        if (message.ReceivedAtUtc >= lease.Run.CreatedAtUtc)
                            await receiver.ReceiveAsync(message, poll.Token);
                    lease.Run.MailboxContinuation = changes.NextLink ?? changes.DeltaLink;
                    await lease.SaveAsync(poll.Token);
                    if (changes.NextLink is null) break;
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError("Mailbox synchronization could not complete: {ErrorType}; durable cursor retained.",
                    exception.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
