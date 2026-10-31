using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Jobs;
using DocIntel.Core.Harness;
using DocIntel.Core.Review;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class ChannelDeliveryTests
{
    [Fact]
    public async Task External_Send_Follows_Persisted_Sending_State_And_Current_Resource_Access()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Transport.Setup(t => t.SendAsync(fixture.Run, fixture.Run.ChannelDelivery!,
                It.IsAny<CancellationToken>()))
            .Callback(() => Assert.Equal(ChannelDeliveryStatus.Sending, fixture.Saved[^1]))
            .Returns(Task.CompletedTask);

        // Act
        await fixture.Processor.ProcessAsync(fixture.Lease.Object);

        // Assert
        Assert.Equal(new[] { ChannelDeliveryStatus.Sending, ChannelDeliveryStatus.Sent }, fixture.Saved);
        Assert.NotNull(fixture.Run.ChannelDelivery!.SentAtUtc);
    }

    [Fact]
    public async Task An_Interrupted_Or_Uncertain_Send_Is_Not_Automatically_Repeated()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Transport.Setup(t => t.SendAsync(It.IsAny<AskAiRun>(), It.IsAny<ChannelDelivery>(),
                It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("Unknown result."));

        // Act
        await fixture.Processor.ProcessAsync(fixture.Lease.Object);
        await fixture.Processor.ProcessAsync(fixture.Lease.Object);

        // Assert
        Assert.Equal(ChannelDeliveryStatus.OutcomeUnknown, fixture.Run.ChannelDelivery!.Status);
        fixture.Transport.Verify(t => t.SendAsync(It.IsAny<AskAiRun>(), It.IsAny<ChannelDelivery>(),
            It.IsAny<CancellationToken>()), Times.Once);
        var recovered = new Fixture();
        recovered.Run.ChannelDelivery!.Status = ChannelDeliveryStatus.Sending;
        await recovered.Processor.ProcessAsync(recovered.Lease.Object);
        Assert.Equal(ChannelDeliveryStatus.OutcomeUnknown, recovered.Run.ChannelDelivery.Status);
        recovered.Transport.Verify(t => t.SendAsync(It.IsAny<AskAiRun>(), It.IsAny<ChannelDelivery>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Access_Revocation_Blocks_Delivery_Without_Discarding_The_Model_Execution_Record()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Access.Setup(a => a.ResolveAsync(fixture.Run.Actor, "tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentAccessScope.Denied);

        // Act
        await fixture.Processor.ProcessAsync(fixture.Lease.Object);

        // Assert
        Assert.Equal(ChannelDeliveryStatus.Failed, fixture.Run.ChannelDelivery!.Status);
        Assert.Equal("DeliveryAccessRevoked", fixture.Run.ChannelDelivery.ErrorCode);
        Assert.Equal(JobStatus.Completed, fixture.Run.Status);
        fixture.Transport.Verify(t => t.SendAsync(It.IsAny<AskAiRun>(), It.IsAny<ChannelDelivery>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_Failed_PreSend_Fence_Does_Not_Contact_The_Channel()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Lease.Setup(l => l.SaveAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("Storage unavailable."));

        // Act / Assert
        await Assert.ThrowsAsync<IOException>(() => fixture.Processor.ProcessAsync(fixture.Lease.Object));
        fixture.Transport.Verify(t => t.SendAsync(It.IsAny<AskAiRun>(), It.IsAny<ChannelDelivery>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_Narrowed_Scope_Blocks_An_Answer_Even_Without_Removed_Documents_In_Its_Citations()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Access.Setup(a => a.ResolveAsync(fixture.Run.Actor, "tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentAccessScope(TenantRole.Reader, new HashSet<string> { "one-document" }));

        // Act
        await fixture.Processor.ProcessAsync(fixture.Lease.Object);

        // Assert
        Assert.Equal("DeliveryAccessRevoked", fixture.Run.ChannelDelivery!.ErrorCode);
        fixture.Transport.Verify(t => t.SendAsync(It.IsAny<AskAiRun>(), It.IsAny<ChannelDelivery>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_Disabled_Tenant_Cannot_Deliver_Previously_Completed_Answers()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Tenant.IsActive = false;

        // Act
        await fixture.Processor.ProcessAsync(fixture.Lease.Object);

        // Assert
        Assert.Equal("DeliveryTenantDisabled", fixture.Run.ChannelDelivery!.ErrorCode);
        fixture.Transport.Verify(t => t.SendAsync(It.IsAny<AskAiRun>(), It.IsAny<ChannelDelivery>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Missing_Or_Source_Mismatched_Review_Evidence_Blocks_Delayed_Delivery()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Run.Citations.Add(new AskAiCitation(
            1, "doc", "file.pdf", "file", 0, "Invoice", "1", "", "", "", "", "", "Corrected value",
            "/review", "/pdf", "HumanReviewedField", 1));
        fixture.Jobs.Setup(j => j.GetJobAsync("doc", It.IsAny<CancellationToken>(), "tenant"))
            .ReturnsAsync(new DocumentJob
            {
                JobId = "doc",
                TenantId = "tenant",
                TenantDisplayName = "Tenant",
                AppliedHarnessVersion = 1,
                FileName = "file.pdf",
                Status = JobStatus.Completed,
                ReviewId = Guid.NewGuid().ToString("N")
            });

        // Act
        await fixture.Processor.ProcessAsync(fixture.Lease.Object);

        // Assert
        Assert.Equal(ChannelDeliveryStatus.Failed, fixture.Run.ChannelDelivery!.Status);
        Assert.Equal("DeliveryEvidenceChanged", fixture.Run.ChannelDelivery.ErrorCode);
        fixture.Transport.Verify(t => t.SendAsync(It.IsAny<AskAiRun>(), It.IsAny<ChannelDelivery>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_Deleted_Referenced_Document_Blocks_Delivery_Even_If_The_Model_Did_Not_Cite_It()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Run.ReferencedJobIds = ["deleted-document"];
        fixture.Run.DocumentsEvaluated = 1;

        // Act
        await fixture.Processor.ProcessAsync(fixture.Lease.Object);

        // Assert
        Assert.Equal("DeliveryEvidenceChanged", fixture.Run.ChannelDelivery!.ErrorCode);
        fixture.Transport.Verify(t => t.SendAsync(It.IsAny<AskAiRun>(), It.IsAny<ChannelDelivery>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_Run_Creation_Cannot_Reset_Completed_Answers_Or_Delivery(bool file)
    {
        // Arrange
        var directory = Path.Combine("TestOutput", "DocIntel.ChannelRun.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            IAskAiRunStore store = file ? new FileAskAiRunStore(directory) : new InMemoryAskAiRunStore();
            var original = Run();
            original.Status = JobStatus.Completed;
            original.Answer = "Preserved result.";
            Assert.True(await store.TryCreateRunAsync(original));

            // Act
            var duplicate = await store.TryCreateRunAsync(Run());
            var recovered = await store.GetRunAsync("tenant", "one");

            // Assert
            Assert.False(duplicate);
            Assert.Equal("Preserved result.", recovered!.Answer);
            Assert.Equal(JobStatus.Completed, recovered.Status);
            Assert.Single(await store.ListUnfinishedRunsAsync(100));
            await using (var lease = await store.TryAcquireAsync("tenant", "one"))
            {
                Assert.False(await store.TryCreateRunAsync(Run()));
                lease!.Run.ChannelDelivery!.Status = ChannelDeliveryStatus.OutcomeUnknown;
                await lease.SaveAsync();
            }
            Assert.Empty(await store.ListUnfinishedRunsAsync(100));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static AskAiRun Run() => new()
    {
        TenantId = "tenant",
        RunId = "one",
        Question = "Question",
        Status = JobStatus.Completed,
        Actor = new ActorIdentity(Guid.NewGuid(), Guid.NewGuid()),
        Answer = "Answer",
        ChannelDelivery = new ChannelDelivery
        {
            Transport = "Teams",
            Profile = "configured",
            DestinationId = "personal-chat",
            AccessFingerprint = ChannelDelivery.GetAccessFingerprint(DocumentAccessScope.Unrestricted)
        }
    };

    private sealed class Fixture
    {
        public AskAiRun Run { get; } = ChannelDeliveryTests.Run();
        public Mock<IAskAiRunLease> Lease { get; } = new();
        public Mock<IChannelTransport> Transport { get; } = new();
        public Mock<ITenantAccessResolver> Access { get; } = new();
        public Mock<IJobStore> Jobs { get; } = new();
        public List<ChannelDeliveryStatus> Saved { get; } = [];
        public TenantHarness Tenant { get; } = new()
        {
            TenantId = "tenant",
            DisplayName = "Tenant",
            Agent = new() { TeamsEnabled = true, EmailEnabled = true }
        };
        public ChannelDeliveryProcessor Processor { get; }
        public Fixture()
        {
            Lease.SetupGet(l => l.Run).Returns(Run);
            Lease.Setup(l => l.SaveAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Saved.Add(Run.ChannelDelivery!.Status)).Returns(Task.CompletedTask);
            Transport.SetupGet(t => t.Name).Returns("Teams");
            Access.Setup(a => a.ResolveAsync(Run.Actor, "tenant", It.IsAny<CancellationToken>()))
                .ReturnsAsync(DocumentAccessScope.Unrestricted);
            var harness = new Mock<IHarnessStore>();
            harness.Setup(h => h.GetTenantAsync("tenant", It.IsAny<CancellationToken>())).ReturnsAsync(Tenant);
            Processor = new ChannelDeliveryProcessor([Transport.Object],
                NullLogger<ChannelDeliveryProcessor>.Instance, Access.Object, harness.Object,
                Jobs.Object, new ReviewService(new InMemoryReviewStore()));
        }
    }
}
