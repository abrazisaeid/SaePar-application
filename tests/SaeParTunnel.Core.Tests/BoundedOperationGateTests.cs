using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class BoundedOperationGateTests
{
    [Fact]
    public async Task CancellationReleasesCallerButDoesNotOverlapUninterruptibleNativeWork()
    {
        var gate = new BoundedOperationGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var running = gate.RunAsync(() => { entered.SetResult(); return finish.Task; }, TimeSpan.FromSeconds(3), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        var anotherEntered = false;
        await Assert.ThrowsAsync<TimeoutException>(() => gate.RunAsync(() => { anotherEntered = true; return Task.FromResult(2); }, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.False(anotherEntered);
        await Assert.ThrowsAsync<TimeoutException>(() => gate.WaitForIdleAsync(TimeSpan.FromMilliseconds(30), CancellationToken.None));
        finish.SetResult(1);
        await gate.WaitForIdleAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        Assert.Equal(2, await gate.RunAsync(() => Task.FromResult(2), TimeSpan.FromSeconds(3), CancellationToken.None));
    }

    [Fact]
    public async Task NativeTimeoutIsBoundedAndLateCompletionMakesGateReusable()
    {
        var gate = new BoundedOperationGate();
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<TimeoutException>(() => gate.RunAsync(() => finish.Task, TimeSpan.FromMilliseconds(30), CancellationToken.None));
        finish.SetResult(1);
        await gate.WaitForIdleAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        Assert.Equal(3, await gate.RunAsync(() => Task.FromResult(3), TimeSpan.FromSeconds(3), CancellationToken.None));
    }

    [Fact]
    public async Task FaultedNativeWorkReleasesGate()
    {
        var gate = new BoundedOperationGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RunAsync<int>(() => throw new InvalidOperationException(), TimeSpan.FromSeconds(3), CancellationToken.None));
        await gate.WaitForIdleAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
    }
}
