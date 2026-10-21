using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace DocIntel.Core.Telemetry;

/// <summary>Privacy-safe operation traces and measured metrics consumed by the existing OpenTelemetry pipeline.</summary>
public static class AgentDiagnostics
{
    public const string SourceName = "DocIntel.Agent";
    private static readonly ActivitySource Source = new(SourceName);
    private static readonly Meter Meter = new(SourceName);
    private static readonly Counter<long> Completed = Meter.CreateCounter<long>("docintel.operation.completed");
    private static readonly UpDownCounter<long> Active = Meter.CreateUpDownCounter<long>("docintel.operation.active");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("docintel.operation.duration", "ms");
    private static readonly Counter<long> Tokens = Meter.CreateCounter<long>("docintel.model.tokens", "{token}");

    /// <summary>Starts one known server operation; never accepts question text, filenames, values or provider diagnostics.</summary>
    public static Operation Begin(string operation, ILogger? logger = null,
        string? tenantId = null, string? documentId = null, int? harnessVersion = null) =>
        new(operation, logger, tenantId, documentId, harnessVersion);

    /// <summary>Records provider-reported usage only; missing counts and monetary cost are never invented.</summary>
    public static void RecordTokens(long? input, long? output)
    {
        if (input is >= 0) Tokens.Add(input.Value, new KeyValuePair<string, object?>("direction", "input"));
        if (output is >= 0) Tokens.Add(output.Value, new KeyValuePair<string, object?>("direction", "output"));
    }

    /// <summary>Balances active-operation metrics and closes spans on success, failure and cancellation.</summary>
    public sealed class Operation : IDisposable
    {
        private readonly string _name;
        private readonly ILogger? _logger;
        private readonly Activity? _activity;
        private readonly IDisposable? _scope;
        private readonly long _started = Stopwatch.GetTimestamp();
        private string _outcome = "failed";
        private string? _errorType;
        private bool _disposed;

        internal Operation(string name, ILogger? logger, string? tenantId, string? documentId, int? harnessVersion)
        {
            _name = name;
            _logger = logger;
            _activity = Source.StartActivity(name);
            _activity?.SetTag("docintel.tenant_id", tenantId);
            _activity?.SetTag("docintel.document_id", documentId);
            _activity?.SetTag("docintel.harness_version", harnessVersion);
            _scope = logger?.BeginScope(new Dictionary<string, object?>
            {
                ["TenantId"] = tenantId,
                ["JobId"] = documentId,
                ["HarnessVersion"] = harnessVersion
            });
            Active.Add(1, new KeyValuePair<string, object?>("operation", name));
            logger?.LogInformation("Operation {Operation} started.", name);
        }

        public void Complete(bool skipped = false) => _outcome = skipped ? "skipped" : "succeeded";
        public void Cancel() => _outcome = "cancelled";
        public void Fail(Exception exception) => _errorType = exception.GetBaseException().GetType().Name;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var duration = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            var tags = new TagList { { "operation", _name }, { "outcome", _outcome } };
            Completed.Add(1, tags);
            Duration.Record(duration, tags);
            Active.Add(-1, new KeyValuePair<string, object?>("operation", _name));
            _activity?.SetTag("docintel.outcome", _outcome);
            if (_outcome == "failed")
            {
                _activity?.SetStatus(ActivityStatusCode.Error);
                _activity?.SetTag("error.type", _errorType);
                _logger?.LogError("Operation {Operation} failed after {DurationMs} ms; error type {ErrorType}.",
                    _name, duration, _errorType ?? "UnspecifiedFailure");
            }
            else
            {
                _activity?.SetStatus(_outcome == "cancelled" ? ActivityStatusCode.Unset : ActivityStatusCode.Ok);
                _logger?.LogInformation("Operation {Operation} ended with {Outcome} after {DurationMs} ms.",
                    _name, _outcome, duration);
            }
            _activity?.Dispose();
            _scope?.Dispose();
        }
    }
}
