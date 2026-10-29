using System.Net;
using System.Text.Json;
using DocIntel.Api.Diagnostics;
using DocIntel.Core.Authorization;
using DocIntel.Core.Features.AskAi;
using DocIntel.Core.Jobs;
using Microsoft.Azure.Cosmos;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class AskAiDataDiagnosticTests
{
    [Theory]
    [InlineData(400, "DataQueryRejected")]
    [InlineData(403, "DataAccessDenied")]
    [InlineData(429, "DataRateLimited")]
    [InlineData(503, "DataUnavailable")]
    public void Database_Status_Is_Not_Confused_With_AI_Provider_Status_Or_Exception_HResult(int status, string code)
    {
        // Arrange
        var exception = new InvalidOperationException("private-wrapper",
            new CosmosException("private-query", (HttpStatusCode)status, 1001, "private-activity", 1));

        // Act
        var details = AskAiDataFailure.FromException(exception);

        // Assert
        Assert.Equal(status, details!.HttpStatus);
        Assert.Equal(1001, details.SubStatus);
        Assert.Equal(code, AskAiFailureDiagnostics.GetCode(exception));
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(details));
        Assert.DoesNotContain("private-", AskAiFailureDiagnostics.GetMessage(code));
    }

    [Fact]
    public async Task Data_Check_Uses_Current_Actor_Scope_And_Only_An_Issued_Continuation_Without_Replaying_The_Question()
    {
        // Arrange
        var source = Source();
        var original = JsonSerializer.Serialize(source);
        var runs = Runs(source);
        var access = Access(source, new DocumentAccessScope(TenantRole.Reader,
            new HashSet<string>(StringComparer.Ordinal) { "allowed" }));
        var jobs = new Mock<IJobStore>(MockBehavior.Strict);
        var queries = new List<DocumentQuery>();
        jobs.Setup(store => store.SearchDocumentsAsync("tenant", It.IsAny<DocumentQuery>(), It.IsAny<CancellationToken>()))
            .Returns((string _, DocumentQuery query, CancellationToken _) =>
            {
                queries.Add(query);
                return Task.FromResult(new DocumentQueryPage(
                    [new("private-document", "private-filename", 1, 1)],
                    queries.Count == 1 ? "private-issued-token" : null));
            });
        using var output = new StringWriter();

        // Act
        var result = await AskAiDataDiagnostic.ExecuteAsync(
            "tenant", source.RunId, runs.Object, access.Object, jobs.Object, output);

        // Assert
        Assert.Equal(0, result);
        Assert.Equal(6, queries.Count);
        Assert.All(queries, query =>
        {
            Assert.Equal(1, query.PageSize);
            Assert.Equal(new[] { "allowed" },
                Assert.IsType<string[]>(DocumentQueryCompiler.Compile("tenant", query).Parameters["@authorizedJobIds"]));
        });
        Assert.Equal("private-issued-token", queries[1].ContinuationToken);
        Assert.Contains("\"variant\":\"Summary\",\"status\":\"Completed\"", output.ToString());
        Assert.DoesNotContain("private-", output.ToString());
        Assert.Equal(original, JsonSerializer.Serialize(source));
        access.Verify(resolver => resolver.ResolveAsync(source.Actor, "tenant", It.IsAny<CancellationToken>()),
            Times.Exactly(6));
        runs.Verify(store => store.GetRunAsync("tenant", source.RunId, It.IsAny<CancellationToken>()), Times.Once);
        runs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Data_Check_Reports_Actual_Cosmos_Status_Without_SQL_Diagnostics_Or_Source_Content()
    {
        // Arrange
        var source = Source();
        var jobs = new Mock<IJobStore>(MockBehavior.Strict);
        jobs.Setup(store => store.SearchDocumentsAsync("tenant", It.IsAny<DocumentQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("private-query", HttpStatusCode.BadRequest, 1001, "private-activity", 1));
        using var output = new StringWriter();

        // Act
        var result = await AskAiDataDiagnostic.ExecuteAsync("tenant", source.RunId, Runs(source).Object,
            Access(source, DocumentAccessScope.Unrestricted).Object, jobs.Object, output);

        // Assert
        Assert.Equal(1, result);
        Assert.Contains("\"httpStatus\":400,\"subStatus\":1001", output.ToString());
        Assert.Contains("DataQueryRejected", output.ToString());
        Assert.Contains("\"variant\":\"Summary\",\"status\":\"Failed\"", output.ToString());
        Assert.DoesNotContain("private-", output.ToString());
    }

    [Fact]
    public async Task Data_Check_Cannot_Bypass_A_Revoked_Actors_Access()
    {
        // Arrange
        var source = Source();
        var jobs = new Mock<IJobStore>(MockBehavior.Strict);
        using var output = new StringWriter();

        // Act
        var result = await AskAiDataDiagnostic.ExecuteAsync("tenant", source.RunId, Runs(source).Object,
            Access(source, DocumentAccessScope.Denied).Object, jobs.Object, output);

        // Assert
        Assert.Equal(2, result);
        Assert.Contains("AccessRevoked", output.ToString());
        jobs.VerifyNoOtherCalls();
    }

    private static AskAiRun Source() => new()
    {
        RunId = Guid.NewGuid().ToString("N"), TenantId = "tenant", Question = "private-question",
        Actor = new ActorIdentity(Guid.NewGuid(), Guid.NewGuid()), Status = JobStatus.Failed
    };

    private static Mock<IAskAiRunStore> Runs(AskAiRun source)
    {
        var store = new Mock<IAskAiRunStore>(MockBehavior.Strict);
        store.Setup(value => value.GetRunAsync(source.TenantId, source.RunId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        return store;
    }

    private static Mock<ITenantAccessResolver> Access(AskAiRun source, DocumentAccessScope scope)
    {
        var access = new Mock<ITenantAccessResolver>(MockBehavior.Strict);
        access.Setup(value => value.ResolveAsync(source.Actor, source.TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(scope);
        return access;
    }
}
