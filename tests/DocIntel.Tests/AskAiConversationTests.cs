using System.Text.Json;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Jobs;
using Microsoft.Extensions.AI;
using Xunit;

namespace DocIntel.Tests;

public sealed class AskAiConversationTests
{
    [Fact]
    public async Task Follow_Ups_Use_Verified_Server_History_And_Keep_It_Separate_From_Fresh_Evidence()
    {
        // Arrange
        var fixture = await FixtureAsync();

        // Act
        var history = await AskAiConversationContext.ReadHistoryAsync(
            fixture.FollowUp, fixture.Runs, fixture.Jobs, DocumentAccessScope.Unrestricted);
        var messages = AskAiConversationContext.BuildMessages(fixture.FollowUp, history);

        // Assert
        Assert.Equal("prior", Assert.Single(history).RunId);
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Contains("untrusted_document_content", messages[0].Text);
        Assert.Contains("previousQuestion", messages[0].Text);
        Assert.Equal(fixture.FollowUp.Question, messages[1].Text);
    }

    [Fact]
    public async Task Another_Actors_Completed_Run_Cannot_Be_Used_As_Conversation_Context()
    {
        // Arrange
        var fixture = await FixtureAsync();
        fixture.FollowUp.Actor = new(Guid.NewGuid(), Guid.NewGuid());

        // Act / Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AskAiConversationContext.ReadHistoryAsync(
            fixture.FollowUp, fixture.Runs, fixture.Jobs, DocumentAccessScope.Unrestricted));
    }

    [Fact]
    public async Task A_Run_From_Another_Workspace_Is_Not_Conversation_Context()
    {
        // Arrange
        var fixture = await FixtureAsync();
        var other = new AskAiRun
        {
            RunId = "follow", TenantId = "other", Question = "Show that by month", PreviousRunId = "prior"
        };

        // Act / Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AskAiConversationContext.ReadHistoryAsync(
            other, fixture.Runs, fixture.Jobs, DocumentAccessScope.Unrestricted));
    }

    [Fact]
    public async Task Revoked_Document_Access_Invalidates_Prior_Context_Before_Model_Execution()
    {
        // Arrange
        var fixture = await FixtureAsync();
        var restricted = new DocumentAccessScope(TenantRole.Reader, new HashSet<string>());

        // Act / Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AskAiConversationContext.ReadHistoryAsync(
            fixture.FollowUp, fixture.Runs, fixture.Jobs, restricted));
    }

    [Fact]
    public async Task Changed_Source_Content_Cannot_Be_Used_With_Old_Analytical_Context()
    {
        // Arrange
        var fixture = await FixtureAsync();
        fixture.Source.Segments[0].BodyText = "Changed source facts";
        await fixture.Jobs.UpsertJobAsync(fixture.Source);

        // Act / Assert
        await Assert.ThrowsAsync<AskAiSourceChangedException>(() => AskAiConversationContext.ReadHistoryAsync(
            fixture.FollowUp, fixture.Runs, fixture.Jobs, DocumentAccessScope.Unrestricted));
    }

    [Fact]
    public async Task Old_Cited_Answers_Without_Source_Versions_Require_A_Fresh_Conversation()
    {
        // Arrange
        var fixture = await FixtureAsync();
        fixture.Parent.SourceVersions.Clear();
        await fixture.Runs.UpsertRunAsync(fixture.Parent);

        // Act / Assert
        await Assert.ThrowsAsync<AskAiSourceChangedException>(() => AskAiConversationContext.ReadHistoryAsync(
            fixture.FollowUp, fixture.Runs, fixture.Jobs, DocumentAccessScope.Unrestricted));
    }

    [Fact]
    public async Task Analytical_Answers_And_Source_Versions_Survive_Server_Snapshot_Round_Trips()
    {
        // Arrange
        var fixture = await FixtureAsync();
        var dataset = AskAiAnalysisTests.Invoices("sum", "bar", "supplier",
            ("Contoso", "100.25", "USD", "2026-01-01"));
        var analysis = AskAiAnalysisEngine.Compute("a1", dataset.Request, dataset.Evidence);
        analysis.Rows[0].CitationNumbers = [1];
        fixture.Parent.Analyses = [analysis];
        await fixture.Runs.UpsertRunAsync(fixture.Parent);

        // Act
        var restored = await fixture.Runs.GetRunAsync("workspace", "prior");
        using var json = JsonSerializer.SerializeToDocument(restored);

        // Assert
        Assert.Equal("bar", Assert.Single(restored!.Analyses).Visualization);
        Assert.Equal(1, restored.Analyses[0].Rows[0].CitationNumbers[0]);
        Assert.Equal(100.25m, json.RootElement.GetProperty("analyses")[0]
            .GetProperty("rows")[0].GetProperty("cells").GetProperty("value").GetDecimal());
        Assert.Equal(AskAiConversationContext.Fingerprint(fixture.Source), restored.SourceVersions["source"]);
    }

    internal static async Task<(InMemoryAskAiRunStore Runs, InMemoryJobStore Jobs,
        AskAiRun Parent, AskAiRun FollowUp, DocumentJob Source)> FixtureAsync()
    {
        var actor = new ActorIdentity(Guid.NewGuid(), Guid.NewGuid());
        var source = new DocumentJob
        {
            JobId = "source", TenantId = "workspace", TenantDisplayName = "Workspace", AppliedHarnessVersion = 1,
            FileName = "invoice.pdf", Status = JobStatus.Completed,
            Segments = [new() { SegmentIndex = 0, StartPage = 1, EndPage = 1, BodyText = "Contoso invoice 100 USD" }]
        };
        var jobs = new InMemoryJobStore();
        await jobs.UpsertJobAsync(source);
        var parent = new AskAiRun
        {
            RunId = "prior", TenantId = "workspace", Actor = actor, Question = "Compare supplier spend",
            Status = JobStatus.Completed, Answer = "Contoso spend is 100 USD [1].", ReferencedJobIds = ["source"],
            SourceVersions = new() { ["source"] = AskAiConversationContext.Fingerprint(source) },
            Citations = [new(1, "source", "invoice.pdf", "invoice", 0, "Invoice", "p.1",
                "Contoso", "", "", "USD", "", "Contoso invoice 100 USD", "/?tenant=workspace", "/source.pdf")]
        };
        var runs = new InMemoryAskAiRunStore();
        await runs.UpsertRunAsync(parent);
        return (runs, jobs, parent, new AskAiRun
        {
            RunId = "follow", TenantId = "workspace", Actor = actor, Question = "Show that by month",
            PreviousRunId = "prior"
        }, source);
    }
}
