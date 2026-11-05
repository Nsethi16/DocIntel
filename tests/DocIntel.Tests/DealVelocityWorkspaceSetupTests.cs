using System.Security.Cryptography;
using System.Text.Json;
using DocIntel.Api.Channels;
using DocIntel.Api.Diagnostics;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Storage;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class DealVelocityWorkspaceSetupTests
{
    [Fact]
    public async Task Corpus_Is_Deterministic_Readable_Bounded_And_Clearly_Fictional()
    {
        // Arrange
        var first = DealVelocityDemoCorpus.Build();
        var second = DealVelocityDemoCorpus.Build();
        var extractor = new PdfPigLayoutExtractor();

        // Act / Assert
        Assert.Equal(9, first.Count);
        Assert.True(first.Sum(document => document.Bytes.Length) < 1024 * 1024);
        for (var index = 0; index < first.Count; index++)
        {
            Assert.Equal(SHA256.HashData(first[index].Bytes), SHA256.HashData(second[index].Bytes));
            var pages = await extractor.ExtractPagesAsync(first[index].Bytes, new());
            Assert.Equal(first[index].Pages, pages.Count);
            Assert.All(pages, page => Assert.Contains("FICTIONAL", page.BodyText));
        }
        Assert.Equal(first[0].Bytes, Assert.Single(first, document => document.Key == "Q1041Copy").Bytes);
        var quote = await extractor.ExtractPagesAsync(first[0].Bytes, new());
        Assert.Contains("48,600.00 USD", quote[0].BodyText);
        var euro = await extractor.ExtractPagesAsync(
            Assert.Single(first, document => document.Key == "Q4101").Bytes, new());
        Assert.Contains("58,400.00 EUR", euro[0].BodyText);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Apply_Requires_Explicit_Confirmation_And_Live_Processing_Opt_In(bool confirmed, bool live)
    {
        // Arrange
        using var fixture = new Fixture();

        // Act
        var result = await fixture.ExecuteAsync(apply: true, confirmed: confirmed, live: live);

        // Assert
        Assert.Equal(2, result);
        Assert.Contains("InvalidSetupInput", fixture.Output.ToString());
        fixture.Runs.VerifyNoOtherCalls();
        fixture.Harness.VerifyNoOtherCalls();
        fixture.Artifacts.VerifyNoOtherCalls();
        fixture.Queue.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_Current_Workspace_Wide_Administrators_Can_Rename_Or_Submit(bool restricted)
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Access.Setup(service => service.ResolveAsync(fixture.Actor, "contoso-legal",
            It.IsAny<CancellationToken>())).ReturnsAsync(restricted
                ? new DocumentAccessScope(TenantRole.Admin, new HashSet<string> { "one" })
                : new DocumentAccessScope(TenantRole.Reader, null));

        // Act
        var result = await fixture.ExecuteAsync(apply: true);

        // Assert
        Assert.Equal(2, result);
        Assert.Contains("CurrentAdministratorRequired", fixture.Output.ToString());
        fixture.Harness.Verify(service => service.UpsertTenantAsync(
            It.IsAny<TenantHarness>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Artifacts.VerifyNoOtherCalls();
        fixture.Queue.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Apply_Renames_Only_The_Display_Label_And_Submits_Owned_Documents_Exactly_Once()
    {
        // Arrange
        using var fixture = new Fixture();
        var originalAgent = fixture.Tenant.Agent;
        var originalCapabilities = JsonSerializer.Serialize(fixture.Tenant.Capabilities);

        // Act
        Assert.Equal(0, await fixture.ExecuteAsync(apply: true));
        var first = await fixture.Jobs.ListJobsByTenantAsync("contoso-legal");
        Assert.Equal(0, await fixture.ExecuteAsync(apply: true));
        var second = await fixture.Jobs.ListJobsByTenantAsync("contoso-legal");

        // Assert
        Assert.Equal("Deal Velocity", fixture.Tenant.DisplayName);
        Assert.Equal("contoso-legal", fixture.Tenant.TenantId);
        Assert.Equal(18, fixture.Tenant.HarnessVersion);
        Assert.Equal(originalAgent, fixture.Tenant.Agent);
        Assert.Equal(originalCapabilities, JsonSerializer.Serialize(fixture.Tenant.Capabilities));
        Assert.Equal(9, first.Count);
        Assert.Equal(9, second.Count);
        Assert.Equal(9, fixture.Tickets.Count);
        Assert.All(second, job =>
        {
            Assert.Equal(fixture.Actor, job.SubmittedBy);
            Assert.Equal("Deal Velocity", job.TenantDisplayName);
            Assert.Equal(18, job.AppliedHarnessVersion);
            Assert.Equal("Deal Velocity", job.ProcessingHarness!.DisplayName);
            Assert.Equal(JobStatus.Queued, job.Status);
        });
        fixture.Harness.Verify(service => service.UpsertTenantAsync(
            It.IsAny<TenantHarness>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Inspect_Never_Renames_Or_Submits_And_Reports_Pending_Until_Real_Results_Exist()
    {
        // Arrange
        using var fixture = new Fixture();

        // Act
        var result = await fixture.ExecuteAsync(apply: false, confirmed: false, live: false);

        // Assert
        Assert.Equal(3, result);
        Assert.Contains("\"status\":\"Pending\"", fixture.Output.ToString());
        Assert.Empty(await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"));
        fixture.Harness.Verify(service => service.UpsertTenantAsync(
            It.IsAny<TenantHarness>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Artifacts.VerifyNoOtherCalls();
        fixture.Queue.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Completed_Inspection_Requires_Original_Integrity_Source_Text_And_Production_Provenance()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.ExecuteAsync(apply: true);
        foreach (var job in await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"))
        {
            job.Status = JobStatus.Completed;
            job.HasDevelopmentResults = false;
            job.Segments = [new() { StartPage = 1, EndPage = job.TotalPages, BodyText = "Original source text." }];
            await fixture.Jobs.UpsertJobAsync(job);
        }

        // Act
        var completed = await fixture.ExecuteAsync(apply: false);
        var corrupted = (await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"))[0];
        corrupted.HasDevelopmentResults = null;
        await fixture.Jobs.UpsertJobAsync(corrupted);
        var rejected = await fixture.ExecuteAsync(apply: false);

        // Assert
        Assert.Equal(0, completed);
        Assert.Equal(1, rejected);
        Assert.Contains("DocumentConflictOrIntegrityFailed", fixture.Output.ToString());
        Assert.Equal(9, fixture.Tickets.Count);
    }

    [Fact]
    public async Task A_Current_Workspace_Change_Aborts_Submission_Instead_Of_Overwriting_It()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Artifacts.Setup(service => service.SaveOriginalPdfAsync("contoso-legal",
                It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string id, byte[] bytes, CancellationToken _) =>
            {
                fixture.Originals[id] = bytes;
                fixture.Tenant.Agent = fixture.Tenant.Agent with { EmailEnabled = false };
                return "private-original";
            });

        // Act
        var result = await fixture.ExecuteAsync(apply: true);

        // Assert
        Assert.Equal(1, result);
        Assert.Contains("WorkspaceChanged", fixture.Output.ToString());
        Assert.Empty(fixture.Tickets);
        Assert.False(fixture.Tenant.Agent.EmailEnabled);
    }

    [Fact]
    public async Task Commercial_Apply_Cleans_Only_Known_Filenames_Without_Reprocessing_Or_Changing_Their_Content()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.ExecuteAsync(apply: true);
        var originals = await fixture.Jobs.ListJobsByTenantAsync("contoso-legal");
        foreach (var job in originals)
        {
            job.Status = JobStatus.Completed;
            job.HasDevelopmentResults = false;
            job.ExecutionAttempts = 1;
            job.Segments = [new() { StartPage = 1, EndPage = job.TotalPages, BodyText = "Preserved source." }];
        }
        var fingerprints = originals.ToDictionary(job => job.JobId, AskAiConversationContext.Fingerprint);
        var unrelated = new DocumentJob
        {
            JobId = new string('f', 32), TenantId = "contoso-legal", TenantDisplayName = "Deal Velocity",
            AppliedHarnessVersion = 1, FileName = "Demo-Customer-Owned-Document.pdf", Status = JobStatus.Completed
        };
        await fixture.Jobs.UpsertJobAsync(unrelated);

        // Act
        var first = await fixture.ExecuteAsync(apply: true, commercial: true);
        var second = await fixture.ExecuteAsync(apply: true, commercial: true);
        var current = await fixture.Jobs.ListJobsByTenantAsync("contoso-legal");

        // Assert
        Assert.Equal(0, first);
        Assert.Equal(0, second);
        Assert.Equal(24, current.Count);
        Assert.Equal(23, fixture.Tickets.Count);
        foreach (var original in originals)
        {
            var renamed = Assert.Single(current, job => job.JobId == original.JobId);
            Assert.DoesNotContain("Demo", renamed.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(fingerprints[original.JobId], AskAiConversationContext.Fingerprint(renamed));
            Assert.Equal(original.OriginalSha256, renamed.OriginalSha256);
            Assert.Equal(original.SubmittedBy, renamed.SubmittedBy);
            Assert.Equal(original.ExecutionAttempts, renamed.ExecutionAttempts);
            Assert.Equal(JobStatus.Completed, renamed.Status);
        }
        Assert.Equal(unrelated.FileName, Assert.Single(current, job => job.JobId == unrelated.JobId).FileName);
    }

    [Fact]
    public async Task Commercial_Rename_Rejects_A_Stale_Metadata_Write_And_Preserves_The_Current_Document()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.ExecuteAsync(apply: true);
        var original = (await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"))[0];
        original.Status = JobStatus.Completed;
        original.StoreVersion = 7;
        original.ETag = "specific-version";
        var fenced = new Mock<IJobStore>(MockBehavior.Strict);
        fenced.Setup(store => store.GetJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), "contoso-legal"))
            .ReturnsAsync((string id, CancellationToken _, string _) => id == original.JobId ? original : null);
        fenced.Setup(store => store.UpsertJobAsync(It.Is<DocumentJob>(job =>
            job.JobId == original.JobId && job.StoreVersion == 7 && job.ETag == "specific-version" &&
            !job.FileName.Contains("Demo", StringComparison.OrdinalIgnoreCase)), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Concurrent metadata change."));

        // Act
        var result = await fixture.ExecuteAsync(apply: true, commercial: true, jobs: fenced.Object);

        // Assert
        Assert.Equal(1, result);
        Assert.Contains("DocumentChanged", fixture.Output.ToString());
        Assert.Contains("Demo", original.FileName);
        Assert.Equal(9, fixture.Tickets.Count);
        fenced.Verify(store => store.UpsertJobAsync(It.IsAny<DocumentJob>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Commercial_Rename_Rechecks_Admin_Access_After_Reading_The_Original()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.ExecuteAsync(apply: true);
        foreach (var job in await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"))
            job.Status = JobStatus.Completed;
        fixture.Artifacts.Setup(store => store.GetOriginalPdfAsync("contoso-legal", It.IsAny<string>(),
            It.IsAny<CancellationToken>())).ReturnsAsync((string _, string id, CancellationToken _) =>
        {
            fixture.Access.Setup(service => service.ResolveAsync(fixture.Actor, "contoso-legal",
                It.IsAny<CancellationToken>())).ReturnsAsync(DocumentAccessScope.Denied);
            return fixture.Originals.GetValueOrDefault(id);
        });

        // Act
        var result = await fixture.ExecuteAsync(apply: true, commercial: true);

        // Assert
        Assert.Equal(2, result);
        Assert.Contains("CurrentAdministratorRequired", fixture.Output.ToString());
        Assert.All(await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"), job =>
            Assert.Contains("Demo", job.FileName));
        Assert.Equal(9, fixture.Tickets.Count);
    }

    [Fact]
    public async Task Commercial_Inspection_Does_Not_Call_Untranslated_Foreign_Documents_Ready()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.ExecuteAsync(apply: true, commercial: true);
        foreach (var job in await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"))
        {
            job.Status = JobStatus.Completed;
            job.HasDevelopmentResults = false;
            job.Segments = [new() { StartPage = 1, EndPage = job.TotalPages, BodyText = "Original source text." }];
        }

        // Act
        var result = await fixture.ExecuteAsync(apply: false, commercial: true);

        // Assert
        Assert.Equal(1, result);
        Assert.Contains("TranslationNotCompleted", fixture.Output.ToString());
        Assert.Contains("PortfolioFeaturesIncomplete", fixture.Output.ToString());
        Assert.Contains("OcrNotVerified", fixture.Output.ToString());
        Assert.Equal(14, fixture.Tickets.Count);
    }

    private sealed class Fixture : IDisposable
    {
        public ActorIdentity Actor { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        public string BasisId { get; } = "mail-" + new string('A', 64);
        public TenantHarness Tenant { get; set; } = new()
        {
            TenantId = "contoso-legal", DisplayName = "Contoso Legal & Licensing", HarnessVersion = 17,
            Agent = new AgentProfile { EmailEnabled = true, DisplayName = "DocIntel" }
        };
        public Mock<IHarnessStore> Harness { get; } = new(MockBehavior.Strict);
        public Mock<IAskAiRunStore> Runs { get; } = new(MockBehavior.Strict);
        public Mock<ITenantAccessResolver> Access { get; } = new(MockBehavior.Strict);
        public InMemoryJobStore Jobs { get; } = new();
        public Mock<IArtifactStore> Artifacts { get; } = new(MockBehavior.Strict);
        public Mock<IJobQueue> Queue { get; } = new(MockBehavior.Strict);
        public Dictionary<string, byte[]> Originals { get; } = new();
        public List<JobQueueTicket> Tickets { get; } = [];
        public StringWriter Output { get; } = new();

        public Fixture()
        {
            Runs.Setup(store => store.GetRunAsync("contoso-legal", BasisId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AskAiRun
                {
                    RunId = BasisId, TenantId = "contoso-legal", Question = "Saved workspace question.",
                    Actor = Actor, Status = JobStatus.Completed,
                    PendingEmail = new PendingEmailQuestion
                    {
                        Profile = "private", Sender = "fictional@example.test", MessageId = "private",
                        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
                    },
                    ReplyChannel = new AskAiReplyChannel { ChannelType = AskAiChannelType.Email }
                });
            Access.Setup(service => service.ResolveAsync(Actor, "contoso-legal", It.IsAny<CancellationToken>()))
                .ReturnsAsync(DocumentAccessScope.Unrestricted);
            Harness.Setup(store => store.GetTenantAsync("contoso-legal", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Copy(Tenant));
            Harness.Setup(store => store.UpsertTenantAsync(It.IsAny<TenantHarness>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((TenantHarness updated, CancellationToken _) =>
                {
                    if (updated.HarnessVersion != Tenant.HarnessVersion) throw new HarnessConflictException();
                    Tenant = Copy(updated);
                    Tenant.HarnessVersion++;
                    Tenant.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    return Copy(Tenant);
                });
            Artifacts.Setup(store => store.GetOriginalPdfAsync("contoso-legal", It.IsAny<string>(),
                It.IsAny<CancellationToken>())).ReturnsAsync((string _, string id, CancellationToken _) =>
                    Originals.GetValueOrDefault(id));
            Artifacts.Setup(store => store.SaveOriginalPdfAsync("contoso-legal", It.IsAny<string>(),
                It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string id, byte[] bytes, CancellationToken _) =>
                {
                    Originals[id] = bytes;
                    return "private-original";
                });
            Queue.Setup(queue => queue.TryEnqueueAsync(It.IsAny<JobQueueTicket>(), It.IsAny<CancellationToken>()))
                .Returns((JobQueueTicket ticket, CancellationToken _) =>
                {
                    Tickets.Add(ticket);
                    return ValueTask.FromResult(true);
                });
        }

        public Task<int> ExecuteAsync(bool apply, bool confirmed = true, bool live = true,
            bool commercial = false, IJobStore? jobs = null) =>
            DealVelocityWorkspaceSetup.ExecuteAsync("contoso-legal", BasisId, apply, confirmed, live,
                Harness.Object, Runs.Object, Access.Object, jobs ?? Jobs, Artifacts.Object, Queue.Object, Output,
                commercialPortfolio: commercial);
        public void Dispose() => Output.Dispose();
        private static TenantHarness Copy(TenantHarness tenant) =>
            JsonSerializer.Deserialize<TenantHarness>(JsonSerializer.SerializeToUtf8Bytes(tenant))!;
    }
}
