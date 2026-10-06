#if ANDROID && SAEPAR_NOTIFICATION_DIAGNOSTICS
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Microsoft.Maui.ApplicationModel;
using SaeParTunnel.Core.Services;
using Application = global::Android.App.Application;
using OperationCanceledException = System.OperationCanceledException;

namespace SaeParTunnel.App.Platforms.Android;

internal static class AndroidNotificationDiagnostics
{
    internal static async Task RunAsync()
    {
        string? token = null;
        var store = new TunnelConfigurationStore(Path.Combine(Microsoft.Maui.Storage.FileSystem.CacheDirectory, "vpn-start"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var echo = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            if (AndroidVpnRuntime.IsServiceRunning) throw new InvalidOperationException("Active service preserved; skip diagnostic.");
            await AndroidNotificationPermission.RequestForConnectionAsync();
            echo.Start();
            var echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
            var socksPort = FreePort();
            var metricsPort = FreePort();
            while (metricsPort == socksPort) metricsPort = FreePort();
            var config = XrayTrafficClient.EnableMetrics(JsonSerializer.Serialize(new
            {
                inbounds = new[] { new { tag = "local-test", listen = "127.0.0.1", port = socksPort, protocol = "socks", settings = new { auth = "noauth", udp = false } } },
                outbounds = new object[] { new { tag = "proxy", protocol = "freedom", settings = new { } }, new { tag = "block", protocol = "blackhole", settings = new { } } },
                routing = new { rules = new object[] { new { type = "field", ip = new[] { "127.0.0.0/8" }, outboundTag = "proxy" }, new { type = "field", network = "tcp,udp", outboundTag = "block" } } }
            }), metricsPort);
            token = await store.WriteAsync(config, timeout.Token);
            SaeParVpnService.NotificationDiagnosticReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var context = Application.Context;
            var start = new Intent(context, typeof(SaeParVpnService)).SetAction(SaeParVpnService.ActionNotificationDiagnostic);
            start.PutExtra(SaeParVpnService.ExtraConfigToken, token);
            start.PutExtra(SaeParVpnService.ExtraMetricsPort, metricsPort);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O) context.StartForegroundService(start);
            else context.StartService(start);
            await SaeParVpnService.NotificationDiagnosticReady.Task.WaitAsync(timeout.Token);
            var echoTask = EchoAsync(echo, timeout.Token);
            for (var round = 0; round < 10; round++)
            {
                await TransferAsync(socksPort, echoPort, timeout.Token);
                await Task.Delay(1000, timeout.Token);
            }
            using var metrics = new XrayTrafficClient(metricsPort);
            var totals = await metrics.ReadAsync(timeout.Token);
            if (totals.SentBytes < 81920 || totals.ReceivedBytes < 655360) throw new Exception("Native counters missed local transfer.");
            Log.Info("SaeParNotificationCheck", $"native-loopback-counters: PASS sent={totals.SentBytes}, received={totals.ReceivedBytes}");
            await Task.Delay(2000, timeout.Token);
            var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            var notification = manager?.GetActiveNotifications()?.FirstOrDefault(n => n.Id == 42017)?.Notification;
            var body = notification?.Extras?.GetString("android.bigText") ?? string.Empty;
            if (!body.Contains("حجم ارسال") || !body.Contains("حجم دریافت")) throw new Exception("Missing traffic notification text/permission.");
            var action = notification?.Actions?.FirstOrDefault(a => a.Title?.ToString() == "قطع VPN");
            if (action?.ActionIntent is null) throw new Exception("Missing Disconnect action.");
            Log.Info("SaeParNotificationCheck", "live-notification-and-disconnect-action: PASS");
            // Leave time to inspect the actual notification on the device. The
            // app may be backgrounded; the same immutable action stops it.
            await Task.Delay(15000, timeout.Token);
            action.ActionIntent.Send();
            for (var i = 0; i < 50 && AndroidVpnRuntime.IsServiceRunning; i++) await Task.Delay(100, timeout.Token);
            if (AndroidVpnRuntime.IsServiceRunning) throw new Exception("Notification action did not stop service.");
            await Task.Delay(2000, timeout.Token);
            if (manager?.GetActiveNotifications()?.Any(n => n.Id == 42017) == true) throw new Exception("Stopped notification reappeared.");
            using var state = JsonDocument.Parse(Com.Saepar.Tunnel.Bridge.SaeParXrayBridge.Invoke("{\"apiVersion\":1,\"method\":\"getXrayState\"}"));
            if (state.RootElement.GetProperty("data").GetProperty("running").GetBoolean()) throw new Exception("Native core kept running.");
            Log.Info("SaeParNotificationCheck", "background-disconnect-core-stopped-no-notification-repost: PASS");
            timeout.Cancel();
            try { await echoTask; } catch (OperationCanceledException) { }
        }
        catch (Exception ex) { Log.Error("SaeParNotificationCheck", "FAIL: " + ex.GetType().Name + ": " + ex.Message); }
        finally
        {
            timeout.Cancel();
            if (token is not null) store.Delete(token);
            // Only the fixture service is stopped, and only if it was started.
            if (token is not null && AndroidVpnRuntime.IsServiceRunning)
                Application.Context.StartService(new Intent(Application.Context, typeof(SaeParVpnService)).SetAction(SaeParVpnService.ActionDisconnect));
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task EchoAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            var stream = client.GetStream();
            await stream.ReadExactlyAsync(new byte[8192], cancellationToken);
            await stream.WriteAsync(new byte[65536], cancellationToken);
        }
    }

    private static async Task TransferAsync(int socksPort, int echoPort, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, socksPort, cancellationToken);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken);
        var reply = new byte[2];
        await stream.ReadExactlyAsync(reply, cancellationToken);
        if (reply[0] != 5 || reply[1] != 0) throw new Exception("SOCKS auth failed.");
        await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(echoPort >> 8), (byte)echoPort }, cancellationToken);
        reply = new byte[4];
        await stream.ReadExactlyAsync(reply, cancellationToken);
        if (reply[1] != 0) throw new Exception("SOCKS loopback connect failed.");
        var remaining = reply[3] switch { 1 => 6, 4 => 18, _ => throw new Exception("Unexpected SOCKS address.") };
        await stream.ReadExactlyAsync(new byte[remaining], cancellationToken);
        await stream.WriteAsync(new byte[8192], cancellationToken);
        await stream.ReadExactlyAsync(new byte[65536], cancellationToken);
    }
}
#endif
