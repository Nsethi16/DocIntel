using DocIntel.Core;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace DocIntel.Tests;

public sealed class LargeDocumentProcessorTests
{
    [Fact]
    public async Task An_Accepted_200_Page_Pdf_With_A_Job_Over_2_MiB_Completes_Once_With_All_Source_Evidence()
    {
        // Arrange
        var source = CreateLargePdf();
        Assert.True(source.Length < 25 * 1024 * 1024);
        using (var pdf = PdfDocument.Open(source)) Assert.Equal(200, pdf.NumberOfPages);
        var tenant = Tenant();
        using var fixture = new LargeDocumentCosmosFixture(source, tenant);
        await fixture.InitializeAsync();
        var native = new PdfPigLayoutExtractor();
        var extractor = new Mock<IPageLayoutExtractor>();
        extractor.Setup(e => e.ExtractPagesAsync(It.IsAny<byte[]>(), It.IsAny<SplittingConfig>(),
                It.IsAny<CancellationToken>()))
            .Returns<byte[], SplittingConfig, CancellationToken>((bytes, config, ct) =>
                native.ExtractPagesAsync(bytes, config, ct));
        using var provider = Services(fixture, tenant, extractor.Object);
        var processor = provider.GetRequiredService<DocumentJobProcessor>();
        var ticket = new JobQueueTicket("one", "tenant");

        // Act
        await processor.ProcessAsync(ticket);
        await processor.ProcessAsync(ticket);
        var job = (await fixture.CreateStore().GetJobAsync("one", tenantId: "tenant"))!;
        var layout = await fixture.Artifacts.GetLayoutAsync("tenant", job.ArtifactDocumentId!,
            job.LayoutSha256!, job.OriginalSha256!);
        var archived = await fixture.Artifacts.GetProcessingResultAsync(job);

        // Assert
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(200, job.TotalPages);
        Assert.Equal(1, job.ExecutionAttempts);
        Assert.True(LargeDocumentCosmosFixture.SerializedSize(job) > 2 * 1024 * 1024);
        Assert.Contains("DOC-P200-L059", String.Join("\n", job.Segments.Select(s => s.BodyText)));
        Assert.Contains("DOC-P200-L059", String.Join("\n", job.Segments.Select(s => s.Markdown)));
        Assert.Equal(200, layout!.Pages.Count);
        Assert.Contains(layout.Pages[199].Words, word => word.Text.Contains("DOC-P200-L059", StringComparison.Ordinal));
        Assert.Equal(source, await fixture.Artifacts.GetOriginalPdfAsync("tenant", "one"));
        Assert.NotNull(archived);
        Assert.Equal(archived, await fixture.Artifacts.GetDocumentContentAsync(job));
        Assert.Equal(job.ProcessingResultSha256, job.ContentSnapshot!.Sha256);
        Assert.Contains(fixture.WrittenJobs, checkpoint =>
            checkpoint.Status == JobStatus.Running && checkpoint.ContentSnapshot?.Kind == "DocumentContent");
        Assert.All(fixture.SerializedWrites, size => Assert.True(size < 2 * 1024 * 1024));
        Assert.Empty(await fixture.Store.ListRecoverableJobsAsync(100));
        extractor.Verify(e => e.ExtractPagesAsync(It.IsAny<byte[]>(), It.IsAny<SplittingConfig>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_Deterministically_Rejected_Checkpoint_Is_Failed_Once_Not_Rediscovered_Or_Reexecuted()
    {
        // Arrange
        var tenant = Tenant();
        using var fixture = new LargeDocumentCosmosFixture(tenant: tenant);
        await fixture.InitializeAsync();
        var native = new PdfPigLayoutExtractor();
        var extractor = new Mock<IPageLayoutExtractor>();
        extractor.Setup(e => e.ExtractPagesAsync(It.IsAny<byte[]>(), It.IsAny<SplittingConfig>(),
                It.IsAny<CancellationToken>()))
            .Returns<byte[], SplittingConfig, CancellationToken>(async (bytes, config, ct) =>
            {
                var pages = await native.ExtractPagesAsync(bytes, config, ct);
                fixture.RejectNextPayload = true;
                return pages;
            });
        using var provider = Services(fixture, tenant, extractor.Object);
        var processor = provider.GetRequiredService<DocumentJobProcessor>();
        var ticket = new JobQueueTicket("one", "tenant");

        // Act
        await processor.ProcessAsync(ticket);
        await processor.ProcessAsync(ticket);
        var job = (await fixture.CreateStore().GetJobAsync("one", tenantId: "tenant"))!;

        // Assert
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(DocumentStorageLimitException.CosmosPayloadTooLarge, job.ErrorCode);
        Assert.Equal(1, job.ExecutionAttempts);
        Assert.True(job.SearchMetadataUnavailable);
        Assert.NotNull(job.ContentSnapshot);
        Assert.NotNull(job.LayoutSha256);
        Assert.Empty(await fixture.Store.ListRecoverableJobsAsync(100));
        extractor.Verify(e => e.ExtractPagesAsync(It.IsAny<byte[]>(), It.IsAny<SplittingConfig>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Revoked_Processing_Access_Is_Checked_Before_Source_Or_Content_Provider_Reads()
    {
        // Arrange
        var tenant = Tenant();
        using var fixture = new LargeDocumentCosmosFixture(tenant: tenant);
        var extractor = new Mock<IPageLayoutExtractor>(MockBehavior.Strict);
        var artifacts = new Mock<IArtifactStore>(MockBehavior.Strict);
        using var provider = Services(fixture, tenant, extractor.Object, artifacts.Object, allowAccess: false);

        // Act
        await provider.GetRequiredService<DocumentJobProcessor>().ProcessAsync(new("one", "tenant"));
        var job = (await fixture.Store.GetJobAsync("one", tenantId: "tenant"))!;

        // Assert
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("Document processing access was revoked.", job.ErrorMessage);
        artifacts.Verify(a => a.GetOriginalPdfAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        extractor.Verify(e => e.ExtractPagesAsync(It.IsAny<byte[]>(), It.IsAny<SplittingConfig>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static TenantHarness Tenant() => new()
    {
        TenantId = "tenant", DisplayName = "Test",
        Capabilities = new(StringComparer.OrdinalIgnoreCase)
        {
            [FeatureHarness.SplitAndGroupKey] = new()
            {
                Enabled = true,
                SplitAndGroup = new() { ProduceSplitPdfBytes = false }
            }
        }
    };

    private static ServiceProvider Services(LargeDocumentCosmosFixture fixture, TenantHarness tenant,
        IPageLayoutExtractor extractor, IArtifactStore? artifacts = null, bool allowAccess = true)
    {
        var harnesses = new Mock<IHarnessStore>();
        harnesses.Setup(h => h.GetTenantAsync("tenant", It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        var access = new Mock<ITenantAccessResolver>();
        access.Setup(a => a.ResolveAsync(It.IsAny<ActorIdentity?>(), "tenant", It.IsAny<CancellationToken>()))
            .ReturnsAsync(allowAccess ? DocumentAccessScope.Unrestricted : DocumentAccessScope.Denied);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IJobStore>(fixture.Store).AddSingleton<IArtifactStore>(artifacts ?? fixture.Artifacts)
            .AddSingleton<IHarnessStore>(harnesses.Object).AddSingleton(extractor).AddSingleton(access.Object);
        services.AddDocIntelCore();
        return services.BuildServiceProvider();
    }

    private static byte[] CreateLargePdf()
    {
        using var stream = new MemoryStream();
        using (var builder = new PdfDocumentBuilder(stream, disposeStream: false))
        {
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            for (var number = 1; number <= 200; number++)
            {
                var page = builder.AddPage(PageSize.Letter);
                for (var line = 0; line < 60; line++)
                {
                    var prefix = $"DOC-P{number:D3}-L{line:D3}";
                    page.AddText(prefix + "-A-" + new string('a', 40) + " " +
                        prefix + "-B-" + new string('b', 40), 5, new PdfPoint(40, 760 - line * 10), font);
                }
                page.AddText("Large-Contract-v1", 8, new PdfPoint(40, 35), font);
            }
            builder.Build();
        }
        return stream.ToArray();
    }
}
