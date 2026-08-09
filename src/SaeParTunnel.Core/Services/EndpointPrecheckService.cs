using System.Diagnostics;
using System.Net.Sockets;
using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

public sealed class EndpointPrecheckService
{
    public async Task<TestResult> TestAsync(ConfigProfile profile, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
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
        catch (Exception ex)
        {
            return new TestResult(false, null, "TCP: " + ex.Message, ValidationLevel.EndpointOnly);
        }
    }
}
