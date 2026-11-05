using System.Security.Cryptography;
using System.Text.Json;
using DocIntel.Api.Channels;
using DocIntel.Api.Diagnostics;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Harness;
using DocIntel.Core.Jobs;
using DocIntel.Core.Models;
using DocIntel.Core.Storage;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class CommercialPresentationTests
{
    private const string Brief =
        "- **What matters:** A customer services document.\n" +
        "- **Key terms:** The document defines the commercial scope.\n" +
        "- **Watch out:** Privacy clearance needs confirmation.\n" +
        "- **Next action:** Recommend Privacy confirm the production prerequisites.";

    [Fact]
    public void Configuration_Is_Idempotent_And_Preserves_Every_Unrelated_Setting_And_User_Column()
    {
        // Arrange
        var source = CommercialPortfolioConfiguration.Apply(new()
            { TenantId = "contoso-legal", DisplayName = "Deal Velocity", HarnessVersion = 7 });
        source.Capabilities[FeatureHarness.DocumentInsightKey].CustomSystemPrompt = "An old lengthy report.";
        source.Capabilities[FeatureHarness.DocumentInsightKey].ModelIdOverride = "phi-4";
        var original = JsonSerializer.Serialize(source);

        // Act
        var configured = CommercialPortfolioPresentation.Configure(source);
        var repeated = CommercialPortfolioPresentation.Configure(configured);

        // Assert
        Assert.Equal(original, JsonSerializer.Serialize(source));
        Assert.Equal(JsonSerializer.Serialize(configured), JsonSerializer.Serialize(repeated));
        var insight = configured.Capabilities[FeatureHarness.DocumentInsightKey];
        Assert.Null(insight.CustomSystemPrompt);
        Assert.Equal("phi-4", insight.ModelIdOverride);
        Assert.Equal(DocumentInsightConfig.DefaultSummaryInstructions, insight.DocumentInsight!.SummaryInstructions);
        var columns = configured.Capabilities[FeatureHarness.PromptColumnsKey].PromptColumns!.Columns;
        Assert.Equal(CommercialPortfolioPresentation.PrivacyColumnId, columns[0].ColumnId);
        Assert.Single(columns, column => column.ColumnId == CommercialPortfolioPresentation.PrivacyColumnId);
        columns.RemoveAt(0);
        configured.Capabilities[FeatureHarness.DocumentInsightKey] = source.Capabilities[FeatureHarness.DocumentInsightKey];
        Assert.Equal(original, JsonSerializer.Serialize(configured));
    }

    [Fact]
    public void An_Unrelated_Column_Using_The_Reserved_Id_Is_Not_Overwritten()
    {
        // Arrange
        var tenant = new TenantHarness { TenantId = "contoso-legal", DisplayName = "Deal Velocity" };
        tenant.Capabilities[FeatureHarness.PromptColumnsKey].PromptColumns!.Columns.Add(new()
        {
            ColumnId = CommercialPortfolioPresentation.PrivacyColumnId,
            ColumnName = "User-authored policy", PromptInstructions = "Keep this policy."
        });
        var before = JsonSerializer.Serialize(tenant);

        // Act / Assert
        Assert.Throws<HarnessConflictException>(() => CommercialPortfolioPresentation.Configure(tenant));
        Assert.Equal(before, JsonSerializer.Serialize(tenant));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Mutations_Require_Separate_Confirmation_And_Live_Opt_In(bool confirmed, bool live)
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();

        // Act
        var result = await fixture.ExecuteAsync(confirmed: confirmed, live: live);

        // Assert
        Assert.Equal(2, result);
        fixture.Runs.VerifyNoOtherCalls();
        fixture.Harnesses.VerifyNoOtherCalls();
        Assert.Equal(0, fixture.Calls);
    }

    [Fact]
    public async Task Inspection_Does_Not_Configure_Or_Generate_Anything()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        var before = JsonSerializer.Serialize(fixture.Tenant);

        // Act
        var result = await fixture.ExecuteAsync(apply: false, confirmed: false, live: false);

        // Assert
        Assert.Equal(3, result);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Tenant));
        Assert.Equal(0, fixture.Calls);
        fixture.Harnesses.Verify(store => store.UpsertTenantAsync(
            It.IsAny<TenantHarness>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("\"status\":\"Pending\"", fixture.Output.ToString());
    }

    [Fact]
    public async Task Changed_Original_Bytes_Stop_The_Refresh_Before_Configuration_Or_Model_Calls()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        fixture.Artifacts.Setup(store => store.GetOriginalPdfAsync("contoso-legal", It.IsAny<string>(),
            It.IsAny<CancellationToken>())).ReturnsAsync([1, 2, 3]);
        var before = JsonSerializer.Serialize(fixture.Tenant);

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, result);
        Assert.Contains("SourceOrGenerationUnavailable", fixture.Output.ToString());
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Tenant));
        Assert.Equal(0, fixture.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_The_Current_Unrestricted_Workspace_Administrator_Can_Refresh(bool restricted)
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        fixture.Access.Setup(service => service.ResolveAsync(fixture.Actor, "contoso-legal",
            It.IsAny<CancellationToken>())).ReturnsAsync(restricted
                ? new DocumentAccessScope(TenantRole.Admin, new HashSet<string> { "one" })
                : new DocumentAccessScope(TenantRole.Reader, null));

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(2, result);
        Assert.Equal(0, fixture.Calls);
        Assert.Contains("CurrentAdministratorRequired", fixture.Output.ToString());
    }

    [Fact]
    public async Task Refresh_Preserves_Sources_Extraction_Translation_And_Other_Columns_And_Reuses_Completed_Results()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        var prior = (await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"))
            .ToDictionary(job => job.JobId, job => JsonSerializer.Serialize(job));

        // Act
        Assert.Equal(0, await fixture.ExecuteAsync());
        var calls = fixture.Calls;
        Assert.Equal(0, await fixture.ExecuteAsync());
        Assert.Equal(0, await fixture.ExecuteAsync(apply: false, confirmed: false, live: false));

        // Assert
        Assert.Equal(32, calls);
        Assert.Equal(calls, fixture.Calls);
        Assert.Equal(8, fixture.Tenant.HarnessVersion);
        foreach (var job in await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"))
        {
            var original = JsonSerializer.Deserialize<DocumentJob>(prior[job.JobId])!;
            Assert.NotEqual(AskAiConversationContext.Fingerprint(original), AskAiConversationContext.Fingerprint(job));
            Assert.Equal(original.OriginalSha256, job.OriginalSha256);
            Assert.Equal(original.OriginalPdfLocation, job.OriginalPdfLocation);
            Assert.Equal(original.LayoutSha256, job.LayoutSha256);
            Assert.Equal(original.SubmittedBy, job.SubmittedBy);
            Assert.Equal(original.ExecutionAttempts, job.ExecutionAttempts);
            Assert.Equal(original.AppliedHarnessVersion, job.AppliedHarnessVersion);
            Assert.Equal(JsonSerializer.Serialize(original.ProcessingHarness), JsonSerializer.Serialize(job.ProcessingHarness));
            Assert.Equal(JsonSerializer.Serialize(original.DataExtractions), JsonSerializer.Serialize(job.DataExtractions));
            Assert.Equal(JsonSerializer.Serialize(original.ExtractedEntities), JsonSerializer.Serialize(job.ExtractedEntities));
            Assert.Equal(JsonSerializer.Serialize(original.PromptColumnResults),
                JsonSerializer.Serialize(job.PromptColumnResults.Where(result =>
                    result.ColumnId != CommercialPortfolioPresentation.PrivacyColumnId)));
            foreach (var slice in job.Segments)
            {
                var previous = original.Segments.Single(item => item.SegmentIndex == slice.SegmentIndex);
                Assert.Equal(previous.BodyText, slice.BodyText);
                Assert.Equal(previous.TranslatedText, slice.TranslatedText);
                Assert.Equal(previous.TranslatedPdfBlobUri, slice.TranslatedPdfBlobUri);
                Assert.Equal(previous.StartPage, slice.StartPage);
                Assert.Equal(previous.EndPage, slice.EndPage);
                Assert.Equal(JsonSerializer.Serialize(previous.DataExtraction), JsonSerializer.Serialize(slice.DataExtraction));
                Assert.Equal(Brief, slice.InsightSummary);
            }
        }
        Assert.DoesNotContain("Privacy approval is pending.", fixture.Output.ToString());
        Assert.DoesNotContain("customer services document", fixture.Output.ToString());
        Assert.DoesNotContain("fictional@example.test", fixture.Output.ToString());
    }

    [Fact]
    public async Task A_Long_Generated_Brief_Is_Not_Published_Or_Truncated_To_Make_It_Pass()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        fixture.Response = (task, _) => task == "insight"
            ? JsonSerializer.Serialize(new { summary = Brief + new string(' ', 2) + String.Join(" ", Enumerable.Repeat("extra", 100)) })
            : null;
        var before = (await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"))
            .ToDictionary(job => job.JobId, job => JsonSerializer.Serialize(job));

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, result);
        Assert.Contains("InsightNotConcise", fixture.Output.ToString());
        foreach (var job in await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"))
            Assert.Equal(before[job.JobId], JsonSerializer.Serialize(job));
    }

    [Fact]
    public async Task One_Bounded_Style_Rewrite_Can_Produce_A_Valid_Short_Brief_Without_Truncation()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        var firstInsight = true;
        fixture.Response = (task, _) =>
        {
            if (task != "insight" || !firstInsight) return null;
            firstInsight = false;
            return JsonSerializer.Serialize(new
            {
                summary = Brief + " " + String.Join(" ", Enumerable.Repeat("extra", 100))
            });
        };

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(0, result);
        Assert.Equal(33, fixture.Calls);
        Assert.All(await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"),
            job => Assert.All(job.Segments, slice => Assert.Equal(Brief, slice.InsightSummary)));
    }

    [Theory]
    [InlineData("NOT APPLICABLE", "\"Not stated in this document.\"", "Not stated in this document.")]
    [InlineData("REVIEW", "\u201cNot stated in this document.\u201d", "Not stated in this document.")]
    [InlineData("HOLD", "\"Not stated in this document.\"", "\"Not stated in this document.\"")]
    [InlineData("READY", "\"Not stated in this document.\"", "\"Not stated in this document.\"")]
    [InlineData("NOT APPLICABLE", "\"A statement that is not sourced.\"", "\"A statement that is not sourced.\"")]
    public void Only_An_Explicit_Absence_Marker_For_Non_Clearance_Decisions_Is_Canonicalized(
        string decision, string evidence, string expected)
    {
        // Arrange
        var result = Result(CommercialPortfolioPresentation.PrivacyColumnId, Privacy(decision, evidence), 0);

        // Act
        var normalized = CommercialPortfolioPresentation.NormalizeMissingEvidence(result);

        // Assert
        Assert.Contains("- **Evidence:** " + expected, normalized.ResultText);
        Assert.Contains("- **Decision:** " + decision, normalized.ResultText);
    }

    [Fact]
    public async Task Quoted_Absence_Markers_Are_Stored_As_Absence_Not_Fabricated_Source_Quotations()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        fixture.Response = (task, input) => task == "privacy" &&
            (input.Contains("SOURCE_KEY=DVXInvoice", StringComparison.Ordinal) ||
             input.Contains("SOURCE_KEY=DVXScannedInvoice", StringComparison.Ordinal))
            ? JsonSerializer.Serialize(new { result = Privacy("NOT APPLICABLE", "\"Not stated in this document.\"") })
            : null;

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(0, result);
        var invoiceId = DealVelocityWorkspaceSetup.JobId(CommercialPortfolioCorpus.Build()
            .Single(document => document.Key == "DVXInvoice"), CommercialPortfolioCorpus.Version);
        var invoice = await fixture.Jobs.GetJobAsync(invoiceId, tenantId: "contoso-legal");
        var outcome = Assert.Single(invoice!.Segments[0].PromptColumnResults,
            item => item.ColumnId == CommercialPortfolioPresentation.PrivacyColumnId);
        Assert.Contains("- **Evidence:** Not stated in this document.", outcome.ResultText);
        Assert.DoesNotContain("\"Not stated in this document.\"", outcome.ResultText);
    }

    [Theory]
    [InlineData("HOLD", "\"Privacy approval is pending.\"", "None")]
    [InlineData("READY", "\"Privacy approval is pending.\"", "PrivacyDecisionMismatch")]
    [InlineData("HOLD", "\"The customer has approved everything.\"", "PrivacyEvidenceMismatch")]
    [InlineData("HOLD", "Not stated in this document.", "PrivacyEvidenceMismatch")]
    [InlineData("HOLD", "\"   \"", "PrivacyEvidenceMismatch")]
    [InlineData("Probably compliant", "\"Privacy approval is pending.\"", "PrivacyDecisionMismatch")]
    public void The_French_Privacy_Example_Requires_An_Actual_Hold_And_Verbatim_Evidence(
        string decision, string evidence, string expected)
    {
        // Arrange
        var job = QualityJob(decision, evidence);

        // Act
        var failure = CommercialPortfolioPresentation.QualityFailure("DVXPrivacyFR", job);

        // Assert
        Assert.Equal(expected, failure);
    }

    [Theory]
    [InlineData("NOT APPLICABLE", "None")]
    [InlineData("REVIEW", "PrivacyDecisionMismatch")]
    public void A_Billing_Document_Is_Not_Mislabeled_As_A_Privacy_Violation(string decision, string expected)
    {
        // Arrange
        var job = QualityJob(decision, "Not stated in this document.");

        // Act
        var failure = CommercialPortfolioPresentation.QualityFailure("DVXInvoice", job);

        // Assert
        Assert.Equal(expected, failure);
    }

    [Fact]
    public void Privacy_Output_Cannot_Hide_A_Long_Report_Inside_The_Short_Decision_Format()
    {
        // Arrange
        var job = QualityJob("HOLD", "\"Privacy approval is pending.\"");
        var result = job.Segments[0].PromptColumnResults[0];
        job.Segments[0].PromptColumnResults[0] = result with
        {
            ResultText = result.ResultText + " " + String.Join(" ", Enumerable.Repeat("extra", 80))
        };

        // Act
        var failure = CommercialPortfolioPresentation.QualityFailure("DVXPrivacyFR", job);

        // Assert
        Assert.Equal("PrivacyNotConcise", failure);
    }

    [Fact]
    public async Task Revoked_Access_Stops_Publication_Of_In_Flight_Results()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        fixture.AfterCall = () => fixture.Access.Setup(service =>
            service.ResolveAsync(fixture.Actor, "contoso-legal", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentAccessScope(TenantRole.Reader, null));

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(2, result);
        Assert.All(await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"),
            job => Assert.Equal("Original insight.", job.PacketSummary));
    }

    [Fact]
    public async Task Concurrent_Workspace_Changes_Are_Not_Overwritten()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        fixture.AfterCall = () => fixture.Tenant.HarnessVersion++;

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(1, result);
        Assert.Contains("WorkspaceChanged", fixture.Output.ToString());
        Assert.All(await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"),
            job => Assert.Equal("Original insight.", job.PacketSummary));
    }

    [Fact]
    public async Task Document_Save_Conflicts_Do_Not_Overwrite_The_Previous_Results()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        store.Setup(service => service.GetJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), "contoso-legal"))
            .Returns((string id, CancellationToken token, string? tenant) => fixture.Jobs.GetJobAsync(id, token, tenant));
        store.Setup(service => service.UpsertJobAsync(It.IsAny<DocumentJob>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("A concurrent write won."));

        // Act
        var result = await fixture.ExecuteAsync(jobs: store.Object);

        // Assert
        Assert.Equal(1, result);
        Assert.Contains("DocumentChanged", fixture.Output.ToString());
        Assert.All(await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"),
            job => Assert.Equal("Original insight.", job.PacketSummary));
    }

    private static DocumentJob QualityJob(string decision, string evidence)
    {
        var slice = new DocumentSlice
        {
            StartPage = 1, EndPage = 1, BodyText = "Privacy approval is pending.", InsightSummary = Brief
        };
        slice.PromptColumnResults = [Result(CommercialPortfolioPresentation.PrivacyColumnId,
            Privacy(decision, evidence), 0)];
        return new() { JobId = "one", TenantId = "contoso-legal", TenantDisplayName = "Deal Velocity",
            AppliedHarnessVersion = 7, FileName = "agreement.pdf", Segments = [slice] };
    }

    private static string Privacy(string decision, string evidence) =>
        $"- **Decision:** {decision}\n- **Why:** Documented privacy clearance needs attention.\n" +
        $"- **Evidence:** {evidence}\n- **Next:** Recommend Privacy confirm production readiness.";

    private static PromptColumnResult Result(string id, string text, int index) =>
        new(id, id, "Original user instruction.", "All Documents (No Entity Filter)", true, [],
            "Evaluated", text, false, "source.pdf", 0, String.Empty, "phi-4", index,
            "Commercial Quote", "1-1", DateTimeOffset.UtcNow);

    private sealed class Fixture : IDisposable
    {
        public ActorIdentity Actor { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        public string BasisId { get; } = "mail-" + new string('A', 64);
        public TenantHarness Tenant { get; set; }
        public Mock<IHarnessStore> Harnesses { get; } = new(MockBehavior.Strict);
        public Mock<IAskAiRunStore> Runs { get; } = new(MockBehavior.Strict);
        public Mock<ITenantAccessResolver> Access { get; } = new(MockBehavior.Strict);
        public InMemoryJobStore Jobs { get; } = new();
        public Mock<IArtifactStore> Artifacts { get; } = new(MockBehavior.Strict);
        public ModelCatalog Models { get; }
        public StringWriter Output { get; } = new();
        public int Calls { get; private set; }
        public Func<string, string, string?>? Response { get; set; }
        public Action? AfterCall { get; set; }
        private readonly Dictionary<string, byte[]> _originals = new();

        private Fixture()
        {
            Tenant = CommercialPortfolioConfiguration.Apply(new()
            {
                TenantId = "contoso-legal", DisplayName = "Deal Velocity", HarnessVersion = 7,
                DefaultModelId = "phi-4", AllowedModels = ["phi-4"]
            });
            Tenant.Capabilities[FeatureHarness.DocumentInsightKey].ModelIdOverride = "phi-4";
            Tenant.Capabilities[FeatureHarness.PromptColumnsKey].ModelIdOverride = "phi-4";
            Tenant.Capabilities[FeatureHarness.DocumentInsightKey].CustomSystemPrompt = "Original report instructions.";
            Runs.Setup(store => store.GetRunAsync("contoso-legal", BasisId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AskAiRun
                {
                    RunId = BasisId, TenantId = "contoso-legal", Question = "Saved question.", Actor = Actor,
                    Status = JobStatus.Completed,
                    PendingEmail = new PendingEmailQuestion { Profile = "private", Sender = "fictional@example.test",
                        MessageId = "private", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) },
                    ReplyChannel = new AskAiReplyChannel { ChannelType = AskAiChannelType.Email }
                });
            Access.Setup(service => service.ResolveAsync(Actor, "contoso-legal", It.IsAny<CancellationToken>()))
                .ReturnsAsync(DocumentAccessScope.Unrestricted);
            Harnesses.Setup(store => store.GetTenantAsync("contoso-legal", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Copy(Tenant));
            Harnesses.Setup(store => store.UpsertTenantAsync(It.IsAny<TenantHarness>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((TenantHarness updated, CancellationToken _) =>
                {
                    if (updated.HarnessVersion != Tenant.HarnessVersion) throw new HarnessConflictException();
                    Tenant = Copy(updated);
                    Tenant.HarnessVersion++;
                    return Copy(Tenant);
                });
            Artifacts.Setup(store => store.GetOriginalPdfAsync("contoso-legal", It.IsAny<string>(),
                It.IsAny<CancellationToken>())).ReturnsAsync((string _, string id, CancellationToken _) =>
                    _originals.GetValueOrDefault(id));
            var client = new Mock<IChatClient>();
            client.Setup(chat => chat.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .Returns((IEnumerable<ChatMessage> messages, ChatOptions options, CancellationToken _) =>
                {
                    Calls++;
                    var input = messages.Last(message => message.Role == ChatRole.User).Text!;
                    var insight = options.Instructions!.Contains("[TASK:DOCUMENT_INSIGHT]", StringComparison.Ordinal);
                    var decision = input.Contains("SOURCE_KEY=DVXInvoice", StringComparison.Ordinal) ||
                        input.Contains("SOURCE_KEY=DVXScannedInvoice", StringComparison.Ordinal)
                        ? "NOT APPLICABLE" : "HOLD";
                    var evidence = decision == "NOT APPLICABLE"
                        ? "Not stated in this document." : "\"Privacy approval is pending.\"";
                    var json = Response?.Invoke(insight ? "insight" : "privacy", input) ??
                        (insight ? JsonSerializer.Serialize(new { summary = Brief }) :
                            JsonSerializer.Serialize(new { result = Privacy(decision, evidence) }));
                    AfterCall?.Invoke();
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
                });
            Models = new ModelCatalog(customClients: new Dictionary<string, IChatClient> { ["phi-4"] = client.Object });
        }

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            foreach (var document in CommercialPortfolioCorpus.Build())
            {
                var id = DealVelocityWorkspaceSetup.JobId(document, CommercialPortfolioCorpus.Version);
                fixture._originals.Add(id, document.Bytes);
                var slices = document.Key == "DVXPacket" ? new[] { (1, 4), (5, 5), (6, 7) }
                    : [(1, document.Pages)];
                var job = new DocumentJob
                {
                    JobId = id, TenantId = "contoso-legal", TenantDisplayName = "Deal Velocity",
                    AppliedHarnessVersion = 7, FileName = document.FileName, SubmittedBy = fixture.Actor,
                    OriginalSha256 = Convert.ToHexString(SHA256.HashData(document.Bytes)),
                    Status = JobStatus.Completed, HasDevelopmentResults = false, TotalPages = document.Pages,
                    OriginalPdfLocation = "original-private", LayoutSha256 = new string('a', 64),
                    ProcessingHarness = Copy(fixture.Tenant), ExecutionAttempts = 2, PacketSummary = "Original insight."
                };
                for (var index = 0; index < slices.Length; index++)
                {
                    var source = "SOURCE_KEY=" + document.Key + "\nPrivacy approval is pending.";
                    var slice = new DocumentSlice
                    {
                        SegmentIndex = index, StartPage = slices[index].Item1, EndPage = slices[index].Item2,
                        IdentifiedDocumentName = "Commercial Quote", BodyText = source,
                        WasTranslated = document.Key is "DVXQuoteES" or "DVXPendingES" or "DVXQuoteFR" or
                            "DVXPrivacyFR" or "DVXQuoteDE",
                        TranslatedText = source, TargetLanguage = "en", InsightSummary = "Original document insight.",
                        DataExtraction = new("Commercial Quote", String.Empty, "1-1", [], []),
                        ExtractedEntities = [new("Customer Name", "Original Customer", "Organization")],
                        PromptColumnResults = [Result("dvxclose", "Original closing result.", index),
                            Result("dvxterms", "Original terms result.", index)]
                    };
                    job.Segments.Add(slice);
                }
                job.PromptColumnResults = job.Segments.SelectMany(slice => slice.PromptColumnResults).ToList();
                await fixture.Jobs.UpsertJobAsync(job);
            }
            return fixture;
        }

        public Task<int> ExecuteAsync(bool apply = true, bool confirmed = true, bool live = true, IJobStore? jobs = null) =>
            CommercialPortfolioPresentation.ExecuteAsync("contoso-legal", BasisId, apply, confirmed, live,
                Harnesses.Object, Runs.Object, Access.Object, jobs ?? Jobs, Artifacts.Object, Models, Output);

        public void Dispose() => Output.Dispose();
        private static TenantHarness Copy(TenantHarness tenant) =>
            JsonSerializer.Deserialize<TenantHarness>(JsonSerializer.SerializeToUtf8Bytes(tenant))!;
    }
}
