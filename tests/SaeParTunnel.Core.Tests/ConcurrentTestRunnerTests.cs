using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class ConcurrentTestRunnerTests
{
    [Fact]
    public async Task SlowFirstServerDoesNotBlockLaterCandidates()
    {
        var laterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new ConcurrentBag<int>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var remaining = await ConcurrentTestRunner.RunAsync(Enumerable.Range(0, 4), 2, async (i, ct) =>
        {
            if (i == 0) await laterStarted.Task.WaitAsync(ct);
            if (i == 3) laterStarted.TrySetResult();
            completed.Add(i);
        }, () => false, deadline.Token);
        Assert.True(laterStarted.Task.IsCompletedSuccessfully);
        Assert.Empty(remaining);
        Assert.Equal(4, completed.Count);
    }

    [Fact]
    public async Task TargetStopsSlowTestsAndRetainsUntestedCandidates()
    {
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var found = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var remaining = await ConcurrentTestRunner.RunAsync(new[] { 0, 1, 2 }, 2, async (i, ct) =>
        {
            if (i == 0)
            {
                slowStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            else
            {
                await slowStarted.Task.WaitAsync(ct);
                Interlocked.Increment(ref found);
            }
        }, () => Volatile.Read(ref found) >= 1, deadline.Token);
        Assert.Equal(1, found);
        Assert.Equal(new[] { 0, 2 }, remaining.OrderBy(i => i));
        Assert.False(deadline.IsCancellationRequested);
    }

    [Fact]
    public async Task BatchSharesEndpointChecksButManualPingIsFresh()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var profile = new ConfigProfile { Address = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        var service = new EndpointPrecheckService();
        using (EndpointPrecheckService.BeginBatch())
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => service.TestAsync(profile, TimeSpan.FromSeconds(2))));
            Assert.All(results, r => Assert.True(r.Success));
            using var accepted = await listener.AcceptTcpClientAsync();
            Assert.False(listener.Pending());
        }
        Assert.True((await service.TestAsync(profile, TimeSpan.FromSeconds(2))).Success);
        Assert.True(listener.Pending());
        using var fresh = await listener.AcceptTcpClientAsync();
    }
}
