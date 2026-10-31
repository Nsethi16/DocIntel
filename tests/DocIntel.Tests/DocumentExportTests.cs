using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocIntel.Core.Authorization;
using DocIntel.Core.Export;
using DocIntel.Core.Features.DocumentSplitting;
using DocIntel.Core.Jobs;
using DocIntel.Core.Review;
using DocIntel.Core.Storage;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using Xunit;

namespace DocIntel.Tests;

public sealed class DocumentExportTests
{
    [Fact]
    public void Machine_Field_Count_Exceeding_Its_Explicit_Limit_Returns_An_Export_Limit_Error()
    {
        // Arrange
        var job = new DocumentJob
        {
            JobId = "table", TenantId = "workspace", TenantDisplayName = "Workspace", FileName = "table.pdf",
            AppliedHarnessVersion = 1, Status = JobStatus.Completed,
            Segments = [new() { SegmentIndex = 0, StartPage = 1, EndPage = 1,
                DataExtraction = new("Table", String.Empty, "1-1",
                    Enumerable.Range(0, 100001).Select(index => new ExtractedFieldValue($"Field-{index}", "Value")).ToArray(), []) }]
        };

        // Act / Assert
        Assert.Throws<ExportLimitException>(() => DocumentExporter.MachineSnapshot(job, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public void Machine_Exports_Preserve_Table_Fields_Above_The_Retired_Review_Limit(string format)
    {
        // Arrange
        var rows = Enumerable.Range(0, 201).Select(row => new ExtractedTableRow(
            Enumerable.Range(0, 10).Select(column => new ExtractedFieldValue(
                $"Column-{column}", $"Value-{row}-{column}")).ToArray())).ToArray();
        var job = new DocumentJob
        {
            JobId = "table", TenantId = "workspace", TenantDisplayName = "Workspace", FileName = "table.pdf",
            AppliedHarnessVersion = 1, Status = JobStatus.Completed,
            Segments = [new() { SegmentIndex = 0, StartPage = 1, EndPage = 1,
                DataExtraction = new("Table", String.Empty, "1-1", [], [new("Rows", rows)]) }]
        };

        // Act
        var export = DocumentExporter.MachineSnapshot(job, DateTimeOffset.UtcNow);
        var bytes = DocumentExporter.Write(export, format);

        // Assert
        Assert.Equal(2010, export.Fields.Count);
        Assert.Equal("Value-200-9", export.Fields[^1].EffectiveValue);
        if (format == "json")
        {
            using var document = JsonDocument.Parse(bytes);
            Assert.Equal(2010, document.RootElement.GetProperty("fields").GetArrayLength());
        }
        else if (format == "csv")
        {
            Assert.Contains("Value-200-9", Encoding.UTF8.GetString(bytes));
            Assert.Equal(2011, Encoding.UTF8.GetString(bytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        }
        else
        {
            using var stream = new MemoryStream(bytes);
            using var workbook = SpreadsheetDocument.Open(stream, false);
            Assert.Empty(new OpenXmlValidator().Validate(workbook));
            Assert.Equal(2011, Assert.Single(workbook.WorkbookPart!.WorksheetParts).Worksheet.Descendants<Row>().Count());
        }
    }

    [Theory]
    [InlineData("=SUM(A1:A2)")]
    [InlineData("+1")]
    [InlineData("-123")]
    [InlineData("@lookup")]
    [InlineData("   =2+2")]
    [InlineData("\ufeff=2+2")]
    [InlineData("\u200b=2+2")]
    [InlineData("\tplain")]
    [InlineData("\rplain")]
    [InlineData("\nplain")]
    public void Csv_Cells_Are_Text_Not_Spreadsheet_Formula_Commands(string value)
    {
        // Act
        var cell = DocumentExporter.CsvCell(value);

        // Assert
        Assert.StartsWith("\"'", cell);
        Assert.EndsWith("\"", cell);
    }

    [Fact]
    public void Csv_Quoting_Preserves_Commas_Quotes_And_Embedded_Newlines()
    {
        // Act / Assert
        Assert.Equal("\"a,\"\"b\"\"\nnext\"", DocumentExporter.CsvCell("a,\"b\"\nnext"));
        Assert.Throws<ExportLimitException>(() => DocumentExporter.CsvCell("invalid\0value"));
    }

    [Fact]
    public async Task Reviewed_Exports_Use_Separate_Human_Values_Without_Changing_Original_Data()
    {
        // Arrange
        var (job, review) = await Approved();

        // Act
        var export = DocumentExporter.Snapshot(job, review, false, DateTimeOffset.UtcNow);
        using var json = JsonDocument.Parse(DocumentExporter.Write(export, "json"));

        // Assert
        Assert.Equal("Original", job.Segments[0].ExtractedEntities[0].Value);
        Assert.Equal("=2+2", Assert.Single(export.Fields).EffectiveValue);
        Assert.Equal("Original", export.Fields[0].OriginalValue);
        Assert.False(export.Fields[0].EffectiveValueMatchesOriginal);
        Assert.Equal("HumanReviewed", export.View);
        Assert.Equal("=2+2", json.RootElement.GetProperty("fields")[0].GetProperty("effectiveValue").GetString());
        Assert.True(json.RootElement.GetProperty("hasDevelopmentResults").GetBoolean());
    }

    [Fact]
    public async Task Pending_Or_Source_Mismatched_Review_Is_Not_Silently_Exported_As_Approved()
    {
        // Arrange
        var (job, review) = await Approved();

        // Act / Assert
        Assert.Throws<ReviewConflictException>(() =>
            DocumentExporter.Snapshot(job, review with { Status = ReviewStatus.InReview }, false, DateTimeOffset.UtcNow));
        job.Segments[0].ExtractedEntities = [new ExtractedEntity("Name", "Changed source", "Party", 0.9)];
        Assert.Throws<ReviewConflictException>(() =>
            DocumentExporter.Snapshot(job, review, false, DateTimeOffset.UtcNow));
        var original = DocumentExporter.Snapshot(job, null, true, DateTimeOffset.UtcNow);
        Assert.Equal("OriginalUnreviewed", original.View);
        Assert.Equal("Unreviewed", Assert.Single(original.Fields).Outcome);
    }

    [Fact]
    public async Task Excel_Is_Valid_OpenXml_With_Inline_Text_And_No_Formula_Elements()
    {
        // Arrange
        var (job, review) = await Approved();
        var snapshot = DocumentExporter.Snapshot(job, review, false, DateTimeOffset.UtcNow);

        // Act
        using var stream = new MemoryStream(DocumentExporter.Write(snapshot, "xlsx"));
        using var document = SpreadsheetDocument.Open(stream, false);

        // Assert
        Assert.Empty(new OpenXmlValidator().Validate(document));
        var part = Assert.Single(document.WorkbookPart!.WorksheetParts);
        var sheet = Assert.IsType<Worksheet>(part.Worksheet);
        var cells = sheet.Descendants<Cell>().ToList();
        Assert.All(cells, cell => Assert.Equal(CellValues.InlineString, cell.DataType!.Value));
        Assert.All(cells, cell => Assert.Null(cell.CellFormula));
        Assert.Contains(cells, cell => cell.InnerText == "=2+2");
        Assert.Contains(cells, cell => cell.InnerText == "Original");
    }

    [Fact]
    public async Task Csv_Has_An_Excel_Compatible_Utf8_Preamble_And_Explicit_Uncalibrated_Metadata()
    {
        // Arrange
        var (job, review) = await Approved();
        var snapshot = DocumentExporter.Snapshot(job, review, false, DateTimeOffset.UtcNow);

        // Act
        var bytes = DocumentExporter.Write(snapshot, "csv");
        var text = Encoding.UTF8.GetString(bytes);

        // Assert
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, bytes[..3]);
        Assert.Contains("ModelScoreUncalibrated", text);
        Assert.Contains("\"'=2+2\"", text);
        Assert.Contains("\r\n", text);
        Assert.Contains("HasDevelopmentReview", text);
    }

    [Fact]
    public async Task Unsupported_Or_Oversized_Excel_Output_Is_Rejected_Not_Truncated()
    {
        // Arrange
        var (job, review) = await Approved();
        var snapshot = DocumentExporter.Snapshot(job, review, false, DateTimeOffset.UtcNow);
        var large = snapshot with { Fields = [snapshot.Fields[0] with { EffectiveValue = new string('x', 32_768) }] };

        // Act / Assert
        Assert.Throws<ArgumentException>(() => DocumentExporter.Write(snapshot, "unknown"));
        Assert.Throws<ExportLimitException>(() => DocumentExporter.Write(large, "xlsx"));
    }

    [Fact]
    public async Task Export_Audit_Is_Immutable_Reference_Metadata_Not_Raw_Field_Values()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "DocIntel.ExportAudit.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var (job, review) = await Approved();
            var export = DocumentExporter.Snapshot(job, review, false, DateTimeOffset.UtcNow);
            var bytes = DocumentExporter.Write(export, "csv");
            var audit = new ExportAuditEvent(export.ExportId, job.TenantId, job.JobId, Actor(), "csv",
                export.View, export.ReviewVersion, job.OriginalSha256, Convert.ToHexString(SHA256.HashData(bytes)),
                export.Fields.Count, export.CreatedAtUtc, false);
            var store = new LocalFileArtifactStore(directory);

            // Act
            await store.SaveExportAuditAsync(audit);

            // Assert
            var path = Assert.Single(Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories));
            var stored = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("=2+2", stored);
            Assert.Contains(audit.ContentSha256, stored);
            await Assert.ThrowsAsync<IOException>(() => store.SaveExportAuditAsync(audit));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static ActorIdentity Actor() => new(Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Guid.Parse("20000000-0000-0000-0000-000000000001"));

    private static async Task<(DocumentJob Job, ReviewSession Review)> Approved()
    {
        var job = new DocumentJob
        {
            TenantId = "tenant",
            JobId = "one",
            TenantDisplayName = "Test",
            FileName = "source.pdf",
            AppliedHarnessVersion = 1,
            Status = JobStatus.Completed,
            OriginalSha256 = new string('A', 64),
            HasDevelopmentResults = true,
            Segments = [new DocumentSlice { SegmentIndex = 0, StartPage = 1, EndPage = 1,
                ExtractedEntities = [new ExtractedEntity("Name", "Original", "Party", 0.9, 0)] }]
        };
        var service = new ReviewService(new InMemoryReviewStore());
        job.ReviewId = await service.CreateAsync(job, new ReviewPolicy());
        var claim = await service.ChangeAsync(job, Actor(), 1, "claim");
        var corrected = await service.ChangeAsync(job, Actor(), claim.Version, "field",
            "entity:0:0", FieldReviewOutcome.Corrected, "=2+2", "Fixture verified.");
        var review = await service.ChangeAsync(job, Actor(), corrected.Version, "submit");
        return (job, review);
    }
}
