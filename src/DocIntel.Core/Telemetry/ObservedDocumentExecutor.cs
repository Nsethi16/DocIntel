using DocIntel.Core.Features.DocumentSplitting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Telemetry;

/// <summary>Observes every stage without duplicating instrumentation in individual feature implementations.</summary>
[SendsMessage(typeof(SplittingState))]
internal sealed class ObservedDocumentExecutor(
    Executor<SplittingState, SplittingState> inner, ILogger logger) : Executor<SplittingState, SplittingState>(inner.Id)
{
    public override async ValueTask<SplittingState> HandleAsync(
        SplittingState message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        using var operation = AgentDiagnostics.Begin("Executor." + inner.Id, logger,
            message.Tenant.TenantId, message.DocumentId, message.Tenant.HarnessVersion);
        var previousSteps = message.ExecutionTrace.Count;
        try
        {
            var result = await inner.HandleAsync(message, context, cancellationToken);
            var trace = result.ExecutionTrace.Skip(previousSteps).LastOrDefault();
            if (trace?.Succeeded == false)
                throw new InvalidOperationException("The executor reported an unsuccessful processing step.");
            operation.Complete(skipped: trace?.Executed == false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }
}
