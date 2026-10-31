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
    [Theory]
    [InlineData(0.5, true)]
    [InlineData(0.79, true)]
    [InlineData(0.8, false)]
    [InlineData(1.0, false)]
    public async Task Lower_Supported_Scores_Are_Usable_But_Only_Reliable_Scores_Allow_A_Skip(
        double score, bool automatic)
    {
        // Arrange
        var handler = new DetectorHandler((count, _) => Enumerable.Range(0, count)
            .Select(_ => new { language = "fr", score, isTranslationSupported = true }).ToArray());
        using var detector = new AzureDocumentLanguageDetector(
            new HttpClient(handler), Credential().Object, new Uri("https://translator.example/"));

        // Act
        var result = await detector.DetectAsync("Le document source contient les conditions de livraison.");

        // Assert
        Assert.Equal("fr", result.Language);
        Assert.Equal(automatic, result.UseAutomaticSourceLanguage);
        Assert.Equal(1, result.UsableSampleCount);
        Assert.Equal(score, Assert.Single(result.Samples).Score);
        Assert.True(result.Samples[0].IsUsable);
    }

    [Theory]
    [InlineData("en", 0.3, true, 0.65)]
    [InlineData("en", 0.3, true, 0.99)]
    [InlineData("und", 0.9, true, 0.65)]
    [InlineData("xx", 1.0, false, 0.65)]
    public async Task An_Uncertain_Or_Unsupported_Chunk_Does_Not_Veto_Another_Usable_Chunk(
        string otherLanguage, double otherScore, bool supported, double goodScore)
    {
        // Arrange
        var handler = new DetectorHandler((count, _) => Enumerable.Range(0, count).Select(index => new
        {
            language = index == 0 ? "en" : otherLanguage,
            score = index == 0 ? goodScore : otherScore,
            isTranslationSupported = index == 0 || supported
        }).ToArray());
        using var detector = new AzureDocumentLanguageDetector(
            new HttpClient(handler), Credential().Object, new Uri("https://translator.example/"));

        // Act
        var result = await detector.DetectAsync(new string('a', 1500) + new string('b', 1500));

        // Assert
        Assert.Equal(!supported ? "mul" : "en", result.Language);
        Assert.True(result.UseAutomaticSourceLanguage);
        Assert.Equal(1, result.UsableSampleCount);
        Assert.Equal(2, result.Samples.Count);
        Assert.Equal(new[] { 0, 1 }, result.Samples.Select(sample => sample.SampleIndex));
        Assert.Equal(1500, result.Samples[0].CharacterCount);
        Assert.False(result.Samples[1].IsUsable);
        Assert.DoesNotContain(new string('a', 100), JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public async Task Invalid_Provider_Scores_Are_Not_Treated_As_Low_Confidence_Evidence(double score)
    {
        // Arrange
        var handler = new DetectorHandler((count, _) => Enumerable.Range(0, count)
            .Select(_ => new { language = "en", score, isTranslationSupported = true }).ToArray());
        using var detector = new AzureDocumentLanguageDetector(
            new HttpClient(handler), Credential().Object, new Uri("https://translator.example/"));

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => detector.DetectAsync("Actual source text."));
    }

    [Fact]
    public async Task A_Reliably_Identified_Unsupported_Language_Is_Not_Mislabeled_As_Undetermined()
    {
        // Arrange
        var handler = new DetectorHandler((count, _) => Enumerable.Range(0, count)
            .Select(_ => new { language = "la", score = 0.99, isTranslationSupported = false }).ToArray());
        using var detector = new AzureDocumentLanguageDetector(
            new HttpClient(handler), Credential().Object, new Uri("https://translator.example/"));

        // Act
        var result = await detector.DetectAsync("Lorem ipsum dolor sit amet, consectetur adipiscing elit.");

        // Assert
        Assert.Equal("la", result.Language);
        Assert.Equal(0, result.UsableSampleCount);
        Assert.False(Assert.Single(result.Samples).IsTranslationSupported);
        Assert.Equal(0.99, result.Samples[0].Score);
    }

    [Fact]
    public async Task Missing_Provider_Result_Fields_Fail_Explicitly_Not_As_An_Uncertain_Chunk()
    {
        // Arrange
        var handler = new DetectorHandler((count, _) => Enumerable.Range(0, count)
            .Select(_ => new { language = "en", score = 0.65 }).ToArray());
        using var detector = new AzureDocumentLanguageDetector(
            new HttpClient(handler), Credential().Object, new Uri("https://translator.example/"));

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => detector.DetectAsync("Actual source text."));
    }

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
        Assert.InRange(body.RootElement.GetArrayLength(), 1, 30);
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
        Assert.True(result.UseAutomaticSourceLanguage);
        Assert.Equal(result.Samples.Count, result.UsableSampleCount);
    }

    [Fact]
    public async Task Foreign_Sections_Outside_Leading_Middle_And_Trailing_Windows_Are_Not_Skipped()
    {
        // Arrange
        var handler = new DetectorHandler((_, body) =>
        {
            using var request = JsonDocument.Parse(body);
            return request.RootElement.EnumerateArray().Select(item => new
            {
                language = item.GetProperty("Text").GetString()!.Contains('b') ? "es" : "en",
                score = 1.0, isTranslationSupported = true
            }).ToArray();
        });
        using var detector = new AzureDocumentLanguageDetector(
            new HttpClient(handler), Credential().Object, new Uri("https://translator.example/"));
        var text = new string('a', 3000) + new string('b', 3000) + new string('a', 12000);

        // Act
        var result = await detector.DetectAsync(text);

        // Assert
        Assert.Equal("mul", result.Language);
    }

    [Fact]
    public async Task Large_Source_Is_Fully_Covered_By_Multiple_Bounded_Detect_Requests()
    {
        // Arrange
        var handler = new DetectorHandler((count, _) => Enumerable.Range(0, count)
            .Select(_ => new { language = "en", score = 1.0, isTranslationSupported = true }).ToArray());
        using var detector = new AzureDocumentLanguageDetector(
            new HttpClient(handler), Credential().Object, new Uri("https://translator.example/"));
        var source = String.Concat(Enumerable.Range(0, 1000)
            .Select(index => $"English source paragraph {index:D4}, with distinct evidence and complete language coverage.\n"));

        // Act
        var result = await detector.DetectAsync(source);

        // Assert
        Assert.Equal("en", result.Language);
        Assert.True(handler.Calls > 1);
        var sent = new List<string>();
        foreach (var body in handler.Bodies)
        {
            using var document = JsonDocument.Parse(body);
            var texts = document.RootElement.EnumerateArray().Select(item => item.GetProperty("Text").GetString()!).ToArray();
            Assert.InRange(texts.Length, 1, 30);
            Assert.InRange(texts.Sum(text => text.Length), 1, 45_000);
            sent.AddRange(texts);
        }
        Assert.Equal(source, String.Concat(sent));
        Assert.Equal(Enumerable.Range(0, sent.Count), result.Samples.Select(sample => sample.SampleIndex));
    }

    [Fact]
    public async Task Source_Beyond_Whole_Text_Detection_Budget_Is_Unknown_Not_A_Sampled_English_Skip()
    {
        // Arrange
        var credential = Credential();
        var handler = new DetectorHandler((_, _) => throw new InvalidOperationException("No provider request expected."));
        using var detector = new AzureDocumentLanguageDetector(
            new HttpClient(handler), credential.Object, new Uri("https://translator.example/"));

        // Act
        var failure = await Assert.ThrowsAsync<DocumentTranslationFailureException>(() =>
            detector.DetectAsync(new string('a', 1_000_001)));

        // Assert
        Assert.Equal("LanguageUndetermined", failure.Code);
        Assert.Null(failure.LanguageDetection);
        Assert.Equal(0, handler.Calls);
        credential.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1501)]
    [InlineData(3001)]
    public async Task Chunking_Does_Not_Classify_A_Tiny_Trailing_Word_Fragment_As_A_Separate_Language(int length)
    {
        // Arrange
        var handler = new DetectorHandler((count, _) => Enumerable.Range(0, count)
            .Select(_ => new { language = "en", score = 1.0, isTranslationSupported = true }).ToArray());
        using var detector = new AzureDocumentLanguageDetector(
            new HttpClient(handler), Credential().Object, new Uri("https://translator.example/"));

        // Act
        await detector.DetectAsync(new string('a', length));

        // Assert
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.All(body.RootElement.EnumerateArray(), item =>
            Assert.InRange(item.GetProperty("Text").GetString()!.Length, 300, 1500));
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
        Assert.NotNull(failure.LanguageDetection);
        Assert.Equal(0, failure.LanguageDetection.UsableSampleCount);
        Assert.Equal(0.3, Assert.Single(failure.LanguageDetection.Samples).Score);
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
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            Assert.Equal(HttpMethod.Post, request.Method);
            Body = await request.Content!.ReadAsStringAsync(token);
            Bodies.Add(Body);
            using var document = JsonDocument.Parse(Body);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response(document.RootElement.GetArrayLength(), Body)),
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
