using Azure;
using Azure.AI.Translation.Document;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DocIntel.Core;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.DocumentTranslation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class PdfBatchTranslationTests
{
    [Fact]
    public void Missing_Staging_Endpoint_Never_Reuses_The_Configured_Original_Artifact_Account()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAI:TranslatorEndpoint"] = "https://translator.example/",
            ["AzureAI:BlobEndpoint"] = "https://originals.blob.core.windows.net/",
            ["AzureAI:CosmosEndpoint"] = "https://metadata.documents.azure.com/"
        }).Build();
        using var services = new ServiceCollection().AddLogging().AddDocIntelCore(configuration).BuildServiceProvider();

        // Act
        var batch = services.GetRequiredService<IPdfBatchTranslationClient>();

        // Assert
        Assert.IsNotType<AzurePdfBatchTranslationClient>(batch);
        Assert.Equal("UnconfiguredPdfBatchTranslationClient", batch.GetType().Name);
    }

    [Fact]
    public void A_Dedicated_Staging_Endpoint_Uses_The_Real_Batch_Client_Without_Shared_Original_Storage()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAI:TranslatorEndpoint"] = "https://translator.example/",
            ["DocumentTranslation:StagingBlobEndpoint"] = "https://staging.blob.core.windows.net/"
        }).Build();
        using var services = new ServiceCollection().AddLogging().AddDocIntelCore(configuration).BuildServiceProvider();

        // Act
        var batch = services.GetRequiredService<IPdfBatchTranslationClient>();

        // Assert
        Assert.IsType<AzurePdfBatchTranslationClient>(batch);
    }

    [Theory]
    [InlineData("http://staging.blob.core.windows.net/")]
    [InlineData("https://user:credential@staging.blob.core.windows.net/")]
    [InlineData("https://staging.blob.core.windows.net/container")]
    [InlineData("https://staging.blob.core.windows.net/?sig=not-allowed")]
    [InlineData("https://originals.blob.core.windows.net/")]
    public void Unsafe_Or_Shared_Staging_Endpoints_Are_Configuration_Errors(string staging)
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAI:BlobEndpoint"] = "https://originals.blob.core.windows.net/",
            ["DocumentTranslation:StagingBlobEndpoint"] = staging
        }).Build();

        // Act
        var failure = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddLogging().AddDocIntelCore(configuration));

        // Assert
        Assert.DoesNotContain(staging, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("fr", "fr")]
    [InlineData("mul", null)]
    public async Task Pdf_Uses_Private_Per_Request_File_Batch_With_No_Sas_And_Only_Owned_Blob_Cleanup(
        string sourceLanguage, string? providerLanguage)
    {
        // Arrange
        var fixture = new Fixture();

        // Act
        var result = await fixture.Client.TranslateAsync(
            "%PDF-original"u8.ToArray(), sourceLanguage, "en", ["ProtectedBrand"]);

        // Assert
        Assert.Equal(fixture.Output, result);
        Assert.Equal(StorageInputUriKind.File, fixture.Input!.StorageUriKind);
        Assert.Equal(providerLanguage, fixture.Input.Source.LanguageCode);
        Assert.Equal(String.Empty, fixture.Input.Source.SourceUri.Query);
        var target = Assert.Single(fixture.Input.Targets);
        Assert.Equal(String.Empty, target.TargetUri.Query);
        Assert.Equal("en", target.LanguageCode);
        Assert.Equal("tsv", Assert.Single(target.Glossaries).Format);
        Assert.Equal(3, fixture.Deleted.Count);
        Assert.All(fixture.Blobs.Values, blob => blob.Verify(client => client.DeleteIfExistsAsync(
            It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(),
            It.IsAny<CancellationToken>()), Times.Once));
        Assert.DoesNotContain(fixture.Uploads, upload => upload.Key.Contains("tenant", StringComparison.Ordinal));
        Assert.All(fixture.Uploads, upload => Assert.Equal(ETag.All, upload.Options.Conditions.IfNoneMatch));
        Assert.All(fixture.Blobs.Keys, key => Assert.Contains("/docintel-translation-v1/", key));
        fixture.Operation.Verify(operation => operation.CancelAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_Unexpected_Output_Url_Cannot_Read_Another_Requests_Pdf()
    {
        // Arrange
        var fixture = new Fixture { WrongTarget = true };

        // Act
        var failure = await Assert.ThrowsAsync<DocumentTranslationFailureException>(() =>
            fixture.Client.TranslateAsync("%PDF-source"u8.ToArray(), "fr", "en", []));

        // Assert
        Assert.Equal("TranslationBatchFailed", failure.Code);
        Assert.Equal(2, fixture.Deleted.Count);
        Assert.All(fixture.Blobs.Values, blob => blob.Verify(client => client.DownloadStreamingAsync(
            It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()), Times.Never));
    }

    [Fact]
    public async Task Public_Staging_Is_Rejected_Before_Any_Source_Document_Is_Uploaded()
    {
        // Arrange
        var fixture = new Fixture(PublicAccessType.Blob);

        // Act
        var failure = await Assert.ThrowsAsync<DocumentTranslationFailureException>(() =>
            fixture.Client.TranslateAsync("%PDF-source"u8.ToArray(), "fr", "en", []));

        // Assert
        Assert.Equal("TranslationStagingNotPrivate", failure.Code);
        Assert.Empty(fixture.Uploads);
        Assert.Empty(fixture.Deleted);
        fixture.Provider.Verify(provider => provider.StartTranslationAsync(
            It.IsAny<DocumentTranslationInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Existing_Targets_Are_Never_Overwritten_Or_Deleted()
    {
        // Arrange
        var fixture = new Fixture { TargetExists = true };

        // Act
        var failure = await Assert.ThrowsAsync<DocumentTranslationFailureException>(() =>
            fixture.Client.TranslateAsync("%PDF-source"u8.ToArray(), "fr", "en", []));

        // Assert
        Assert.Equal("TranslationTargetExists", failure.Code);
        Assert.Single(fixture.Deleted);
        Assert.All(fixture.Deleted, name => Assert.StartsWith("translation-source/", name));
        fixture.Provider.Verify(provider => provider.StartTranslationAsync(
            It.IsAny<DocumentTranslationInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancellation_Requests_Server_Stop_And_Does_Not_Produce_A_Translated_Success()
    {
        // Arrange
        var fixture = new Fixture { Completed = false };
        using var cancellation = new CancellationTokenSource();
        fixture.Operation.Setup(operation => operation.WaitForCompletionAsync(
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cancellation.Cancel();
                return ValueTask.FromException<Response<AsyncPageable<DocumentStatusResult>>>(
                    new OperationCanceledException(cancellation.Token));
            });

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Client.TranslateAsync("%PDF-source"u8.ToArray(), "fr", "en", [], cancellation.Token));

        // Assert
        fixture.Operation.Verify(operation => operation.CancelAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, fixture.Deleted.Count);
    }

    [Fact]
    public async Task Cancellation_Waits_For_Terminal_State_Before_The_Final_Staging_Deletion()
    {
        // Arrange
        var fixture = new Fixture { Completed = false };
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        fixture.Operation.Setup(operation => operation.WaitForCompletionAsync(
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns((TimeSpan _, CancellationToken token) =>
            {
                if (++calls == 1)
                {
                    cancellation.Cancel();
                    return ValueTask.FromException<Response<AsyncPageable<DocumentStatusResult>>>(
                        new OperationCanceledException(cancellation.Token));
                }
                Assert.False(token.IsCancellationRequested);
                fixture.Completed = true;
                fixture.Events.Add("terminal");
                return ValueTask.FromResult(Response.FromValue(
                    AsyncPageable<DocumentStatusResult>.FromPages([]), Mock.Of<Response>()));
            });

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Client.TranslateAsync("%PDF-source"u8.ToArray(), "fr", "en", [], cancellation.Token));

        // Assert
        Assert.Equal(2, calls);
        Assert.Equal(new[] { "cancel", "terminal", "delete", "delete" }, fixture.Events);
    }

    private sealed class Fixture
    {
        public Mock<BlobServiceClient> Storage { get; } = new();
        public Mock<DocumentTranslationClient> Provider { get; } = new();
        public Mock<DocumentTranslationOperation> Operation { get; } = new();
        public Dictionary<string, Mock<BlobClient>> Blobs { get; } = new(StringComparer.Ordinal);
        public List<(string Key, BlobUploadOptions Options)> Uploads { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<string> Events { get; } = [];
        public DocumentTranslationInput? Input { get; private set; }
        public bool WrongTarget { get; set; }
        public bool TargetExists { get; set; }
        public bool Completed { get; set; } = true;
        public byte[] Output { get; } = PdfPigLayoutExtractor.CreateSamplePdf([("Genuine translated output", "Fixture")]);
        public AzurePdfBatchTranslationClient Client { get; }

        public Fixture(PublicAccessType access = PublicAccessType.None)
        {
            Storage.Setup(storage => storage.GetBlobContainerClient(It.IsAny<string>()))
                .Returns((string name) => Container(name, access).Object);
            Provider.Setup(provider => provider.StartTranslationAsync(
                    It.IsAny<DocumentTranslationInput>(), It.IsAny<CancellationToken>()))
                .Callback((DocumentTranslationInput input, CancellationToken _) => Input = input)
                .ReturnsAsync(Operation.Object);
            Operation.SetupGet(operation => operation.HasCompleted).Returns(() => Completed);
            Operation.Setup(operation => operation.WaitForCompletionAsync(
                    It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns(() => ValueTask.FromResult(Response.FromValue(Results(), Mock.Of<Response>())));
            Operation.Setup(operation => operation.GetValuesAsync(It.IsAny<CancellationToken>())).Returns(Results);
            Operation.Setup(operation => operation.CancelAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Events.Add("cancel")).Returns(Task.CompletedTask);
            Client = new(Storage.Object, Provider.Object, new(), NullLogger<AzurePdfBatchTranslationClient>.Instance);
        }

        private AsyncPageable<DocumentStatusResult> Results()
        {
            var value = DocumentTranslationModelFactory.DocumentStatusResult("fixture",
                Input!.Source.SourceUri, WrongTarget ? new Uri("https://unrelated.example/another.pdf") :
                    Input.Targets.Single().TargetUri, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                DocumentTranslationStatus.Succeeded, "en", 1, 100);
            return AsyncPageable<DocumentStatusResult>.FromPages(
                [Page<DocumentStatusResult>.FromValues([value], null, Mock.Of<Response>())]);
        }

        private Mock<BlobContainerClient> Container(string name, PublicAccessType access)
        {
            var container = new Mock<BlobContainerClient>();
            container.Setup(value => value.CreateIfNotExistsAsync(PublicAccessType.None,
                It.IsAny<IDictionary<string, string>>(), It.IsAny<BlobContainerEncryptionScopeOptions>(),
                It.IsAny<CancellationToken>())).ReturnsAsync((Response<BlobContainerInfo>)null!);
            container.Setup(value => value.GetAccessPolicyAsync(
                    It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobContainerAccessPolicy(
                    access, new ETag("fixture"), DateTimeOffset.UtcNow, []), Mock.Of<Response>()));
            container.Setup(value => value.GetBlobClient(It.IsAny<string>())).Returns((string blobName) =>
            {
                var key = name + "/" + blobName;
                var blob = new Mock<BlobClient>();
                blob.SetupGet(value => value.Uri).Returns(new Uri("https://storage.example/" + key));
                blob.Setup(value => value.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(),
                        It.IsAny<CancellationToken>()))
                    .Callback((BinaryData _, BlobUploadOptions options, CancellationToken _) => Uploads.Add((key, options)))
                    .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());
                blob.Setup(value => value.ExistsAsync(It.IsAny<CancellationToken>()))
                    .Returns(() => Task.FromResult(Response.FromValue(TargetExists, Mock.Of<Response>())));
                blob.Setup(value => value.DownloadStreamingAsync(
                        It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
                    .Returns(() => Task.FromResult(Response.FromValue(
                        BlobsModelFactory.BlobDownloadStreamingResult(new MemoryStream(Output)), Mock.Of<Response>())));
                blob.Setup(value => value.DeleteIfExistsAsync(It.IsAny<DeleteSnapshotsOption>(),
                        It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
                    .Callback(() => { Deleted.Add(key); Events.Add("delete"); })
                    .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));
                Blobs[key] = blob;
                return blob.Object;
            });
            return container;
        }
    }
}
