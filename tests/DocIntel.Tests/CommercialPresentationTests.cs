using System.Security.Cryptography;
using System.Text.Json;
using DocIntel.Api.Channels;
using DocIntel.Api.Diagnostics;
using DocIntel.Core.Authorization;
using DocIntel.Core.Channels;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Features.PromptColumns;
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
    private const string ConditionalTransfer =
        "A proposed transfer outside the approved regions requires documented approval.";
    private const string ConditionalQuote = "\"A proposed transfer outside the approved regions\"";
    private const string ColumnBrief =
        "- **Answer:** Privacy clearance needs confirmation.\n" +
        "- **Evidence:** \"Privacy approval is pending.\"\n" +
        "- **Next:** Privacy should confirm the stated prerequisite.";

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

    [Fact]
    public void The_Previous_Owned_Privacy_Policy_Is_Updated_Without_Overwriting_User_Policies()
    {
        // Arrange
        var source = CommercialPortfolioPresentation.Configure(new()
            { TenantId = "contoso-legal", DisplayName = "Deal Velocity" });
        source.Capabilities[FeatureHarness.PromptColumnsKey].PromptColumns!.Columns[0].PromptInstructions =
            CommercialPortfolioPresentation.LegacyPrivacyInstructions;

        // Act
        var updated = CommercialPortfolioPresentation.Configure(source);

        // Assert
        Assert.Equal(CommercialPortfolioPresentation.LegacyPrivacyInstructions,
            source.Capabilities[FeatureHarness.PromptColumnsKey].PromptColumns!.Columns[0].PromptInstructions);
        Assert.Contains(CommercialPortfolioPresentation.ScopeClarification,
            updated.Capabilities[FeatureHarness.PromptColumnsKey].PromptColumns!.Columns[0].PromptInstructions);
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

    [Fact]
    public async Task Already_Validated_Briefs_Are_Reused_When_Only_The_Privacy_Policy_Changes()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        fixture.Tenant = CommercialPortfolioPresentation.Configure(fixture.Tenant);
        fixture.Tenant.Capabilities[FeatureHarness.PromptColumnsKey].PromptColumns!.Columns[0].PromptInstructions =
            CommercialPortfolioPresentation.LegacyPrivacyInstructions;
        foreach (var job in await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"))
        {
            foreach (var slice in job.Segments) slice.InsightSummary = Brief;
            job.PacketSummary = Brief;
            job.ExecutionTrace.Add(new("RefreshCommercialPresentation", true, true, null, 1,
                CommercialPortfolioPresentation.InputFingerprint(job, fixture.Tenant, fixture.Models, legacyPrivacy: true)));
        }
        fixture.Response = (task, _) => task == "insight"
            ? throw new InvalidOperationException("Validated insights must not be regenerated.")
            : null;

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(0, result);
        Assert.Equal(16, fixture.Calls);
        Assert.All(await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"),
            job => Assert.All(job.Segments, slice => Assert.Equal(Brief, slice.InsightSummary)));
    }

    [Theory]
    [InlineData("DVXAgreement", "REVIEW", "None")]
    [InlineData("DVXAgreement", "HOLD", "PrivacyDecisionMismatch")]
    [InlineData("DVXPacket", "REVIEW", "None")]
    [InlineData("DVXPacket", "HOLD", "PrivacyDecisionMismatch")]
    public void Conditional_Transfer_Restrictions_Are_Not_Mislabeled_As_Current_Production_Blocks(
        string key, string decision, string expected)
    {
        // Arrange
        var job = QualityJob(decision, ConditionalQuote);
        job.Segments[0].BodyText = ConditionalTransfer;

        // Act
        var failure = CommercialPortfolioPresentation.QualityFailure(key, job);

        // Assert
        Assert.Equal(expected, failure);
    }

    [Fact]
    public async Task One_Source_Based_Privacy_Refinement_Can_Resolve_A_Conditional_Clause_Misclassification()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        var firstAgreement = true;
        fixture.Response = (task, input) =>
        {
            if (task != "privacy" || !firstAgreement ||
                !input.Contains("SOURCE_KEY=DVXAgreement", StringComparison.Ordinal)) return null;
            firstAgreement = false;
            return JsonSerializer.Serialize(new { result = Privacy("HOLD", ConditionalQuote) });
        };

        // Act
        var result = await fixture.ExecuteAsync();

        // Assert
        Assert.Equal(0, result);
        Assert.Equal(33, fixture.Calls);
        var agreementId = DealVelocityWorkspaceSetup.JobId(CommercialPortfolioCorpus.Build()
            .Single(document => document.Key == "DVXAgreement"), CommercialPortfolioCorpus.Version);
        var agreement = await fixture.Jobs.GetJobAsync(agreementId, tenantId: "contoso-legal");
        var outcome = Assert.Single(agreement!.Segments[0].PromptColumnResults,
            item => item.ColumnId == CommercialPortfolioPresentation.PrivacyColumnId);
        Assert.Contains("- **Decision:** REVIEW", outcome.ResultText);
        Assert.Contains(ConditionalQuote, outcome.ResultText);
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

    [Fact]
    public void Prompt_Configuration_Preserves_Every_Setting_Except_The_Five_Requested_Instructions()
    {
        // Arrange
        var source = CommercialPortfolioPresentation.Configure(CommercialPortfolioConfiguration.Apply(new()
            { TenantId = "contoso-legal", DisplayName = "Deal Velocity" }));
        var columns = source.Capabilities[FeatureHarness.PromptColumnsKey].PromptColumns!.Columns;
        var prior = columns.ToDictionary(column => column.ColumnId, column => column.PromptInstructions);
        foreach (var column in columns.Where(column => column.ColumnId != "dvxprivacy"))
            column.PromptInstructions = "An old verbose report.";
        var before = JsonSerializer.Serialize(source);

        // Act
        var configured = CommercialPortfolioPresentation.ConfigurePromptColumns(source);
        var repeated = CommercialPortfolioPresentation.ConfigurePromptColumns(configured);

        // Assert
        Assert.Equal(before, JsonSerializer.Serialize(source));
        Assert.Equal(JsonSerializer.Serialize(configured), JsonSerializer.Serialize(repeated));
        var updated = configured.Capabilities[FeatureHarness.PromptColumnsKey].PromptColumns!.Columns;
        Assert.Equal(prior["dvxprivacy"], updated.Single(column => column.ColumnId == "dvxprivacy").PromptInstructions);
        foreach (var column in updated.Where(column => column.ColumnId != "dvxprivacy"))
        {
            Assert.Contains("at most 60 words", column.PromptInstructions);
            column.PromptInstructions = "An old verbose report.";
        }
        Assert.Equal(before, JsonSerializer.Serialize(configured));
    }

    [Fact]
    public async Task Prompt_Refresh_Only_Changes_Selected_Columns_And_Reuses_Checkpointed_Results()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        foreach (var job in await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"))
        {
            foreach (var slice in job.Segments)
                slice.PromptColumnResults.Add(Result("dvxprivacy", "Existing privacy result.", slice.SegmentIndex));
            job.PromptColumnResults = job.Segments.SelectMany(slice => slice.PromptColumnResults).ToList();
        }
        var before = (await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"))
            .ToDictionary(job => job.JobId, job => JsonSerializer.Serialize(job));

        // Act
        Assert.Equal(0, await fixture.ExecuteAsync(promptColumns: true));
        var calls = fixture.Calls;
        Assert.Equal(0, await fixture.ExecuteAsync(promptColumns: true));
        Assert.Equal(0, await fixture.ExecuteAsync(apply: false, confirmed: false, live: false, promptColumns: true));

        // Assert
        Assert.Equal(64, calls);
        Assert.Equal(calls, fixture.Calls);
        foreach (var job in await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"))
        {
            var original = JsonSerializer.Deserialize<DocumentJob>(before[job.JobId])!;
            Assert.Equal(original.PacketSummary, job.PacketSummary);
            Assert.Equal(original.OriginalSha256, job.OriginalSha256);
            Assert.Equal(original.LayoutSha256, job.LayoutSha256);
            Assert.Equal(original.ExecutionAttempts, job.ExecutionAttempts);
            Assert.Equal(JsonSerializer.Serialize(original.DataExtractions), JsonSerializer.Serialize(job.DataExtractions));
            foreach (var slice in job.Segments)
            {
                var previous = original.Segments.Single(value => value.SegmentIndex == slice.SegmentIndex);
                Assert.Equal(previous.BodyText, slice.BodyText);
                Assert.Equal(previous.TranslatedText, slice.TranslatedText);
                Assert.Equal(previous.InsightSummary, slice.InsightSummary);
                Assert.Equal(JsonSerializer.Serialize(previous.DataExtraction), JsonSerializer.Serialize(slice.DataExtraction));
                Assert.Equal(previous.PromptColumnResults.Single(result => result.ColumnId == "dvxprivacy"),
                    slice.PromptColumnResults.Single(result => result.ColumnId == "dvxprivacy"));
                Assert.Equal(6, slice.PromptColumnResults.Count);
            }
        }
        Assert.DoesNotContain("Privacy approval is pending.", fixture.Output.ToString());
    }

    [Fact]
    public async Task Prompt_Inspection_Does_Not_Configure_Or_Generate_Results()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        var before = JsonSerializer.Serialize(fixture.Tenant);

        // Act
        var result = await fixture.ExecuteAsync(apply: false, confirmed: false, live: false, promptColumns: true);

        // Assert
        Assert.Equal(3, result);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Tenant));
        Assert.Equal(0, fixture.Calls);
    }

    [Fact]
    public async Task Accepted_Column_Checkpoints_Survive_A_Later_Save_Conflict_And_Are_Not_Recomputed()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        var writes = 0;
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        store.Setup(service => service.GetJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), "contoso-legal"))
            .Returns((string id, CancellationToken token, string? tenant) => fixture.Jobs.GetJobAsync(id, token, tenant));
        store.Setup(service => service.UpsertJobAsync(It.IsAny<DocumentJob>(), It.IsAny<CancellationToken>()))
            .Returns((DocumentJob job, CancellationToken token) =>
            {
                if (Interlocked.Increment(ref writes) == 3)
                    throw new InvalidOperationException("A concurrent write won.");
                return fixture.Jobs.UpsertJobAsync(job, token);
            });

        // Act
        var interrupted = await fixture.ExecuteAsync(jobs: store.Object, promptColumns: true);
        var saved = (await fixture.Jobs.ListJobsByTenantAsync("contoso-legal"))
            .Sum(job => job.ExecutionTrace.Count(step => step.ExecutorName == "RefreshPromptColumnBrief"));
        var calls = fixture.Calls;
        var completed = await fixture.ExecuteAsync(promptColumns: true);

        // Assert
        Assert.Equal(1, interrupted);
        Assert.InRange(saved, 1, 69);
        Assert.Equal(0, completed);
        Assert.True(fixture.Calls - calls < 64);
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
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
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
                    Interlocked.Increment(ref _calls);
                    var input = messages.Last(message => message.Role == ChatRole.User).Text!;
                    var instructions = options.Instructions ?? String.Empty;
                    var insight = instructions.Contains("[TASK:DOCUMENT_INSIGHT]", StringComparison.Ordinal);
                    var brief = instructions.Contains("**Answer:**", StringComparison.Ordinal);
                    var conditional = input.Contains("SOURCE_KEY=DVXAgreement", StringComparison.Ordinal) ||
                        input.Contains("SOURCE_KEY=DVXPacket", StringComparison.Ordinal) &&
                        input.Contains("pages=1-4", StringComparison.Ordinal);
                    var administrative = new[] { "DVXInvoice", "DVXScannedInvoice", "DVXOrder", "DVXAmendment", "DVXPendingES" }
                        .Any(key => input.Contains("SOURCE_KEY=" + key, StringComparison.Ordinal)) ||
                        input.Contains("SOURCE_KEY=DVXPacket", StringComparison.Ordinal) && !conditional;
                    var decision = conditional ? "REVIEW" : administrative ? "NOT APPLICABLE" : "HOLD";
                    var evidence = decision == "NOT APPLICABLE"
                        ? "Not stated in this document." : conditional ? ConditionalQuote : "\"Privacy approval is pending.\"";
                    var json = Response?.Invoke(insight ? "insight" : brief ? "brief" : "privacy", input) ??
                        (insight ? JsonSerializer.Serialize(new { summary = Brief }) :
                            brief ? JsonSerializer.Serialize(new { result = conditional
                                ? ColumnBrief.Replace("\"Privacy approval is pending.\"", ConditionalQuote, StringComparison.Ordinal)
                                : ColumnBrief }) :
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
                    var conditional = document.Key == "DVXAgreement" || document.Key == "DVXPacket" && index == 0;
                    var source = "SOURCE_KEY=" + document.Key + "\n" +
                        (conditional ? ConditionalTransfer : "Privacy approval is pending.");
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

        public Task<int> ExecuteAsync(
            bool apply = true, bool confirmed = true, bool live = true, IJobStore? jobs = null, bool promptColumns = false) =>
            CommercialPortfolioPresentation.ExecuteAsync("contoso-legal", BasisId, apply, confirmed, live,
                Harnesses.Object, Runs.Object, Access.Object, jobs ?? Jobs, Artifacts.Object, Models, Output,
                promptColumns: promptColumns);

        public void Dispose() => Output.Dispose();
        private static TenantHarness Copy(TenantHarness tenant) =>
            JsonSerializer.Deserialize<TenantHarness>(JsonSerializer.SerializeToUtf8Bytes(tenant))!;
    }
}
