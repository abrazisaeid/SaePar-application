using System.Collections.Concurrent;

namespace SaeParTunnel.Core.Services;

public static class ConcurrentTestRunner
{
    public static async Task<IReadOnlyList<T>> RunAsync<T>(IEnumerable<T> items, int concurrency,
        Func<T, CancellationToken, Task> test, Func<bool> reachedTarget, CancellationToken cancellationToken)
    {
        var remaining = new ConcurrentQueue<T>(items);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task Worker()
        {
            while (!stop.IsCancellationRequested && !reachedTarget() && remaining.TryDequeue(out var item))
            {
                try { await test(item, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    remaining.Enqueue(item);
                    return;
                }
                catch
                {
                    remaining.Enqueue(item);
                    stop.Cancel();
                    throw;
                }
                if (reachedTarget()) stop.Cancel();
            }
        }
        await Task.WhenAll(Enumerable.Range(0, Math.Clamp(concurrency, 1, 64)).Select(_ => Task.Run(Worker)));
        return remaining.ToArray();
    }
}
