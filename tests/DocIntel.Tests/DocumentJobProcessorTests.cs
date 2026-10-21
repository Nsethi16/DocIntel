using System.Security.Cryptography;
using DocIntel.Core;
using DocIntel.Core.Authorization;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class DocumentJobProcessorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_Or_Modified_Originals_Are_Not_Processed_Or_Replayed(bool modified)
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.CreateAsync();
        fixture.Artifacts.Setup(a => a.GetOriginalPdfAsync("tenant", "one", It.IsAny<CancellationToken>()))
            .ReturnsAsync(modified ? "%PDF-modified"u8.ToArray() : null);

        // Act
        await fixture.Processor.ProcessAsync(fixture.Ticket);
        await fixture.Processor.ProcessAsync(fixture.Ticket);

        // Assert
        var job = await fixture.ReadAsync();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(1, job.ExecutionAttempts);
        Assert.Empty(job.Segments);
        fixture.Artifacts.Verify(a => a.GetOriginalPdfAsync("tenant", "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Duplicate_Delivery_Does_Not_Run_The_Workflow_Again()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Artifacts.Setup(a => a.GetOriginalPdfAsync("tenant", "one", It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, CancellationToken ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return Fixture.Source;
            });
        var first = fixture.Processor.ProcessAsync(fixture.Ticket);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        try { await fixture.Processor.ProcessAsync(fixture.Ticket).WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(); }
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        var job = await fixture.ReadAsync();
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(1, job.ExecutionAttempts);
        fixture.Artifacts.Verify(a => a.GetOriginalPdfAsync("tenant", "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Revoked_Processing_Access_Stops_Work_Before_The_Original_Is_Read()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.CreateAsync();
        fixture.Access.Setup(a => a.ResolveAsync(It.IsAny<ActorIdentity?>(), "tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentAccessScope.Denied);

        // Act
        await fixture.Processor.ProcessAsync(fixture.Ticket);

        // Assert
        var job = await fixture.ReadAsync();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("Document processing access was revoked.", job.ErrorMessage);
        fixture.Artifacts.Verify(a => a.GetOriginalPdfAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Revocation_Before_Final_Publication_Does_Not_Produce_A_Completed_Result()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.CreateAsync();
        fixture.Access.SetupSequence(a => a.ResolveAsync(
                It.IsAny<ActorIdentity?>(), "tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentAccessScope.Unrestricted).ReturnsAsync(DocumentAccessScope.Denied);

        // Act
        await fixture.Processor.ProcessAsync(fixture.Ticket);

        // Assert
        Assert.Equal(JobStatus.Failed, (await fixture.ReadAsync()).Status);
    }

    [Fact]
    public async Task Execution_Timeout_Is_Persisted_Instead_Of_Remaining_Running()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.CreateAsync();
        fixture.Provider.GetRequiredService<DocumentProcessingOptions>().MaxRunSeconds = 1;
        fixture.Artifacts.Setup(a => a.GetOriginalPdfAsync("tenant", "one", It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, CancellationToken ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Fixture.Source;
            });

        // Act
        await fixture.Processor.ProcessAsync(fixture.Ticket).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        var job = await fixture.ReadAsync();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("Document processing exceeded its time limit.", job.ErrorMessage);
    }

    [Fact]
    public async Task Repeated_Interruptions_Are_Bounded_Without_Another_Provider_Call()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.CreateAsync();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var interrupted = await fixture.Jobs.TryAcquireJobAsync("tenant", "one");
        }

        // Act
        await fixture.Processor.ProcessAsync(fixture.Ticket);

        // Assert
        var job = await fixture.ReadAsync();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("repeatedly interrupted", job.ErrorMessage);
        fixture.Artifacts.Verify(a => a.GetOriginalPdfAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_Completed_Job_Is_Not_Published_Unless_Its_Processing_Json_Is_Durably_Acknowledged()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.CreateAsync();
        fixture.Artifacts.Setup(a => a.SaveProcessingResultAsync(It.IsAny<DocumentJob>(), It.IsAny<CancellationToken>()))
            .Returns<DocumentJob, CancellationToken>(async (snapshot, _) =>
            {
                Assert.Equal(JobStatus.Completed, snapshot.Status);
                Assert.Equal(JobStatus.Running, (await fixture.ReadAsync()).Status);
                throw new IOException("Archive unavailable");
            });

        // Act
        await fixture.Processor.ProcessAsync(fixture.Ticket);

        // Assert
        var job = await fixture.ReadAsync();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Null(job.ProcessingResultSha256);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "DocIntel.Processor.Tests",
            Guid.NewGuid().ToString("N"));
        private readonly TenantHarness _tenant = new()
        {
            TenantId = "tenant",
            DisplayName = "Test",
            HarnessVersion = 1,
            Capabilities = []
        };
        public static byte[] Source => "%PDF-accepted"u8.ToArray();
        public Mock<IArtifactStore> Artifacts { get; } = new();
        public Mock<ITenantAccessResolver> Access { get; } = new();
        public SqliteJobStore Jobs { get; }
        public ServiceProvider Provider { get; }
        public JobQueueTicket Ticket { get; } = new("one", "tenant");
        public DocumentJobProcessor Processor => Provider.GetRequiredService<DocumentJobProcessor>();

        public Fixture()
        {
            Jobs = new SqliteJobStore(Path.Combine(_directory, "jobs.sqlite"));
            var harnesses = new Mock<IHarnessStore>();
            harnesses.Setup(h => h.GetTenantAsync("tenant", It.IsAny<CancellationToken>())).ReturnsAsync(_tenant);
            Artifacts.Setup(a => a.GetOriginalPdfAsync("tenant", "one", It.IsAny<CancellationToken>())).ReturnsAsync(Source);
            Artifacts.Setup(a => a.SaveProcessingResultAsync(It.IsAny<DocumentJob>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new string('A', 64));
            Access.Setup(a => a.ResolveAsync(It.IsAny<ActorIdentity?>(), "tenant", It.IsAny<CancellationToken>()))
                .ReturnsAsync(DocumentAccessScope.Unrestricted);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IJobStore>(Jobs).AddSingleton(harnesses.Object).AddSingleton(Artifacts.Object)
                .AddSingleton(Access.Object);
            services.AddDocIntelCore();
            Provider = services.BuildServiceProvider();
        }

        public Task CreateAsync() => Jobs.UpsertJobAsync(new DocumentJob
        {
            JobId = "one",
            TenantId = "tenant",
            TenantDisplayName = "Test",
            FileName = "source.pdf",
            AppliedHarnessVersion = 1,
            ProcessingHarness = _tenant,
            OriginalSha256 = Convert.ToHexString(SHA256.HashData(Source))
        });

        public async Task<DocumentJob> ReadAsync() => (await Jobs.GetJobAsync("one", tenantId: "tenant"))!;

        public void Dispose()
        {
            Provider.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
