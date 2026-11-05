using System.Security.Cryptography;
using System.Text.Json;
using DocIntel.Api.Diagnostics;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using DocIntel.Core.Ocr;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class CommercialPortfolioTests
{
    [Fact]
    public async Task Portfolio_Has_Natural_Names_Detailed_Content_Multilingual_Sources_And_A_Real_Packet()
    {
        // Arrange
        var documents = CommercialPortfolioCorpus.Build();
        var extractor = new PdfPigLayoutExtractor();

        // Act / Assert
        Assert.Equal(14, documents.Count);
        Assert.Equal(37, documents.Sum(document => document.Pages));
        foreach (var document in documents)
        {
            Assert.DoesNotContain("demo", document.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sample", document.FileName, StringComparison.OrdinalIgnoreCase);
            var pages = await extractor.ExtractPagesAsync(document.Bytes, new());
            Assert.Equal(document.Pages, pages.Count);
            if (document.Key == "DVXScannedInvoice")
            {
                Assert.All(pages, page => Assert.True(String.IsNullOrWhiteSpace(page.BodyText)));
                Assert.All(pages, page => Assert.True(page.ImageAreaRatio > 0.95));
                Assert.All(pages, page => Assert.Empty(page.Words));
            }
            else
            {
                Assert.All(pages, page => Assert.True(page.BodyText.Length > 500));
            }
        }
        Assert.Equal(Assert.Single(documents, document => document.Key == "DVXQuoteAR").Bytes,
            Assert.Single(documents, document => document.Key == "DVXCopy").Bytes);
        var french = await extractor.ExtractPagesAsync(
            Assert.Single(documents, document => document.Key == "DVXQuoteFR").Bytes, new());
        Assert.Contains("184 600,00", String.Join("\n", french.Select(page => page.BodyText)));
        var packet = await extractor.ExtractPagesAsync(
            Assert.Single(documents, document => document.Key == "DVXPacket").Bytes, new());
        var text = String.Join("\n", packet.Select(page => page.BodyText));
        Assert.Contains("DVX-MSA-2026-1101", text);
        Assert.Contains("DVX-PO-2026-1101", text);
        Assert.Contains("DVX-AM-2026-1101", text);
    }

    [Fact]
    public void Processing_Definitions_Are_Idempotent_And_Do_Not_Change_Access_Channels_Retention_Or_Models()
    {
        // Arrange
        var initial = new TenantHarness { TenantId = "contoso-legal", DisplayName = "Deal Velocity", HarnessVersion = 6 };
        initial.Agent = initial.Agent with { EmailEnabled = true, EmailConfirmationRequired = false };
        initial.Capabilities[FeatureHarness.PromptColumnsKey].PromptColumns!.Columns.Add(new()
        {
            ColumnId = "existing", ColumnName = "Existing User Column", PromptInstructions = "Preserve this.",
            FilterMode = "All"
        });
        var before = JsonSerializer.Serialize(initial);

        // Act
        var once = CommercialPortfolioConfiguration.Apply(initial);
        var twice = CommercialPortfolioConfiguration.Apply(once);

        // Assert
        Assert.Equal(before, JsonSerializer.Serialize(initial));
        Assert.Equal(JsonSerializer.Serialize(once), JsonSerializer.Serialize(twice));
        Assert.Equal(initial.Agent, once.Agent);
        Assert.Equal(initial.DefaultModelId, once.DefaultModelId);
        Assert.Equal(initial.DecisionModelId, once.DecisionModelId);
        Assert.Equal(initial.AllowedModels, once.AllowedModels);
        Assert.Equal(JsonSerializer.Serialize(initial.Retention), JsonSerializer.Serialize(once.Retention));
        Assert.Equal(6, once.HarnessVersion);
        Assert.Equal("en", once.Capabilities[FeatureHarness.DocumentTranslationKey].DocumentTranslation!.TargetLanguage);
        Assert.Contains(once.Capabilities[FeatureHarness.IdentifyDocumentsKey].IdentifyDocuments!.Rules,
            rule => rule.DocumentName == "Commercial Quote");
        Assert.Contains(once.Capabilities[FeatureHarness.DataExtractionKey].DataExtraction!.Profiles,
            profile => profile.DocumentName == "Commercial Quote" && profile.Tables.Any());
        Assert.Contains(once.Capabilities[FeatureHarness.PromptColumnsKey].PromptColumns!.Columns,
            column => column.ColumnId == "existing");
        Assert.Contains(once.Capabilities[FeatureHarness.PromptColumnsKey].PromptColumns!.Columns,
            column => column.ColumnId == "dvxclose");
        Assert.Null(once.ValidateAndNormalizeDocumentCatalog());
    }

    [Theory]
    [InlineData("commercial-values")]
    [InlineData("commercial-billing")]
    [InlineData("commercial-terms")]
    [InlineData("commercial-risk")]
    public void Recording_Questions_Require_Original_Evidence_And_Do_Not_Claim_Unverified_Actions(string scenario)
    {
        // Arrange / Act
        var question = CommercialPortfolioQuestions.Question(scenario);

        // Assert
        Assert.True(question.Length > 300);
        Assert.True(question.Contains("cite", StringComparison.OrdinalIgnoreCase) ||
            question.Contains("citation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("send", question, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("demo", question, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(OcrMode.Never, OcrMode.Auto)]
    [InlineData(OcrMode.Auto, OcrMode.Auto)]
    [InlineData(OcrMode.Always, OcrMode.Always)]
    public void Portfolio_Enables_Needed_Ocr_Without_Overriding_An_Existing_Always_Policy(OcrMode before, OcrMode after)
    {
        // Arrange
        var tenant = new TenantHarness { TenantId = "contoso-legal", DisplayName = "Deal Velocity" };
        tenant.Ocr.Mode = before;

        // Act
        var configured = CommercialPortfolioConfiguration.Apply(tenant);

        // Assert
        Assert.Equal(before, tenant.Ocr.Mode);
        Assert.Equal(after, configured.Ocr.Mode);
    }

    [Theory]
    [InlineData("DVXQuoteES")]
    [InlineData("DVXPendingES")]
    [InlineData("DVXQuoteFR")]
    [InlineData("DVXPrivacyFR")]
    [InlineData("DVXQuoteDE")]
    public void Foreign_Documents_Require_Actual_Translation(string key)
    {
        // Arrange
        var job = Job("test", []);
        job.Segments = [new() { DetectedLanguage = "es", TargetLanguage = "en" }];

        // Act
        var failure = DealVelocityWorkspaceSetup.PortfolioFailure(key, job);

        // Assert
        Assert.Equal("TranslationNotCompleted", failure);
    }

    [Fact]
    public void Packet_Readiness_Requires_The_Actual_Three_Logical_Documents()
    {
        // Arrange
        var job = Job("packet", []);
        job.Segments = [new() { StartPage = 1, EndPage = 7 }];

        // Act
        var failure = DealVelocityWorkspaceSetup.PortfolioFailure("DVXPacket", job);

        // Assert
        Assert.Equal("PacketSplitIncomplete", failure);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Commercial_Question_Requires_Explicit_Confirmation_And_Live_Opt_In(bool confirmed, bool live)
    {
        // Arrange
        using var fixture = new QuestionFixture();

        // Act
        var result = await fixture.ExecuteAsync(confirmed: confirmed, live: live);

        // Assert
        Assert.Equal(2, result);
        fixture.Access.VerifyNoOtherCalls();
        fixture.Harnesses.VerifyNoOtherCalls();
        fixture.Models.VerifyNoOtherCalls();
        Assert.Empty(await fixture.Runs.ListRunsByTenantAsync("contoso-legal"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commercial_Question_Rejects_Non_Admin_And_Document_Restricted_Admins(bool restricted)
    {
        // Arrange
        using var fixture = new QuestionFixture();
        await fixture.PopulateAsync();
        fixture.Access.Setup(resolver => resolver.ResolveAsync(fixture.Actor, "contoso-legal",
            It.IsAny<CancellationToken>())).ReturnsAsync(restricted
                ? new DocumentAccessScope(TenantRole.Admin, new HashSet<string> { fixture.Sources[0].JobId })
                : new DocumentAccessScope(TenantRole.Reader, null));

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, result);
        Assert.Contains("AccessRevoked", fixture.Output.ToString());
        fixture.Models.VerifyNoOtherCalls();
        Assert.Single(await fixture.Runs.ListRunsByTenantAsync("contoso-legal"));
    }

    [Fact]
    public async Task Commercial_Question_Rejects_A_Pending_Or_Modified_Source_Before_Model_Execution()
    {
        // Arrange
        using var fixture = new QuestionFixture();
        await fixture.PopulateAsync();
        fixture.Sources[0].OriginalSha256 = new string('0', 64);

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(3, result);
        Assert.Contains("PortfolioNotProcessed", fixture.Output.ToString());
        fixture.Models.VerifyNoOtherCalls();
        Assert.Single(await fixture.Runs.ListRunsByTenantAsync("contoso-legal"));
    }

    [Fact]
    public async Task Completed_Web_Answer_Is_Reused_Without_Model_Usage_Or_Delivery()
    {
        // Arrange
        using var fixture = new QuestionFixture();
        await fixture.PopulateAsync();
        var completed = fixture.Answer(0, JobStatus.Completed);
        await fixture.Runs.TryCreateRunAsync(completed);

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(0, result);
        Assert.Contains(completed.RunId, fixture.Output.ToString());
        Assert.Equal(2, (await fixture.Runs.ListRunsByTenantAsync("contoso-legal")).Count);
        fixture.Models.VerifyNoOtherCalls();
        Assert.Null((await fixture.Runs.GetRunAsync("contoso-legal", completed.RunId))!.ChannelDelivery);
    }

    [Fact]
    public async Task Confirmed_Retry_Preserves_Failed_Attempts_And_Reuses_A_Completed_Answer()
    {
        // Arrange
        using var fixture = new QuestionFixture();
        await fixture.PopulateAsync();
        var failed = fixture.Answer(0, JobStatus.Failed);
        var completed = fixture.Answer(1, JobStatus.Completed);
        await fixture.Runs.TryCreateRunAsync(failed);
        await fixture.Runs.TryCreateRunAsync(completed);

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(0, result);
        Assert.Equal(JobStatus.Failed, (await fixture.Runs.GetRunAsync("contoso-legal", failed.RunId))!.Status);
        Assert.Equal(JobStatus.Completed, (await fixture.Runs.GetRunAsync("contoso-legal", completed.RunId))!.Status);
        fixture.Models.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Failed_Questions_Have_A_Three_Attempt_Ceiling_And_Are_Not_Overwritten()
    {
        // Arrange
        using var fixture = new QuestionFixture();
        await fixture.PopulateAsync();
        for (var attempt = 0; attempt < 3; attempt++)
            await fixture.Runs.TryCreateRunAsync(fixture.Answer(attempt, JobStatus.Failed));

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, result);
        Assert.Contains("RetryLimitReached", fixture.Output.ToString());
        Assert.Equal(4, (await fixture.Runs.ListRunsByTenantAsync("contoso-legal")).Count);
        fixture.Models.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_Completed_Answer_Without_The_Requested_Multi_Customer_Bar_Is_Not_Accepted()
    {
        // Arrange
        using var fixture = new QuestionFixture("commercial-values");
        await fixture.PopulateAsync();
        var completed = fixture.Answer(0, JobStatus.Completed);
        completed.Analyses = [new() { Visualization = "bar", Rows = [new()] }];
        await fixture.Runs.TryCreateRunAsync(completed);

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, result);
        Assert.Contains("\"status\":\"Failed\"", fixture.Output.ToString());
        fixture.Models.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Saved_Answers_With_Changed_Source_Evidence_Are_Not_Reused()
    {
        // Arrange
        using var fixture = new QuestionFixture();
        await fixture.PopulateAsync();
        var completed = fixture.Answer(0, JobStatus.Completed);
        completed.SourceVersions[fixture.Sources[0].JobId] = new string('0', 64);
        await fixture.Runs.TryCreateRunAsync(completed);

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, result);
        Assert.Contains("SourceChanged", fixture.Output.ToString());
        fixture.Models.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(158400, 0)]
    [InlineData(183000, 1)]
    public async Task Commercial_Chart_Uses_Original_Quoted_Value_Not_The_Amended_Contract(decimal arborValue, int expected)
    {
        // Arrange
        using var fixture = new QuestionFixture("commercial-values");
        await fixture.PopulateAsync();
        var completed = fixture.Answer(0, JobStatus.Completed);
        completed.Analyses = [new()
        {
            Visualization = "bar", XColumn = "customer", YColumn = "value",
            Rows =
            [
                new() { Cells = new() { ["customer"] = "Arbor Ridge Manufacturing LLC", ["value"] = arborValue } },
                new() { Cells = new() { ["customer"] = "Summit Harbor Logistics Ltd", ["value"] = 226800m } }
            ]
        }];
        await fixture.Runs.TryCreateRunAsync(completed);

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(expected, result);
        fixture.Models.VerifyNoOtherCalls();
    }

    private static DocumentJob Job(string id, byte[] bytes) => new()
    {
        JobId = id, TenantId = "contoso-legal", TenantDisplayName = "Deal Velocity", AppliedHarnessVersion = 7,
        FileName = "Commercial-Document.pdf", OriginalSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
        Status = JobStatus.Completed, HasDevelopmentResults = false
    };

    private sealed class QuestionFixture : IDisposable
    {
        public ActorIdentity Actor { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        public string BasisId { get; } = "mail-" + new string('A', 64);
        public InMemoryAskAiRunStore Runs { get; } = new();
        public InMemoryJobStore Jobs { get; } = new();
        public List<DocumentJob> Sources { get; } = [];
        public Mock<ITenantAccessResolver> Access { get; } = new(MockBehavior.Strict);
        public Mock<IHarnessStore> Harnesses { get; } = new(MockBehavior.Strict);
        public Mock<IModelCatalog> Models { get; } = new(MockBehavior.Strict);
        public StringWriter Output { get; } = new();
        private string Scenario { get; }

        public QuestionFixture(string scenario = "commercial-billing")
        {
            Scenario = scenario;
            Access.Setup(resolver => resolver.ResolveAsync(Actor, "contoso-legal", It.IsAny<CancellationToken>()))
                .ReturnsAsync(DocumentAccessScope.Unrestricted);
            Harnesses.Setup(store => store.GetTenantAsync("contoso-legal", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TenantHarness { TenantId = "contoso-legal", DisplayName = "Deal Velocity" });
        }

        public async Task PopulateAsync()
        {
            await Runs.TryCreateRunAsync(new AskAiRun
            {
                RunId = BasisId, TenantId = "contoso-legal", Actor = Actor, Status = JobStatus.Completed,
                Question = "Saved authorized request.", ReplyChannel = new() { ChannelType = AskAiChannelType.Email },
                PendingEmail = new PendingEmailQuestion
                {
                    Profile = "private", Sender = "fictional@example.test", MessageId = "private",
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
                }
            });
            foreach (var document in CommercialPortfolioCorpus.Build())
            {
                var job = new DocumentJob
                {
                    JobId = DealVelocityWorkspaceSetup.JobId(document, CommercialPortfolioCorpus.Version),
                    TenantId = "contoso-legal", TenantDisplayName = "Deal Velocity", AppliedHarnessVersion = 7,
                    FileName = document.FileName, Status = JobStatus.Completed, SubmittedBy = Actor,
                    OriginalSha256 = Convert.ToHexString(SHA256.HashData(document.Bytes)), TotalPages = document.Pages,
                    HasDevelopmentResults = false, Segments = [new() { BodyText = "Authorized original source." }]
                };
                await Jobs.UpsertJobAsync(job);
                Sources.Add(job);
            }
        }

        public AskAiRun Answer(int attempt, JobStatus status) => new()
        {
            RunId = CommercialPortfolioQuestions.RunId("contoso-legal", Actor, Scenario, Sources, attempt),
            TenantId = "contoso-legal", Actor = Actor, Question = CommercialPortfolioQuestions.Question(Scenario),
            Status = status, Answer = status == JobStatus.Completed ? "Source-backed answer [1]." : null,
            ReplyChannel = new() { ChannelType = AskAiChannelType.Web },
            SourceVersions = Sources.ToDictionary(job => job.JobId, AskAiConversationContext.Fingerprint),
            Citations = [new(1, Sources[0].JobId, Sources[0].FileName, "Commercial Offer", 0,
                "Commercial Quote", "1-3", "Arbor Ridge", "", "", "USD", "", "Original source.",
                "/workspace.html", "/api/document.pdf")]
        };

        public Task<int> ExecuteAsync(bool confirmed = true, bool live = true) =>
            CommercialPortfolioQuestions.ExecuteAsync("contoso-legal", BasisId, Scenario, confirmed, live,
                Harnesses.Object, Runs, Access.Object, Jobs, Models.Object, new(), Output);

        public void Dispose() => Output.Dispose();
    }
}
