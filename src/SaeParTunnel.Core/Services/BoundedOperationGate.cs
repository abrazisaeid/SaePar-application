namespace SaeParTunnel.Core.Services;

// Native calls cannot be interrupted by cancelling a managed Task. Return control
// to the caller promptly, but retain the gate until native work really finishes.
public sealed class BoundedOperationGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task? _abandonedOperation;

    public async Task<T> RunAsync<T>(Func<Task<T>> work, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _abandonedOperation) is { IsCompleted: false })
            throw new TimeoutException("Previous native operation has not finished.");
        // Queueing behind healthy native calls is distinct from a hung call.
        await EnterAsync(TimeSpan.FromTicks(timeout.Ticks * 16), cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            _gate.Release();
            cancellationToken.ThrowIfCancellationRequested();
        }
        var operation = Task.Run(async () =>
        {
            try { return await work().ConfigureAwait(false); }
            finally { _gate.Release(); }
        });
        // Observe late faults even if the caller has already timed out/cancelled.
        _ = operation.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await operation.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            if (!operation.IsCompleted) Volatile.Write(ref _abandonedOperation, operation);
            throw;
        }
    }

    public async Task WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await EnterAsync(timeout, cancellationToken).ConfigureAwait(false);
        _gate.Release();
    }

    private async Task EnterAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
            throw new TimeoutException("Native operation has not finished.");
    }
}
