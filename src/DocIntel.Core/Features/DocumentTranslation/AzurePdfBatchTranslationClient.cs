using System.Text;
using Azure;
using Azure.AI.Translation.Document;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Features.DocumentTranslation;

/// <summary>Private staging and bounded processing settings for the PDF-capable batch API.</summary>
public sealed class PdfBatchTranslationOptions
{
    public const string SectionName = "DocumentTranslation";
    /// <summary>Trusted endpoint of the dedicated temporary-translation storage account, never the originals account.</summary>
    public string? StagingBlobEndpoint { get; set; }
    public string SourceContainerName { get; set; } = "translation-source";
    public string TargetContainerName { get; set; } = "translation-target";
    public int TimeoutSeconds { get; set; } = 180;
    public int PollIntervalSeconds { get; set; } = 2;

    public void Validate()
    {
        if (TimeoutSeconds is < 1 or > 600 || PollIntervalSeconds is < 1 or > 30 ||
            SourceContainerName == TargetContainerName ||
            new[] { SourceContainerName, TargetContainerName }.Any(name =>
                String.IsNullOrWhiteSpace(name) || name.Length is < 3 or > 63 ||
                name[0] == '-' || name[^1] == '-' || name.Contains("--", StringComparison.Ordinal) ||
                name.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))))
            throw new ArgumentException("Invalid private translation staging containers or timing settings.");
    }
}

/// <summary>Translates genuine PDF bytes; the implementation must not generate a source or output PDF from text.</summary>
public interface IPdfBatchTranslationClient
{
    Task<byte[]> TranslateAsync(byte[] sourcePdf, string sourceLanguage, string targetLanguage,
        IReadOnlyList<string> protectedTerms, CancellationToken cancellationToken = default);
}

/// <summary>Uses managed identity, exact per-request blob URLs and the supported asynchronous PDF translation API.</summary>
public sealed class AzurePdfBatchTranslationClient(
    BlobServiceClient storage, DocumentTranslationClient client, PdfBatchTranslationOptions options,
    ILogger<AzurePdfBatchTranslationClient> logger) : IPdfBatchTranslationClient
{
    private const int MaximumOutputBytes = 50 * 1024 * 1024;

    public async Task<byte[]> TranslateAsync(byte[] sourcePdf, string sourceLanguage, string targetLanguage,
        IReadOnlyList<string> protectedTerms, CancellationToken cancellationToken = default)
    {
        options.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var sourceContainer = storage.GetBlobContainerClient(options.SourceContainerName);
        var targetContainer = storage.GetBlobContainerClient(options.TargetContainerName);
        var id = Guid.NewGuid().ToString("N");
        var source = sourceContainer.GetBlobClient("docintel-translation-v1/" + id + "/source.pdf");
        var target = targetContainer.GetBlobClient("docintel-translation-v1/" + id + "/translated.pdf");
        var glossary = sourceContainer.GetBlobClient("docintel-translation-v1/" + id + "/protected.tsv");
        DocumentTranslationOperation? operation = null;
        Exception? pending = null;
        var owned = new List<BlobClient>();
        try
        {
            await sourceContainer.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: deadline.Token);
            await targetContainer.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: deadline.Token);
            var sourceAccess = await sourceContainer.GetAccessPolicyAsync(cancellationToken: deadline.Token);
            var targetAccess = await targetContainer.GetAccessPolicyAsync(cancellationToken: deadline.Token);
            if (sourceAccess.Value.BlobPublicAccess != PublicAccessType.None ||
                targetAccess.Value.BlobPublicAccess != PublicAccessType.None)
                throw new DocumentTranslationFailureException("TranslationStagingNotPrivate",
                    "Translation staging containers must be private; no document was uploaded.", sourceLanguage);
            await source.UploadAsync(BinaryData.FromBytes(sourcePdf), UploadOptions("application/pdf"), deadline.Token);
            owned.Add(source);
            TranslationGlossary? translationGlossary = null;
            if (protectedTerms.Count > 0)
            {
                var bytes = Encoding.UTF8.GetBytes(String.Join("\n", protectedTerms.Select(term => $"{term}\t{term}")));
                await glossary.UploadAsync(BinaryData.FromBytes(bytes), UploadOptions("text/tab-separated-values"), deadline.Token);
                owned.Add(glossary);
                translationGlossary = new TranslationGlossary(glossary.Uri, "tsv");
            }
            var input = new DocumentTranslationInput(source.Uri, target.Uri, targetLanguage, translationGlossary)
            {
                StorageUriKind = StorageInputUriKind.File
            };
            input.Source.LanguageCode = sourceLanguage == "mul" ? null : sourceLanguage;
            if (await target.ExistsAsync(deadline.Token))
                throw new DocumentTranslationFailureException("TranslationTargetExists",
                    "The private translation target already exists; no existing result was replaced.", sourceLanguage);
            operation = await client.StartTranslationAsync(input, deadline.Token);
            owned.Add(target);
            await operation.WaitForCompletionAsync(TimeSpan.FromSeconds(options.PollIntervalSeconds), deadline.Token);
            var results = new List<DocumentStatusResult>();
            await foreach (var result in operation.GetValuesAsync(deadline.Token))
            {
                results.Add(result);
                if (results.Count > 1) break;
            }
            if (results.Count != 1 || results[0].Status != DocumentTranslationStatus.Succeeded ||
                !SameLocation(results[0].SourceDocumentUri, source.Uri) ||
                !SameLocation(results[0].TranslatedDocumentUri, target.Uri))
                throw new DocumentTranslationFailureException("TranslationBatchFailed",
                    "The PDF batch did not produce the expected translated document.", sourceLanguage);
            var response = await target.DownloadStreamingAsync(cancellationToken: deadline.Token);
            await using var stream = response.Value.Content;
            using var output = new MemoryStream();
            var buffer = new byte[65_536];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (output.Length + read > MaximumOutputBytes)
                    throw new InvalidDataException("The translated PDF exceeds its supported size.");
                await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
            }
            var translated = output.ToArray();
            if (translated.Length < 5 || !translated.AsSpan().StartsWith("%PDF-"u8))
                throw new InvalidDataException("The PDF translation provider returned an invalid PDF.");
            return translated;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            pending = exception;
            throw new DocumentTranslationFailureException("TranslationTimedOut",
                "PDF translation exceeded its time limit; original source was retained.", sourceLanguage,
                innerException: exception);
        }
        catch (Exception exception)
        {
            pending = exception;
            throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            async Task AttemptAsync(Func<Task> work)
            {
                try { await work(); }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    logger.LogWarning("Private translation staging cleanup failed with {ErrorType}.",
                        exception.GetType().Name);
                }
            }
            if (pending is not null && operation is not null && !operation.HasCompleted)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await AttemptAsync(() => operation.CancelAsync(stop.Token));
                await AttemptAsync(async () =>
                    { await operation.WaitForCompletionAsync(TimeSpan.FromSeconds(1), stop.Token); });
                if (!operation.HasCompleted)
                    logger.LogWarning(
                        "Batch cancellation is not terminal; late staging output remains subject to deferred prefix expiry.");
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            foreach (var blob in owned)
                await AttemptAsync(async () => { await blob.DeleteIfExistsAsync(cancellationToken: cleanup.Token); });
            if (pending is null && cleanupFailure is not null)
                throw new DocumentTranslationFailureException("TranslationCleanupFailed",
                    "Translation staging cleanup could not be acknowledged; original source was retained.",
                    sourceLanguage, innerException: cleanupFailure);
        }
    }

    private static BlobUploadOptions UploadOptions(string contentType) => new()
    {
        HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
        Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
    };

    private static bool SameLocation(Uri? actual, Uri expected) =>
        actual is { IsAbsoluteUri: true } && actual.UserInfo.Length == 0 && actual.Query.Length == 0 &&
        actual.Fragment.Length == 0 && actual.Equals(expected);
}

internal sealed class UnconfiguredPdfBatchTranslationClient : IPdfBatchTranslationClient
{
    public Task<byte[]> TranslateAsync(byte[] sourcePdf, string sourceLanguage, string targetLanguage,
        IReadOnlyList<string> protectedTerms, CancellationToken cancellationToken = default) =>
        throw new DocumentTranslationFailureException("TranslationNotConfigured",
            "PDF batch translation and its private staging storage are not configured; original source was retained.",
            sourceLanguage);
}
