using SaeParTunnel.App.Platforms.Android;
using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Tests;

public sealed class AndroidVpnRuntimeTests : IDisposable
{
    public AndroidVpnRuntimeTests() => AndroidVpnRuntime.ReportServiceStopped();
    public void Dispose()
    {
        AndroidVpnRuntime.CancelStartWait();
        AndroidVpnRuntime.ReportServiceStopped();
    }

    [Fact]
    public async Task FailedStartupDoesNotAcknowledgeStopBeforeActualServiceCleanup()
    {
        var startup = AndroidVpnRuntime.PrepareStartWaitAsync(CancellationToken.None);
        AndroidVpnRuntime.ReportServiceCreated();
        var shutdown = AndroidVpnRuntime.PrepareStopWaitAsync(CancellationToken.None);
        AndroidVpnRuntime.SignalError("TUN failed");
        await Assert.ThrowsAsync<TunnelStartupException>(() => startup);
        Assert.True(AndroidVpnRuntime.IsServiceRunning);
        Assert.False(shutdown.IsCompleted);
        Assert.False(AndroidVpnRuntime.TryRequestStop()); // error path already stops itself
        AndroidVpnRuntime.ReportServiceStopped();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(AndroidVpnRuntime.IsServiceRunning);
    }

    [Fact]
    public async Task OldCancellationCannotCancelTheNextConnection()
    {
        using var oldCts = new CancellationTokenSource();
        var old = AndroidVpnRuntime.PrepareStartWaitAsync(oldCts.Token);
        AndroidVpnRuntime.SignalConnected("old");
        await old;
        var next = AndroidVpnRuntime.PrepareStartWaitAsync(CancellationToken.None);
        oldCts.Cancel();
        Assert.False(next.IsCompleted);
        AndroidVpnRuntime.SignalConnected("next");
        await next.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ShutdownCommandIsSentOnceAndCanBeRetriedIfSendingFailed()
    {
        AndroidVpnRuntime.ReportServiceCreated();
        Assert.True(AndroidVpnRuntime.TryRequestStop());
        Assert.False(AndroidVpnRuntime.TryRequestStop());
        AndroidVpnRuntime.ResetStopRequest();
        Assert.True(AndroidVpnRuntime.TryRequestStop());
        AndroidVpnRuntime.ReportServiceStopped();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupAndTrafficFailuresRemainDistinct(bool startupFailure)
    {
        var start = AndroidVpnRuntime.PrepareStartWaitAsync(CancellationToken.None);
        AndroidVpnRuntime.SignalError("failure", startupFailure);
        if (startupFailure)
            await Assert.ThrowsAsync<TunnelStartupException>(() => start);
        else
            await Assert.ThrowsAsync<TunnelValidationException>(() => start);
    }
}
