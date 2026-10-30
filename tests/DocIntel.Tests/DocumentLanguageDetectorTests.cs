using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using DocIntel.Core.Features.DocumentTranslation;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class DocumentLanguageDetectorTests
{
    [Fact]
    public async Task Real_Detect_Protocol_Is_Bounded_And_Does_Not_Translate_Or_Guess_English()
    {
        // Arrange
        var handler = new DetectorHandler((count, _) => Enumerable.Range(0, count)
            .Select(_ => new { language = "fr", score = 1.0, isTranslationSupported = true }).ToArray());
        var credential = Credential();
        using var detector = new AzureDocumentLanguageDetector(
            new HttpClient(handler), credential.Object, new Uri("https://translator.example/"));
        var text = String.Concat(Enumerable.Repeat(
            "Le document source contient une description suffisamment longue pour identifier sa langue. " +
            "Les données originales doivent rester inchangées. ", 50));

        // Act
        var result = await detector.DetectAsync(text);

        // Assert
        Assert.Equal("fr", result.Language);
        Assert.Equal("/translator/text/v3.0/detect?api-version=3.0", handler.Uri!.PathAndQuery);
        Assert.Equal("Bearer", handler.Authorization!.Scheme);
        Assert.Equal("private-test-token", handler.Authorization.Parameter);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.InRange(body.RootElement.GetArrayLength(), 1, 3);
        Assert.All(body.RootElement.EnumerateArray(), item =>
            Assert.InRange(item.GetProperty("Text").GetString()!.Length, 1, 1500));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Confident_Different_Languages_Are_Explicitly_Mixed_Not_A_Majority_English_Skip()
    {
        // Arrange
        var handler = new DetectorHandler((count, _) => Enumerable.Range(0, count)
            .Select(index => new { language = index == 0 ? "en" : "es", score = 1.0, isTranslationSupported = true }).ToArray());
        using var detector = new AzureDocumentLanguageDetector(
            new HttpClient(handler), Credential().Object, new Uri("https://translator.example/"));
        var source = new string('a', 2000) + new string('b', 2000) + new string('c', 2000);

        // Act
        var result = await detector.DetectAsync(source);

        // Assert
        Assert.Equal("mul", result.Language);
    }

    [Fact]
    public async Task Uncertain_Detection_Is_An_Explicit_Feature_Failure()
    {
        // Arrange
        var handler = new DetectorHandler((count, _) => Enumerable.Range(0, count)
            .Select(_ => new { language = "en", score = 0.3, isTranslationSupported = true }).ToArray());
        using var detector = new AzureDocumentLanguageDetector(
            new HttpClient(handler), Credential().Object, new Uri("https://translator.example/"));

        // Act
        var failure = await Assert.ThrowsAsync<DocumentTranslationFailureException>(() =>
            detector.DetectAsync("This is an original document with enough source text to detect."));

        // Assert
        Assert.Equal("LanguageUndetermined", failure.Code);
    }

    [Fact]
    public async Task Numeric_Only_Source_Does_Not_Authenticate_Or_Default_To_English()
    {
        // Arrange
        var credential = Credential();
        var handler = new DetectorHandler((_, _) => throw new InvalidOperationException("No provider request expected."));
        using var detector = new AzureDocumentLanguageDetector(
            new HttpClient(handler), credential.Object, new Uri("https://translator.example/"));

        // Act
        await Assert.ThrowsAsync<DocumentTranslationFailureException>(() => detector.DetectAsync("123 456 789"));

        // Assert
        Assert.Equal(0, handler.Calls);
        credential.VerifyNoOtherCalls();
    }

    private static Mock<TokenCredential> Credential()
    {
        var credential = new Mock<TokenCredential>();
        credential.Setup(value => value.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken("private-test-token", DateTimeOffset.UtcNow.AddHours(1)));
        return credential;
    }

    private sealed class DetectorHandler(Func<int, string, object> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }
        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            Assert.Equal(HttpMethod.Post, request.Method);
            Body = await request.Content!.ReadAsStringAsync(token);
            using var document = JsonDocument.Parse(Body);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response(document.RootElement.GetArrayLength(), Body)),
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
