using System.Text.Json;
using DocIntel.Core.Authorization;
using DocIntel.Core.Jobs;
using Xunit;

namespace DocIntel.Tests;

public sealed class DocumentAccessTests
{
    [Theory]
    [InlineData(TenantRole.Processor, false, true)]
    [InlineData(TenantRole.Processor, true, false)]
    [InlineData(TenantRole.Admin, false, true)]
    [InlineData(TenantRole.Admin, true, false)]
    [InlineData(TenantRole.Reader, false, false)]
    [InlineData(TenantRole.Reviewer, false, false)]
    public void Processing_Requires_A_Processing_Role_And_Unrestricted_Document_Scope(
        TenantRole role, bool restricted, bool expected)
    {
        // Arrange
        var scope = new DocumentAccessScope(role, restricted ? new HashSet<string> { "allowed" } : null);

        // Act
        var canProcess = scope.CanProcess;

        // Assert
        Assert.Equal(expected, canProcess);
    }

    [Fact]
    public void Scope_Combines_Tenant_Roles_With_Explicit_Document_Grants()
    {
        // Arrange
        var scope = new DocumentAccessScope(TenantRole.Reader,
            new HashSet<string>(StringComparer.Ordinal) { "allowed" });

        // Act / Assert
        Assert.True(scope.CanRead);
        Assert.False(scope.CanAdmin);
        Assert.False(scope.CanProcess);
        Assert.True(scope.CanReadDocument("allowed"));
        Assert.False(scope.CanReadDocument("other"));
        Assert.False(DocumentAccessScope.Denied.CanReadDocument("allowed"));
    }

    [Fact]
    public void Model_Query_Arguments_Cannot_Supply_Server_Owned_Document_Grants()
    {
        // Arrange
        var query = JsonSerializer.Deserialize<DocumentQuery>(
            "{\"authorizedJobIds\":[\"forbidden\"],\"pageSize\":20}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        // Act
        var scoped = query.WithAccess(new HashSet<string> { "allowed" });
        var compiled = DocumentQueryCompiler.Compile("tenant", scoped);

        // Assert
        Assert.Contains("ARRAY_CONTAINS(@authorizedJobIds, c.jobId)", compiled.Sql);
        Assert.Equal(["allowed"], Assert.IsType<string[]>(compiled.Parameters["@authorizedJobIds"]));
        Assert.DoesNotContain("authorizedJobIds", JsonSerializer.Serialize(scoped));
    }

    [Fact]
    public async Task An_Empty_Document_Grant_Never_Expands_To_All_Tenant_Documents()
    {
        // Arrange
        var store = new InMemoryJobStore();
        await store.UpsertJobAsync(new DocumentJob
        {
            JobId = "one",
            TenantId = "tenant",
            TenantDisplayName = "Test",
            AppliedHarnessVersion = 1,
            FileName = "one.pdf",
            Status = JobStatus.Completed
        });

        // Act
        var result = await store.SearchDocumentsAsync("tenant", new DocumentQuery().WithAccess(new HashSet<string>()));

        // Assert
        Assert.Empty(result.Documents);
    }

    [Fact]
    public void Persisted_Actor_Identities_Use_The_Same_Json_Contract_As_The_Api()
    {
        // Arrange
        var actor = new ActorIdentity(Guid.NewGuid(), Guid.NewGuid());
        var json = JsonSerializer.Serialize(actor);

        // Act
        var roundTrip = JsonSerializer.Deserialize<ActorIdentity>(json);

        // Assert
        Assert.Contains("\"directoryTenantId\"", json);
        Assert.Contains("\"objectId\"", json);
        Assert.Equal(actor, roundTrip);
    }
}
