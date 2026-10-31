using System.Text.Json;
using DocIntel.Api.Identity;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class AskAiSavedOutputTests
{
    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Deleted)]
    public async Task Unavailable_Sources_Return_Not_Found_And_Block_Delivery_Without_Replaying(JobStatus sourceStatus)
    {
        // Arrange
        using var fixture = new Fixture(sqlite: false);
        await fixture.InitializeAsync();
        var source = (await fixture.Jobs.GetJobAsync("source", tenantId: "tenant"))!;
        source.Status = sourceStatus;
        await fixture.Jobs.UpsertJobAsync(source);
        var completed = (await fixture.Runs.GetRunAsync("tenant", "answer"))!;
        await using var owner = await fixture.Runs.TryAcquireAsync("tenant", "answer");

        // Act
        var status = await AskAiSavedOutputAuthorization.GetStatusAsync(
            completed, fixture.Jobs, DocumentAccessScope.Unrestricted);
        await fixture.Delivery.ProcessAsync(owner!);

        // Assert
        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Equal(ChannelDeliveryStatus.Failed, owner!.Run.ChannelDelivery!.Status);
        Assert.Equal("DeliveryEvidenceChanged", owner.Run.ChannelDelivery.ErrorCode);
        fixture.VerifyNoSend();
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Completed)]
    public async Task Revoked_Workspace_Access_Denies_Source_Free_Run_Output(JobStatus runStatus)
    {
        // Arrange
        var runs = new InMemoryAskAiRunStore();
        var run = new AskAiRun
        {
            RunId = "answer", TenantId = "tenant", Question = "Hello", Status = runStatus,
            Answer = runStatus == JobStatus.Completed ? "Hello!" : null
        };
        await runs.TryCreateRunAsync(run);
        var snapshot = (await runs.GetRunAsync("tenant", "answer"))!;

        // Act
        var status = await AskAiSavedOutputAuthorization.GetStatusAsync(
            snapshot, new InMemoryJobStore(), DocumentAccessScope.Denied);

        // Assert
        Assert.Equal(StatusCodes.Status404NotFound, status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Polling_Rejects_Changed_Completed_Original_Sources_Without_Returning_Old_Output(bool sqlite)
    {
        // Arrange
        using var fixture = new Fixture(sqlite);
        await fixture.InitializeAsync();
        var completed = (await fixture.Runs.GetRunAsync("tenant", "answer"))!;
        Assert.Equal(0, await AskAiSavedOutputAuthorization.GetStatusAsync(
            completed, fixture.Jobs, DocumentAccessScope.Unrestricted));
        await fixture.ChangeSourceAsync();
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = body;

        // Act
        var status = await AskAiSavedOutputAuthorization.GetStatusAsync(
            completed, fixture.Jobs, DocumentAccessScope.Unrestricted);
        await AskAiSavedOutputAuthorization.Denied(status).ExecuteAsync(context);
        using var response = JsonDocument.Parse(body.ToArray());

        // Assert
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Equal("SourceChanged", response.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(AskAiFailureDiagnostics.GetMessage("SourceChanged"),
            response.RootElement.GetProperty("error").GetString());
        Assert.Equal(2, response.RootElement.EnumerateObject().Count());
        Assert.DoesNotContain(fixture.Run.Answer!, response.RootElement.GetRawText());
        Assert.DoesNotContain(fixture.Source.Segments[0].BodyText, response.RootElement.GetRawText());
        Assert.DoesNotContain("private analytical output", response.RootElement.GetRawText());
        Assert.False(response.RootElement.TryGetProperty("citations", out _));
        Assert.False(response.RootElement.TryGetProperty("analyses", out _));
        var persisted = (await fixture.Runs.GetRunAsync("tenant", "answer"))!;
        Assert.Equal(JobStatus.Completed, persisted.Status);
        Assert.Equal(fixture.Run.Answer, persisted.Answer);
        Assert.Equal(fixture.Run.SourceVersions["source"], persisted.SourceVersions["source"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delayed_Delivery_Rejects_Reprocessed_Completed_Sources_Without_Sending_Or_Replaying(bool sqlite)
    {
        // Arrange
        using var fixture = new Fixture(sqlite);
        await fixture.InitializeAsync();
        await fixture.ChangeSourceAsync();
        await using var owner = await fixture.Runs.TryAcquireAsync("tenant", "answer");

        // Act
        await fixture.Delivery.ProcessAsync(owner!);
        await fixture.Delivery.ProcessAsync(owner!);
        var persisted = (await fixture.Runs.GetRunAsync("tenant", "answer"))!;

        // Assert
        Assert.Equal(JobStatus.Completed, persisted.Status);
        Assert.Equal(fixture.Run.Answer, persisted.Answer);
        Assert.Equal(fixture.Run.SourceVersions["source"], persisted.SourceVersions["source"]);
        Assert.Equal("OriginalSourceText", Assert.Single(persisted.Citations).EvidenceKind);
        Assert.Equal(ChannelDeliveryStatus.Failed, persisted.ChannelDelivery!.Status);
        Assert.Equal("DeliveryEvidenceChanged", persisted.ChannelDelivery.ErrorCode);
        Assert.Null(persisted.ChannelDelivery.AttemptedAtUtc);
        Assert.Empty(await fixture.Runs.ListUnfinishedRunsAsync(100));
        fixture.VerifyNoSend();
    }

    [Fact]
    public async Task A_Source_Changed_At_The_Persisted_Send_Fence_Is_Checked_Again_Before_Transport_Contact()
    {
        // Arrange
        using var fixture = new Fixture(sqlite: true);
        await fixture.InitializeAsync();
        var owner = (await fixture.Runs.TryAcquireAsync("tenant", "answer"))!;
        await using var boundary = new SourceChangingLease(owner, fixture.ChangeSourceAsync);

        // Act
        await fixture.Delivery.ProcessAsync(boundary);
        var persisted = (await fixture.Runs.GetRunAsync("tenant", "answer"))!;

        // Assert
        Assert.True(boundary.Changed);
        Assert.Equal(ChannelDeliveryStatus.Failed, persisted.ChannelDelivery!.Status);
        Assert.Equal("DeliveryEvidenceChanged", persisted.ChannelDelivery.ErrorCode);
        Assert.NotNull(persisted.ChannelDelivery.AttemptedAtUtc);
        Assert.Null(persisted.ChannelDelivery.SentAtUtc);
        Assert.Equal(JobStatus.Completed, persisted.Status);
        fixture.VerifyNoSend();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unchanged_Content_Remains_Readable_After_Storage_Version_Updates(bool sqlite)
    {
        // Arrange
        using var fixture = new Fixture(sqlite);
        await fixture.InitializeAsync();
        var source = (await fixture.Jobs.GetJobAsync("source", tenantId: "tenant"))!;
        await fixture.Jobs.UpsertJobAsync(source);
        var completed = (await fixture.Runs.GetRunAsync("tenant", "answer"))!;
        await using var owner = await fixture.Runs.TryAcquireAsync("tenant", "answer");

        // Act
        var status = await AskAiSavedOutputAuthorization.GetStatusAsync(
            completed, fixture.Jobs, DocumentAccessScope.Unrestricted);
        await fixture.Delivery.ProcessAsync(owner!);
        await fixture.Delivery.ProcessAsync(owner!);

        // Assert
        Assert.Equal(0, status);
        Assert.Equal(ChannelDeliveryStatus.Sent,
            (await fixture.Runs.GetRunAsync("tenant", "answer"))!.ChannelDelivery!.Status);
        fixture.Transport.Verify(transport => transport.SendAsync(
            It.IsAny<AskAiRun>(), It.IsAny<ChannelDelivery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Source_Version_Only_Documents_Cannot_Bypass_Current_Access_Or_Source_Checks()
    {
        // Arrange
        using var fixture = new Fixture(sqlite: true);
        await fixture.InitializeAsync();
        fixture.Run.Citations.Clear();
        fixture.Run.ReferencedJobIds.Clear();
        await fixture.Runs.UpsertRunAsync(fixture.Run);
        var completed = (await fixture.Runs.GetRunAsync("tenant", "answer"))!;
        var restricted = new DocumentAccessScope(TenantRole.Reader, new HashSet<string> { "other-document" });

        // Act
        var denied = await AskAiSavedOutputAuthorization.GetStatusAsync(completed, fixture.Jobs, restricted);
        await fixture.ChangeSourceAsync();
        var stale = await AskAiSavedOutputAuthorization.GetStatusAsync(
            completed, fixture.Jobs, DocumentAccessScope.Unrestricted);
        await using var owner = await fixture.Runs.TryAcquireAsync("tenant", "answer");
        await fixture.Delivery.ProcessAsync(owner!);

        // Assert
        Assert.Equal(StatusCodes.Status404NotFound, denied);
        Assert.Equal(StatusCodes.Status409Conflict, stale);
        Assert.Equal("DeliveryEvidenceChanged", owner!.Run.ChannelDelivery!.ErrorCode);
        fixture.VerifyNoSend();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_Cited_Or_Analytical_Output_Without_Source_Versions_Is_Not_Released(bool analysisOnly)
    {
        // Arrange
        using var fixture = new Fixture(sqlite: true);
        await fixture.InitializeAsync();
        fixture.Run.SourceVersions.Clear();
        if (analysisOnly) fixture.Run.Citations.Clear();
        else fixture.Run.Analyses.Clear();
        await fixture.Runs.UpsertRunAsync(fixture.Run);
        var completed = (await fixture.Runs.GetRunAsync("tenant", "answer"))!;
        await using var owner = await fixture.Runs.TryAcquireAsync("tenant", "answer");

        // Act
        var status = await AskAiSavedOutputAuthorization.GetStatusAsync(
            completed, fixture.Jobs, DocumentAccessScope.Unrestricted);
        await fixture.Delivery.ProcessAsync(owner!);

        // Assert
        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal("DeliveryEvidenceChanged", owner!.Run.ChannelDelivery!.ErrorCode);
        fixture.VerifyNoSend();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Historical_Search_Only_Clarifications_Do_Not_Require_Factual_Source_Versions(bool references)
    {
        // Arrange
        using var fixture = new Fixture(sqlite: true);
        await fixture.InitializeAsync();
        fixture.Run.Answer = "Which reporting period would you like?";
        fixture.Run.EmailHtmlBody = "<p>Which reporting period would you like?</p>";
        fixture.Run.TeamsAdaptiveCardJson = """{"text":"Which reporting period would you like?"}""";
        fixture.Run.Citations.Clear();
        fixture.Run.Analyses.Clear();
        fixture.Run.SourceVersions.Clear();
        fixture.Run.DocumentsEvaluated = 3;
        if (!references) fixture.Run.ReferencedJobIds.Clear();
        await fixture.Runs.UpsertRunAsync(fixture.Run);
        var completed = (await fixture.Runs.GetRunAsync("tenant", "answer"))!;
        await using var owner = await fixture.Runs.TryAcquireAsync("tenant", "answer");

        // Act
        var status = await AskAiSavedOutputAuthorization.GetStatusAsync(
            completed, fixture.Jobs, DocumentAccessScope.Unrestricted);
        await fixture.Delivery.ProcessAsync(owner!);

        // Assert
        Assert.Equal(0, status);
        Assert.Equal(ChannelDeliveryStatus.Sent, owner!.Run.ChannelDelivery!.Status);
        fixture.Transport.Verify(transport => transport.SendAsync(
            It.IsAny<AskAiRun>(), It.IsAny<ChannelDelivery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_Completed_Document_In_Another_Tenant_Cannot_Validate_A_Saved_Answer()
    {
        // Arrange
        using var fixture = new Fixture(sqlite: true);
        await fixture.InitializeAsync();
        var run = new AskAiRun
        {
            RunId = "other-answer", TenantId = "other-tenant", Question = "Question",
            Status = JobStatus.Completed, Answer = fixture.Run.Answer,
            SourceVersions = new(fixture.Run.SourceVersions)
        };

        // Act
        var status = await AskAiSavedOutputAuthorization.GetStatusAsync(
            run, fixture.Jobs, DocumentAccessScope.Unrestricted);

        // Assert
        Assert.Equal(StatusCodes.Status409Conflict, status);
    }

    private sealed class SourceChangingLease(
        IAskAiRunLease owner, Func<Task> changeSource) : IAskAiRunLease
    {
        public AskAiRun Run => owner.Run;
        public CancellationToken OwnershipLostToken => owner.OwnershipLostToken;
        public bool Changed { get; private set; }

        public async Task SaveAsync(CancellationToken cancellationToken = default)
        {
            await owner.SaveAsync(cancellationToken);
            if (!Changed && Run.ChannelDelivery!.Status == ChannelDeliveryStatus.Sending)
            {
                Changed = true;
                await changeSource();
            }
        }

        public ValueTask DisposeAsync() => owner.DisposeAsync();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            "TestOutput", "DocIntel.SavedOutput.Tests", Guid.NewGuid().ToString("N"));
        public IJobStore Jobs { get; }
        public InMemoryAskAiRunStore Runs { get; } = new();
        public DocumentJob Source { get; } = new()
        {
            JobId = "source", TenantId = "tenant", TenantDisplayName = "Tenant", AppliedHarnessVersion = 1,
            FileName = "invoice.pdf", Status = JobStatus.Completed, OriginalSha256 = new string('A', 64),
            Segments = [new() { SegmentIndex = 0, StartPage = 1, EndPage = 1, BodyText = "Private invoice 100 USD" }]
        };
        public AskAiRun Run { get; } = new()
        {
            RunId = "answer", TenantId = "tenant", Question = "What is the invoice amount?",
            Actor = new(Guid.NewGuid(), Guid.NewGuid()), Status = JobStatus.Completed,
            Answer = "The old invoice is 100 USD [1].", DocumentsEvaluated = 1, ReferencedJobIds = ["source"],
            EmailHtmlBody = "<p>The old invoice is 100 USD [1].</p>",
            TeamsAdaptiveCardJson = """{"text":"The old invoice is 100 USD [1]."}""",
            Analyses = [new() { AnalysisId = "a1", Title = "private analytical output" }],
            Citations = [new(1, "source", "invoice.pdf", "invoice", 0, "Invoice", "1",
                "", "", "", "USD", "", "Private invoice 100 USD", "/review", "/pdf", "OriginalSourceText")],
            ChannelDelivery = new()
            {
                Transport = "Teams", Profile = "configured", DestinationId = "personal-chat",
                AccessFingerprint = ChannelDelivery.GetAccessFingerprint(DocumentAccessScope.Unrestricted)
            }
        };
        public Mock<IChannelTransport> Transport { get; } = new();
        public ChannelDeliveryProcessor Delivery { get; }

        public Fixture(bool sqlite)
        {
            Jobs = sqlite ? new SqliteJobStore(Path.Combine(_directory, "jobs.db")) : new InMemoryJobStore();
            var harness = new Mock<IHarnessStore>();
            harness.Setup(store => store.GetTenantAsync("tenant", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TenantHarness
                {
                    TenantId = "tenant", DisplayName = "Tenant", Agent = new() { TeamsEnabled = true }
                });
            var access = new Mock<ITenantAccessResolver>();
            access.Setup(resolver => resolver.ResolveAsync(Run.Actor, "tenant", It.IsAny<CancellationToken>()))
                .ReturnsAsync(DocumentAccessScope.Unrestricted);
            Transport.SetupGet(transport => transport.Name).Returns("Teams");
            Transport.Setup(transport => transport.SendAsync(
                It.IsAny<AskAiRun>(), It.IsAny<ChannelDelivery>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Delivery = new([Transport.Object], NullLogger<ChannelDeliveryProcessor>.Instance,
                access.Object, harness.Object, Jobs);
        }

        public async Task InitializeAsync()
        {
            await Jobs.UpsertJobAsync(Source);
            Run.SourceVersions["source"] = AskAiConversationContext.Fingerprint(Source);
            Assert.True(await Runs.TryCreateRunAsync(Run));
        }

        public async Task ChangeSourceAsync()
        {
            var changed = (await Jobs.GetJobAsync("source", tenantId: "tenant"))!;
            Assert.Equal(JobStatus.Completed, changed.Status);
            changed.Segments[0].BodyText = "New invoice 200 USD";
            changed.Segments[0].StartPage = 2;
            changed.Segments[0].EndPage = 2;
            await Jobs.UpsertJobAsync(changed);
            Assert.Equal(JobStatus.Completed, (await Jobs.GetJobAsync("source", tenantId: "tenant"))!.Status);
        }

        public void VerifyNoSend() => Transport.Verify(transport => transport.SendAsync(
            It.IsAny<AskAiRun>(), It.IsAny<ChannelDelivery>(), It.IsAny<CancellationToken>()), Times.Never);

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
