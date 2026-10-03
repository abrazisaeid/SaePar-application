using System.Diagnostics;
using System.Net.Sockets;
using System.Collections.Concurrent;
using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

public sealed class EndpointPrecheckService
{
    private sealed record Entry(DateTime Created, Lazy<Task<TestResult>> Result);
    private static readonly AsyncLocal<ConcurrentDictionary<string, Entry>?> Batch = new();

    public static IDisposable BeginBatch()
    {
        var previous = Batch.Value;
        Batch.Value = new ConcurrentDictionary<string, Entry>();
        return new BatchScope(() => Batch.Value = previous);
    }

    private sealed class BatchScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    public Task<TestResult> TestAsync(ConfigProfile profile, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(profile.Address)) return TestEndpointAsync(profile, timeout, cancellationToken);
        if (Batch.Value is not { } cache) return TestEndpointAsync(profile, timeout, cancellationToken);
        var transport = string.Equals(profile.Network, "mkcp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";
        var key = $"{profile.Address.ToLowerInvariant()}:{profile.Port}:{transport}:{timeout.Ticks}";
        if (cache.TryGetValue(key, out var old) && DateTime.UtcNow - old.Created > TimeSpan.FromSeconds(15))
            cache.TryRemove(key, out _);
        if (cache.Count > 2048) cache.Clear();
        var entry = cache.GetOrAdd(key, _ => new Entry(DateTime.UtcNow,
            new Lazy<Task<TestResult>>(() => TestEndpointAsync(profile, timeout, cancellationToken))));
        return entry.Result.Value.WaitAsync(cancellationToken);
    }

    private static async Task<TestResult> TestEndpointAsync(ConfigProfile profile, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(profile.Address) || profile.Port is < 1 or > 65535)
            return new TestResult(false, null, "آدرس یا پورت معتبر نیست.", ValidationLevel.EndpointOnly);

        if (string.Equals(profile.Network, "mkcp", StringComparison.OrdinalIgnoreCase))
            return new TestResult(false, null, "برای mKCP تست TCP معیار مناسبی نیست.", ValidationLevel.EndpointOnly);

        using var tcp = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var sw = Stopwatch.StartNew();
            await tcp.ConnectAsync(profile.Address, profile.Port, timeoutCts.Token);
            sw.Stop();
            return new TestResult(true, (int)sw.ElapsedMilliseconds, "Endpoint TCP قابل دسترس است.", ValidationLevel.EndpointOnly);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TestResult(false, null, $"TCP timeout بعد از {timeout.TotalSeconds:0.#} ثانیه.", ValidationLevel.EndpointOnly);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new TestResult(false, null, "TCP: " + ex.Message, ValidationLevel.EndpointOnly);
        }
    }
}
