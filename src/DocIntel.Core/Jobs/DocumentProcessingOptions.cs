namespace DocIntel.Core.Jobs;

/// <summary>Bounded document execution and persistent local metadata configuration.</summary>
public sealed class DocumentProcessingOptions
{
    public const string SectionName = "DocumentJobs";
    public string? DatabasePath { get; set; }
    public int MaxRunSeconds { get; set; } = 1_800;
    public int RecoveryPollSeconds { get; set; } = 5;
    public string QueueName { get; set; } = "document-jobs";
    public string PoisonQueueName { get; set; } = "document-jobs-poison";

    public void Validate()
    {
        if (MaxRunSeconds is < 1 or > 3_600 || RecoveryPollSeconds is < 1 or > 300 ||
            new[] { QueueName, PoisonQueueName }.Any(name => String.IsNullOrWhiteSpace(name) ||
                name.Length is < 3 or > 63 || name[0] == '-' || name[^1] == '-' ||
                name.Contains("--", StringComparison.Ordinal) ||
                name.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))) ||
            QueueName == PoisonQueueName)
            throw new ArgumentException("Document execution limits or queue names are invalid.");
    }
}
