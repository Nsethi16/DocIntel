using DocIntel.Api.Channels;
using DocIntel.Core.Channels;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Harness;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class MailboxWorkerTests
{
    [Fact]
    public async Task A_Restart_Uses_The_Saved_Delta_Checkpoint_And_Does_Not_Send_Historical_Confirmation_Bursts()
    {
        // Arrange
        var options = new EmailChannelOptions
        {
            Enabled = true,
            TenantId = Guid.NewGuid(),
            MailboxObjectId = Guid.NewGuid(),
            ApplicationTenantId = "tenant",
            MailboxAddress = "agent@example.com",
            PublicOrigin = "https://docintel.example"
        };
        var runs = new InMemoryAskAiRunStore();
        var harness = new Mock<IHarnessStore>();
        harness.Setup(h => h.GetTenantAsync("tenant", It.IsAny<CancellationToken>())).ReturnsAsync(
            new TenantHarness { TenantId = "tenant", DisplayName = "Tenant", Agent = new() { EmailEnabled = true } });
        var mailbox = new Mock<IMailboxClient>();
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mailbox.Setup(m => m.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => sent.TrySetResult()).Returns(Task.CompletedTask);
        mailbox.Setup(m => m.ReadChangesAsync(null, It.IsAny<CancellationToken>(), It.IsAny<DateTimeOffset?>()))
            .Returns<string?, CancellationToken, DateTimeOffset?>((_, _, baseline) =>
                Task.FromResult(new MailboxChanges(
                [
                    new MailboxQuestion("historical", "old@example.com", "Old", "Old question", baseline!.Value.AddDays(-1)),
                    new MailboxQuestion("new", "current@example.com", "New", "New question", baseline.Value.AddSeconds(1))
                ], null, "durable-delta-one")));
        var receiver = new EmailQuestionReceiver(options, runs, harness.Object, mailbox.Object,
            NullLogger<EmailQuestionReceiver>.Instance);
        var worker = new MailboxQuestionWorker(options, mailbox.Object, receiver, runs, harness.Object,
            NullLogger<MailboxQuestionWorker>.Instance);

        // Act
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!(await runs.ListRunsByTenantAsync("tenant", deadline.Token))
                .Any(run => run.MailboxContinuation == "durable-delta-one"))
                await Task.Delay(10, deadline.Token);
        }
        finally { await worker.StopAsync(CancellationToken.None); worker.Dispose(); }
        var checkpoint = Assert.Single(await runs.ListRunsByTenantAsync("tenant"), run => run.PendingEmail is null);
        var observed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        mailbox.Setup(m => m.ReadChangesAsync("durable-delta-one", It.IsAny<CancellationToken>(), It.IsAny<DateTimeOffset?>()))
            .Callback<string?, CancellationToken, DateTimeOffset?>((cursor, _, _) => observed.TrySetResult(cursor))
            .ReturnsAsync(new MailboxChanges([], null, "durable-delta-two"));
        using var restarted = new MailboxQuestionWorker(options, mailbox.Object, receiver, runs, harness.Object,
            NullLogger<MailboxQuestionWorker>.Instance);
        await restarted.StartAsync(CancellationToken.None);
        try { Assert.Equal("durable-delta-one", await observed.Task.WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { await restarted.StopAsync(CancellationToken.None); }

        // Assert
        Assert.Equal("durable-delta-one", checkpoint.MailboxContinuation);
        mailbox.Verify(m => m.SendAsync("old@example.com", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        mailbox.Verify(m => m.SendAsync("current@example.com", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
