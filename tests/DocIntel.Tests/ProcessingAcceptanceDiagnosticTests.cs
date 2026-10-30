using System.Security.Cryptography;
using System.Text.Json;
using DocIntel.Api.Diagnostics;
using DocIntel.Core;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.DocumentTranslation;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using DocIntel.Core.Storage;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class ProcessingAcceptanceDiagnosticTests
{
    private readonly byte[] _original =
        PdfPigLayoutExtractor.CreateSamplePdf([("Accepted private source", "Fixture")]);

    [Fact]
    public async Task All_Acceptance_Modes_Require_Live_Opt_In_Before_Using_Any_Service()
    {
        // Arrange
        var fixture = new Fixture();
        var harnesses = new Mock<IHarnessStore>(MockBehavior.Strict);
        var models = new Mock<IModelCatalog>(MockBehavior.Strict);
        var translation = new Mock<IDocumentTranslationService>(MockBehavior.Strict);
        var language = new Mock<IDocumentLanguageDetector>(MockBehavior.Strict);
        using var output = new StringWriter();

        // Act
        var reprocess = await fixture.Run(Guid.NewGuid().ToString("N"), false, output);
        var partial = await ProcessingAcceptanceDiagnostic.PartialAsync(
            "workspace", false, harnesses.Object, models.Object, output);
        var batch = await ProcessingAcceptanceDiagnostic.TranslationAsync(
            "workspace", false, translation.Object, language.Object, new(), output);

        // Assert
        Assert.Equal(2, reprocess);
        Assert.Equal(2, partial);
        Assert.Equal(2, batch);
        fixture.Jobs.VerifyNoOtherCalls();
        fixture.Queue.VerifyNoOtherCalls();
        fixture.Access.VerifyNoOtherCalls();
        fixture.Artifacts.VerifyNoOtherCalls();
        harnesses.VerifyNoOtherCalls();
        models.VerifyNoOtherCalls();
        translation.VerifyNoOtherCalls();
        language.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Simulated_Model_Results_Cannot_Pass_A_Live_Processing_Acceptance_Check()
    {
        // Arrange
        var harnesses = new Mock<IHarnessStore>();
        harnesses.Setup(store => store.GetTenantAsync("workspace", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantHarness { TenantId = "workspace", DisplayName = "Synthetic workspace" });
        var models = new ModelCatalog(allowDevelopmentSimulation: true);
        using var output = new StringWriter();

        // Act
        var result = await ProcessingAcceptanceDiagnostic.PartialAsync(
            "workspace", true, harnesses.Object, models, output);

        // Assert
        Assert.Equal(1, result);
        Assert.Contains("SimulationNotPermitted", output.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Reprocessing_Refuses_Ineligible_Stored_Sources_Without_Writing(int condition)
    {
        // Arrange
        var source = Source(condition != 0);
        if (condition == 1) source.OriginalSha256 = null;
        if (condition == 2) source.Status = JobStatus.Completed;
        if (condition == 3) source.ExecutionAttempts = 3;
        var fixture = new Fixture();
        fixture.Jobs.Setup(store => store.GetJobAsync(source.JobId, It.IsAny<CancellationToken>(), "workspace"))
            .ReturnsAsync(source);
        using var output = new StringWriter();

        // Act
        var result = await fixture.Run(source.JobId, true, output);

        // Assert
        Assert.Equal(2, result);
        Assert.Contains("RetryNotEligible", output.ToString());
        fixture.Access.VerifyNoOtherCalls();
        fixture.Artifacts.VerifyNoOtherCalls();
        fixture.Queue.VerifyNoOtherCalls();
        fixture.Jobs.Verify(store => store.UpsertJobAsync(
            It.IsAny<DocumentJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_Revoked_Submitter_Cannot_Read_Source_Or_Requeue_A_Failed_Document()
    {
        // Arrange
        var source = Source();
        var fixture = new Fixture();
        fixture.Jobs.Setup(store => store.GetJobAsync(source.JobId, It.IsAny<CancellationToken>(), "workspace"))
            .ReturnsAsync(source);
        fixture.Access.Setup(access => access.ResolveAsync(source.SubmittedBy, "workspace", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentAccessScope.Denied);
        using var output = new StringWriter();

        // Act
        var result = await fixture.Run(source.JobId, true, output);

        // Assert
        Assert.Equal(2, result);
        Assert.Contains("AccessRevoked", output.ToString());
        fixture.Artifacts.VerifyNoOtherCalls();
        fixture.Queue.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_Source_Hash_Mismatch_Prevents_Any_Retry_Or_Source_Replacement()
    {
        // Arrange
        var source = Source();
        var fixture = Accepted(source, PdfPigLayoutExtractor.CreateSamplePdf([("Different source", "Fixture")]));
        var before = JsonSerializer.Serialize(source);
        using var output = new StringWriter();

        // Act
        var result = await fixture.Run(source.JobId, true, output);

        // Assert
        Assert.Equal(1, result);
        Assert.Equal(before, JsonSerializer.Serialize(source));
        Assert.DoesNotContain("private", output.ToString());
        fixture.Queue.VerifyNoOtherCalls();
        fixture.Jobs.Verify(store => store.UpsertJobAsync(
            It.IsAny<DocumentJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Retry_Queues_Only_The_Accepted_Source_And_Reports_The_Normal_Workers_Results()
    {
        // Arrange
        var source = Source();
        var before = JsonSerializer.Serialize(source);
        var completed = JsonSerializer.Deserialize<DocumentJob>(before)!;
        completed.Status = JobStatus.Completed;
        completed.ExecutionAttempts++;
        completed.ExecutionTrace = [new("TranslateDocumentsExecutor", true, false, null, 1, "private-note")];
        var fixture = Accepted(source, Original());
        fixture.Jobs.SetupSequence(store => store.GetJobAsync(
                source.JobId, It.IsAny<CancellationToken>(), "workspace"))
            .ReturnsAsync(source).ReturnsAsync(completed);
        DocumentJob? queued = null;
        fixture.Jobs.Setup(store => store.UpsertJobAsync(It.IsAny<DocumentJob>(), It.IsAny<CancellationToken>()))
            .Callback((DocumentJob job, CancellationToken _) => queued = job).Returns(Task.CompletedTask);
        fixture.Queue.Setup(queue => queue.TryEnqueueAsync(
                new JobQueueTicket(source.JobId, "workspace"), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(true));
        using var output = new StringWriter();

        // Act
        var result = await fixture.Run(source.JobId, true, output);

        // Assert
        Assert.Equal(0, result);
        Assert.Equal(before, JsonSerializer.Serialize(source));
        Assert.NotNull(queued);
        Assert.Equal(JobStatus.Queued, queued.Status);
        Assert.Equal(source.OriginalSha256, queued.OriginalSha256);
        Assert.Equal(source.SubmittedBy, queued.SubmittedBy);
        Assert.Equal(source.StoreVersion, queued.StoreVersion);
        Assert.Equal(source.ETag, queued.ETag);
        Assert.Equal(JsonSerializer.Serialize(source.ProcessingHarness), JsonSerializer.Serialize(queued.ProcessingHarness));
        Assert.Contains("\"status\":\"Completed\"", output.ToString());
        Assert.Contains("\"hasWarnings\":true", output.ToString());
        Assert.DoesNotContain("private", output.ToString());
        fixture.Artifacts.Verify(store => store.GetOriginalPdfAsync(
            "workspace", source.JobId, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Artifacts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_Fenced_Retry_Save_Does_Not_Mutate_The_Loaded_Failed_Record_Or_Queue_Work()
    {
        // Arrange
        var source = Source();
        var before = JsonSerializer.Serialize(source);
        var fixture = Accepted(source, Original());
        fixture.Jobs.Setup(store => store.UpsertJobAsync(It.IsAny<DocumentJob>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Concurrent record version."));
        using var output = new StringWriter();

        // Act
        var result = await fixture.Run(source.JobId, true, output);

        // Assert
        Assert.Equal(1, result);
        Assert.Equal(before, JsonSerializer.Serialize(source));
        fixture.Queue.VerifyNoOtherCalls();
    }

    private byte[] Original() => _original;

    private DocumentJob Source(bool accepted = true) => new()
    {
        JobId = Guid.NewGuid().ToString("N"), TenantId = "workspace", TenantDisplayName = "private-workspace",
        AppliedHarnessVersion = 3, FileName = "private-source.pdf", Status = JobStatus.Failed,
        SubmittedBy = new(Guid.NewGuid(), Guid.NewGuid()), OriginalSha256 = Convert.ToHexString(SHA256.HashData(Original())),
        ProcessingHarness = accepted
            ? new() { TenantId = "workspace", DisplayName = "private-workspace", HarnessVersion = 3 } : null,
        ExecutionAttempts = 1, StoreVersion = 7, ETag = "fixture-version"
    };

    private static Fixture Accepted(DocumentJob source, byte[] original)
    {
        var fixture = new Fixture();
        fixture.Jobs.Setup(store => store.GetJobAsync(source.JobId, It.IsAny<CancellationToken>(), "workspace"))
            .ReturnsAsync(source);
        fixture.Access.Setup(access => access.ResolveAsync(source.SubmittedBy, "workspace", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentAccessScope.Unrestricted);
        fixture.Artifacts.Setup(store => store.GetOriginalPdfAsync(
            "workspace", source.JobId, It.IsAny<CancellationToken>())).ReturnsAsync(original);
        return fixture;
    }

    private sealed class Fixture
    {
        public Mock<IJobStore> Jobs { get; } = new(MockBehavior.Strict);
        public Mock<IJobQueue> Queue { get; } = new(MockBehavior.Strict);
        public Mock<ITenantAccessResolver> Access { get; } = new(MockBehavior.Strict);
        public Mock<IArtifactStore> Artifacts { get; } = new(MockBehavior.Strict);

        public Task<int> Run(string jobId, bool live, TextWriter output) =>
            ProcessingAcceptanceDiagnostic.ReprocessAsync(
                "workspace", jobId, live, Jobs.Object, Queue.Object, Access.Object, Artifacts.Object, output);
    }
}
