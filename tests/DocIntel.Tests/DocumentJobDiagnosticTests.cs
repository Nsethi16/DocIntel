using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using DocIntel.Api.Diagnostics;
using DocIntel.Core;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.DocumentTranslation;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Storage;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class DocumentJobDiagnosticTests
{
    [Fact]
    public async Task A_Completed_Language_Warning_Can_Be_Probed_Without_Reprocessing_Or_Changing_Stored_Output()
    {
        // Arrange
        var source = Source();
        source.Status = JobStatus.Completed;
        source.Segments[0].Markdown = "private-complete-source";
        source.Segments[0].TranslationErrorCode = "LanguageUndetermined";
        source.ExecutionTrace.Add(new("TranslateDocumentsExecutor", true, false, null, 1, "private-note"));
        var before = JsonSerializer.Serialize(source);
        var fixture = new Fixture();
        fixture.Jobs.Setup(store => store.GetJobAsync(source.JobId, It.IsAny<CancellationToken>(), "workspace"))
            .ReturnsAsync(source);
        fixture.Access.Setup(access => access.ResolveAsync(source.SubmittedBy, "workspace", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentAccessScope.Unrestricted);
        fixture.Artifacts.Setup(store => store.GetSlicePdfAsync("workspace", source.ArtifactDocumentId!, 0,
            It.IsAny<CancellationToken>(), true)).ReturnsAsync((byte[]?)null);
        fixture.Artifacts.Setup(store => store.GetSlicePdfAsync("workspace", source.ArtifactDocumentId!, 0,
            It.IsAny<CancellationToken>(), false)).ReturnsAsync("%PDF-synthetic"u8.ToArray());
        fixture.Translation.Setup(service => service.TranslatePdfSliceAsync(
                It.IsAny<byte[]>(), "slice-1-pages-1-1.pdf", "private-complete-source",
                It.IsAny<IReadOnlyList<string>>(), "en", "https://configured.example/",
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentSliceTranslationResult("fr", true, "private-translated", "%PDF-translation"u8.ToArray())
            {
                LanguageDetection = new("fr")
                    { UseAutomaticSourceLanguage = true, Samples = [new(0, 200, "fr", 0.6, true)] }
            });
        using var output = new StringWriter();

        // Act
        var result = await fixture.Run(source.JobId, true, true, output);

        // Assert
        Assert.Equal(0, result);
        Assert.Contains("\"probeTranslated\":true", output.ToString());
        Assert.Contains("\"languageChecks\"", output.ToString());
        Assert.Contains("0.6", output.ToString());
        Assert.DoesNotContain("private", output.ToString());
        Assert.Equal(before, JsonSerializer.Serialize(source));
        fixture.Jobs.Verify(store => store.UpsertJobAsync(
            It.IsAny<DocumentJob>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Artifacts.Verify(store => store.SaveSlicePdfAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<byte[]>(),
            It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task A_Successful_Completed_Translation_Is_Not_Eligible_For_A_Probe()
    {
        // Arrange
        var source = Source();
        source.Status = JobStatus.Completed;
        source.ExecutionTrace.Add(new("TranslateDocumentsExecutor", true, true, null, 1, "private-note"));
        var fixture = new Fixture();
        fixture.Jobs.Setup(store => store.GetJobAsync(source.JobId, It.IsAny<CancellationToken>(), "workspace"))
            .ReturnsAsync(source);
        fixture.Access.Setup(access => access.ResolveAsync(source.SubmittedBy, "workspace", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentAccessScope.Unrestricted);
        using var output = new StringWriter();

        // Act
        var result = await fixture.Run(source.JobId, true, true, output);

        // Assert
        Assert.Equal(2, result);
        Assert.Contains("TranslationStageNotEligible", output.ToString());
        fixture.Artifacts.VerifyNoOtherCalls();
        fixture.Translation.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Filename_Hash_Lookup_Prints_Only_Metadata_Without_A_Provider_Call_Or_Stored_Changes()
    {
        // Arrange
        var source = Source();
        var before = JsonSerializer.Serialize(source);
        var fixture = new Fixture();
        fixture.Jobs.Setup(store => store.ListJobsByTenantAsync("workspace", It.IsAny<CancellationToken>()))
            .ReturnsAsync([source]);
        var reference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.FileName)));
        using var output = new StringWriter();

        // Act
        var result = await fixture.Run(reference, false, false, output);

        // Assert
        Assert.Equal(0, result);
        Assert.Contains(source.JobId, output.ToString());
        Assert.Contains("\"lastStep\":\"IdentifyDocumentsExecutor\"", output.ToString());
        Assert.DoesNotContain("private", output.ToString());
        Assert.Equal(before, JsonSerializer.Serialize(source));
        fixture.Jobs.Verify(store => store.ListJobsByTenantAsync("workspace", It.IsAny<CancellationToken>()), Times.Once);
        fixture.Jobs.VerifyNoOtherCalls();
        fixture.Access.VerifyNoOtherCalls();
        fixture.Artifacts.VerifyNoOtherCalls();
        fixture.Translation.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Translation_Probe_Requires_Live_Opt_In_And_An_Exact_Document_Id()
    {
        // Arrange
        var fixture = new Fixture();
        using var output = new StringWriter();

        // Act
        var result = await fixture.Run(Guid.NewGuid().ToString("N"), true, false, output);

        // Assert
        Assert.Equal(2, result);
        Assert.Contains("ExplicitDocumentAndLiveProbeRequired", output.ToString());
        fixture.Jobs.VerifyNoOtherCalls();
        fixture.Translation.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Configured", false)]
    [InlineData("WithoutGlossary", false)]
    [InlineData("Configured", true)]
    [InlineData("WithoutGlossary", true)]
    public async Task Translation_Probe_Reports_Real_HTTP_Status_Without_Exposing_Provider_Text_Or_Writing_Results(
        string variant, bool normalized)
    {
        // Arrange
        var source = Source();
        var before = JsonSerializer.Serialize(source);
        var fixture = new Fixture();
        fixture.Jobs.Setup(store => store.GetJobAsync(source.JobId, It.IsAny<CancellationToken>(), "workspace"))
            .ReturnsAsync(source);
        fixture.Access.Setup(access => access.ResolveAsync(source.SubmittedBy, "workspace", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentAccessScope.Unrestricted);
        fixture.Artifacts.Setup(store => store.GetSlicePdfAsync("workspace", source.ArtifactDocumentId!, 0,
            It.IsAny<CancellationToken>(), true)).ReturnsAsync((byte[]?)null);
        fixture.Artifacts.Setup(store => store.GetSlicePdfAsync("workspace", source.ArtifactDocumentId!, 0,
            It.IsAny<CancellationToken>(), false)).ReturnsAsync("%PDF-synthetic"u8.ToArray());
        fixture.Translation.Setup(service => service.TranslatePdfSliceAsync(
            It.IsAny<byte[]>(), "slice-1-pages-1-1.pdf", "private-source", It.IsAny<IReadOnlyList<string>>(),
            "en", "https://configured.example/",
            It.Is<IReadOnlyList<string>>(terms => (terms.Count == 0) == (variant == "WithoutGlossary")),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(normalized
                ? new DocumentTranslationFailureException("TranslationProviderFailed", "private-provider-body", "fr", 400)
                : new RequestFailedException(400, "private-provider-body", "400070", null));
        using var output = new StringWriter();

        // Act
        var result = await fixture.Run(source.JobId, true, true, output, variant);

        // Assert
        Assert.Equal(1, result);
        Assert.Contains("\"stage\":\"Translate\"", output.ToString());
        Assert.Contains("\"httpStatus\":400", output.ToString());
        Assert.Contains(normalized ? "\"failureCode\":\"TranslationProviderFailed\"" :
            "\"providerCode\":\"400070\"", output.ToString());
        Assert.Contains("\"variant\":\"" + variant + "\"", output.ToString());
        Assert.DoesNotContain("private", output.ToString());
        Assert.Equal(before, JsonSerializer.Serialize(source));
        fixture.Artifacts.Verify(store => store.GetSlicePdfAsync("workspace", source.ArtifactDocumentId!, 0,
            It.IsAny<CancellationToken>(), true), Times.Once);
        fixture.Artifacts.Verify(store => store.GetSlicePdfAsync("workspace", source.ArtifactDocumentId!, 0,
            It.IsAny<CancellationToken>(), false), Times.Once);
        fixture.Artifacts.VerifyNoOtherCalls();
        fixture.Slicer.VerifyNoOtherCalls();
        fixture.Jobs.Verify(store => store.GetJobAsync(source.JobId, It.IsAny<CancellationToken>(), "workspace"), Times.Once);
        fixture.Jobs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_Revoked_Actor_Cannot_Trigger_A_Translation_Probe()
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
        var result = await fixture.Run(source.JobId, true, true, output);

        // Assert
        Assert.Equal(2, result);
        Assert.Contains("AccessRevoked", output.ToString());
        fixture.Artifacts.VerifyNoOtherCalls();
        fixture.Translation.VerifyNoOtherCalls();
    }

    private static DocumentJob Source() => new()
    {
        JobId = Guid.NewGuid().ToString("N"), TenantId = "workspace", TenantDisplayName = "private-workspace",
        AppliedHarnessVersion = 3, FileName = "private-filename.pdf", Status = JobStatus.Failed,
        SubmittedBy = new(Guid.NewGuid(), Guid.NewGuid()), ArtifactDocumentId = "private-current-attempt",
        ProcessingHarness = new() { TenantId = "workspace", DisplayName = "private-workspace", HarnessVersion = 3 },
        TotalPages = 1, Segments = [new() { SegmentIndex = 0, StartPage = 1, EndPage = 1, BodyText = "private-source" }],
        ExecutionTrace = [new("IdentifyDocumentsExecutor", true, true, "decision-1", 1, "private-note")]
    };

    private sealed class Fixture
    {
        public Mock<IJobStore> Jobs { get; } = new(MockBehavior.Strict);
        public Mock<ITenantAccessResolver> Access { get; } = new(MockBehavior.Strict);
        public Mock<IArtifactStore> Artifacts { get; } = new(MockBehavior.Strict);
        public Mock<IPdfSlicer> Slicer { get; } = new(MockBehavior.Strict);
        public Mock<IDocumentTranslationService> Translation { get; } = new(MockBehavior.Strict);

        public Task<int> Run(string reference, bool probe, bool live, TextWriter output, string variant = "Configured") =>
            DocumentJobDiagnostic.ExecuteAsync("workspace", reference, probe, live, Jobs.Object, Access.Object,
                Artifacts.Object, Slicer.Object, Translation.Object,
                new AzureIntegrationOptions { TranslatorEndpoint = "https://configured.example/" }, output, variant: variant);
    }
}
