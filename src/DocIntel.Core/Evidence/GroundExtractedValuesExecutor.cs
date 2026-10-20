using System.Diagnostics;
using DocIntel.Core.Features.DocumentSplitting;
using Microsoft.Agents.AI.Workflows;

namespace DocIntel.Core.Evidence;

/// <summary>Attaches original-source occurrence evidence after model extraction without changing extracted values.</summary>
[SendsMessage(typeof(SplittingState))]
public sealed class GroundExtractedValuesExecutor : Executor<SplittingState, SplittingState>
{
    public GroundExtractedValuesExecutor() : base(nameof(GroundExtractedValuesExecutor)) { }

    public override ValueTask<SplittingState> HandleAsync(
        SplittingState message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        var grounding = new ValueGrounding(message.Pages, cancellationToken);
        var remainingWordReferences = 1_024;
        ValueProvenance Find(string value, DocumentSlice slice)
        {
            var provenance = grounding.Find(value, slice.StartPage, slice.EndPage, cancellationToken);
            if (provenance.WordIds.Count > remainingWordReferences)
                return new ValueProvenance(GroundingStatus.ReferenceLimitExceeded, [], [], null);
            remainingWordReferences -= provenance.WordIds.Count;
            return provenance;
        }
        foreach (var slice in message.Slices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            slice.ExtractedEntities = slice.ExtractedEntities.Select(entity =>
            {
                var provenance = Find(entity.Value, slice);
                return entity with
                {
                    Provenance = provenance,
                    ConfidenceSignals = ValueGrounding.Signals(
                    entity.Confidence, 1, provenance)
                };
            }).ToList();
            if (slice.DataExtraction is not { } data) continue;
            ExtractedFieldValue Ground(ExtractedFieldValue field)
            {
                var provenance = Find(field.FieldValue, slice);
                return field with
                {
                    Provenance = provenance,
                    ConfidenceSignals = ValueGrounding.Signals(
                    field.ConfidenceValue, 100, provenance)
                };
            }
            slice.DataExtraction = data with
            {
                Fields = data.Fields.Select(Ground).ToList(),
                Tables = data.Tables.Select(table => table with
                {
                    Rows = table.Rows.Select(row => row with { Fields = row.Fields.Select(Ground).ToList() }).ToList()
                }).ToList()
            };
        }
        message.ExtractedEntities = message.Slices.SelectMany(slice => slice.ExtractedEntities).ToList();
        message.DataExtractions = message.Slices.Where(slice => slice.DataExtraction is not null)
            .Select(slice => slice.DataExtraction!).ToList();
        message.ExecutionTrace.Add(new WorkflowStepTrace(nameof(GroundExtractedValuesExecutor), true, true, null,
            watch.Elapsed.TotalMilliseconds,
            "Matched extracted values against original source words; model scores remain uncalibrated and no semantic verification is claimed."));
        return ValueTask.FromResult(message);
    }
}
