using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocIntel.Core.Jobs;

/// <summary>Fenced ownership of one persisted document job.</summary>
public interface IDocumentJobLease : IAsyncDisposable
{
    DocumentJob Job { get; }
    CancellationToken OwnershipLostToken { get; }
    Task SaveAsync(CancellationToken cancellationToken = default);
}

internal sealed class DocumentJobLease : IDocumentJobLease
{
    private readonly Func<CancellationToken, Task<bool>> _save;
    private readonly Func<CancellationToken, Task<bool>>? _renew;
    private readonly Func<Task> _release;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _lost = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _renewal;
    private bool _disposed;
    private readonly ILogger _logger;

    public DocumentJobLease(DocumentJob job, Func<CancellationToken, Task<bool>> save,
        Func<CancellationToken, Task<bool>>? renew, Func<Task> release, ILogger? logger = null)
    {
        Job = job;
        _save = save;
        _renew = renew;
        _release = release;
        _logger = logger ?? NullLogger.Instance;
        _renewal = RenewAsync();
    }

    public DocumentJob Job { get; }
    public CancellationToken OwnershipLostToken => _lost.Token;

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lost.Token.ThrowIfCancellationRequested();
            try
            {
                if (await _save(cancellationToken)) return;
                _lost.Cancel();
                throw new OperationCanceledException("Document ownership was lost.", _lost.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                _lost.Cancel();
                throw;
            }
        }
        finally { _operations.Release(); }
    }

    private async Task RenewAsync()
    {
        if (_renew is null) return;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), _stop.Token);
                await _operations.WaitAsync(_stop.Token);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    if (!await _renew(timeout.Token)) { _lost.Cancel(); return; }
                }
                finally { _operations.Release(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogWarning("Document ownership renewal failed: {ErrorType}.", exception.GetType().Name);
            _lost.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _renewal;
        await _operations.WaitAsync();
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                try { await _release(); }
                catch (Exception exception)
                {
                    _logger.LogWarning("Document ownership release failed; its finite lease must expire: {ErrorType}.",
                        exception.GetType().Name);
                }
            }
        }
        finally { _operations.Release(); }
    }
}
