using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Jobs;
using Xunit;

namespace DocIntel.Tests;

public sealed class DocumentQueryTests
{
    [Fact]
    public void User_Text_Is_Bound_As_Parameters_Not_SQL()
    {
        // Arrange
        const string value = "O'XXO'); DELETE FROM c --";
        var query = new DocumentQuery
        {
            SearchText = value,
            Entities = [new DocumentEntityCondition("Customer Name", value)]
        };

        // Act
        var compiled = DocumentQueryCompiler.Compile("tenant-one", query);

        // Assert
        Assert.DoesNotContain(value, compiled.Sql);
        Assert.Equal(value, compiled.Parameters["@text"]);
        Assert.Equal(value, compiled.Parameters["@value0"]);
        Assert.Equal("tenant-one", compiled.Parameters["@tenantId"]);
        Assert.Contains("c.tenantId = @tenantId AND c.status = @status", compiled.Sql);
        Assert.DoesNotContain("SELECT *", compiled.Sql);
    }

    [Fact]
    public async Task Search_Requires_All_Conditions_And_Does_Not_Return_Other_Tenants()
    {
        // Arrange
        var store = new InMemoryJobStore();
        await store.UpsertJobAsync(CreateJob("one", "tenant-one", "USD"));
        await store.UpsertJobAsync(CreateJob("two", "tenant-one", "EUR"));
        await store.UpsertJobAsync(CreateJob("three", "tenant-two", "USD"));
        var query = new DocumentQuery
        {
            Entities =
            [
                new DocumentEntityCondition("Customer Name", "Woodgrove"),
                new DocumentEntityCondition("Billing Currency", "USD")
            ]
        };

        // Act
        var page = await store.SearchDocumentsAsync("tenant-one", query);

        // Assert
        Assert.Equal("one", Assert.Single(page.Documents).JobId);
        Assert.Null(page.ContinuationToken);
    }

    [Fact]
    public async Task No_Matches_Remains_Empty()
    {
        // Arrange
        var store = new InMemoryJobStore();
        await store.UpsertJobAsync(CreateJob("one", "tenant-one", "USD"));

        // Act
        var page = await store.SearchDocumentsAsync("tenant-one", new DocumentQuery { SearchText = "missing" });

        // Assert
        Assert.Empty(page.Documents);
        Assert.Null(page.ContinuationToken);
    }

    [Fact]
    public async Task Search_Pages_Without_Claiming_The_First_Page_Is_The_Whole_Corpus()
    {
        // Arrange
        var store = new InMemoryJobStore();
        await store.UpsertJobAsync(CreateJob("one", "tenant-one", "USD"));
        await store.UpsertJobAsync(CreateJob("two", "tenant-one", "USD"));

        // Act
        var first = await store.SearchDocumentsAsync("tenant-one", new DocumentQuery { PageSize = 1 });
        var second = await store.SearchDocumentsAsync(
            "tenant-one", new DocumentQuery { PageSize = 1, ContinuationToken = first.ContinuationToken });

        // Assert
        Assert.Single(first.Documents);
        Assert.NotNull(first.ContinuationToken);
        Assert.Single(second.Documents);
        Assert.NotEqual(first.Documents[0].JobId, second.Documents[0].JobId);
        Assert.Null(second.ContinuationToken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Oversized_Or_Empty_Page_Requests_Are_Rejected(int pageSize)
    {
        // Arrange
        var query = new DocumentQuery { PageSize = pageSize };

        // Act
        var exception = Assert.Throws<ArgumentException>(() => DocumentQueryCompiler.Compile("tenant-one", query));

        // Assert
        Assert.Contains("page size", exception.Message);
    }

    [Fact]
    public async Task Complete_File_Insights_Are_Searchable_Without_Broadening_Tenant_Or_Document_Access()
    {
        // Arrange
        var store = new InMemoryJobStore();
        var first = CreateJob("one", "tenant-one", "USD");
        first.PacketSummary = "A complete-file RenewalRisk insight";
        await store.UpsertJobAsync(first);
        var other = CreateJob("two", "tenant-two", "USD");
        other.PacketSummary = first.PacketSummary;
        await store.UpsertJobAsync(other);
        var query = new DocumentQuery { SearchText = "renewalrisk" }
            .WithAccess(new HashSet<string>(StringComparer.Ordinal) { "one" });

        // Act
        var page = await store.SearchDocumentsAsync("tenant-one", query);
        var compiled = DocumentQueryCompiler.Compile("tenant-one", query);

        // Assert
        Assert.Equal("one", Assert.Single(page.Documents).JobId);
        Assert.Contains("CONTAINS(c.packetSummary, @text, true)", compiled.Sql);
        Assert.Contains("ARRAY_CONTAINS(@authorizedJobIds, c.jobId)", compiled.Sql);
        Assert.Equal("renewalrisk", compiled.Parameters["@text"]);
        Assert.False(DocumentQueryCompiler.Matches(first, "tenant-two", query));
    }

    private static DocumentJob CreateJob(string id, string tenantId, string currency) => new()
    {
        JobId = id,
        TenantId = tenantId,
        TenantDisplayName = "Test",
        AppliedHarnessVersion = 1,
        FileName = "Agreement.pdf",
        Status = JobStatus.Completed,
        Segments =
        [
            new DocumentSlice
            {
                StartPage = 1, EndPage = 1, BodyText = "Woodgrove agreement",
                ExtractedEntities =
                [
                    new ExtractedEntity("Customer Name", "Woodgrove", "Party"),
                    new ExtractedEntity("Billing Currency", currency, "Financial")
                ]
            }
        ]
    };
}
