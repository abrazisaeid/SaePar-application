#if ANDROID
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Android.Content;
using Android.Net;
using Android.OS;
using Com.Saepar.Tunnel.Bridge;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using SaeParTunnel.Core.Abstractions;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.App.Platforms.Android;

public sealed class AndroidTunnelService : ITunnelService
{
    private static readonly SemaphoreSlim LibXrayTestGate = new(1, 1);
    private readonly EndpointPrecheckService _precheck;
    private readonly XrayConfigBuilder _configBuilder;

    public AndroidTunnelService(EndpointPrecheckService precheck, XrayConfigBuilder configBuilder)
    {
        _precheck = precheck;
        _configBuilder = configBuilder;
    }

    public PlatformCapabilities Capabilities { get; } = new(
        "Android",
        "Android VpnService + XTLS libXray v26.7.28",
        true,
        true,
        true,
        true,
        "VPN واقعی Android فعال است: VpnService یک TUN می‌سازد و libXray رسمی داخل همان پروسه ترافیک TCP/UDP را به کانفیگ انتخاب‌شده می‌فرستد.");

    public bool IsConnected => AndroidVpnRuntime.IsConnected;

    public Task EnsureReadyAsync(
        AppSettings settings,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Preview 20 no longer reserves a fake/stable TUN fd. The real fd is
        // injected into Xray's per-config root env immediately before connect.
        SaeParXrayBridge.Initialize();
        progress?.Report(1);
        return Task.CompletedTask;
    }

    public async Task<TestResult> TestAsync(
        ConfigProfile profile,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (profile.Health == ProfileHealth.Unsupported)
            return new TestResult(false, null, profile.TestMessage, ValidationLevel.None);

        // Cheap endpoint rejection first; this keeps dead subscription entries from
        // paying the native Xray startup cost.
        var precheck = await _precheck.TestAsync(
            profile,
            settings.FastTestMode ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(4),
            cancellationToken).ConfigureAwait(false);
        if (!precheck.Success)
            return precheck;

        await LibXrayTestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? path = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaeParXrayBridge.Initialize();

            var socksPort = FindFreeLoopbackPort();
            var config = _configBuilder.BuildAndroidProbe(profile, socksPort);
            path = Path.Combine(FileSystem.CacheDirectory, $"saepar-probe-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(path, config, cancellationToken).ConfigureAwait(false);

            var timeout = settings.FastTestMode ? 4 : 7;
            var request = JsonSerializer.Serialize(new
            {
                apiVersion = 1,
                method = "ping",
                payload = new
                {
                    configPath = path,
                    timeout,
                    url = "https://cp.cloudflare.com/",
                    proxy = $"socks5://127.0.0.1:{socksPort}"
                }
            });

            var response = await Task.Run(() => SaeParXrayBridge.Invoke(request), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return ParsePingResponse(response);
        }
        catch (System.OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new TestResult(false, null, $"libXray: {ex.Message}", ValidationLevel.None);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                try { File.Delete(path); } catch { }
            }
            LibXrayTestGate.Release();
        }
    }

    public Task<TestResult> TestCurrentConnectionAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(IsConnected
            ? new TestResult(true, null, "VPN Android فعال است و تست اینترنت تأیید شده است.", ValidationLevel.FullProxy)
            : new TestResult(false, null, "VPN Android متصل نیست.", ValidationLevel.None));
    }

    public async Task ConnectAsync(
        ConfigProfile profile,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureReadyAsync(settings, cancellationToken: cancellationToken);

        var activity = Platform.CurrentActivity as MainActivity
            ?? throw new InvalidOperationException("Android Activity برای درخواست مجوز VPN در دسترس نیست.");

        AndroidVpnRuntime.ReportStatus("permission-check", "در حال بررسی مجوز VPN Android...");
        var permissionIntent = VpnService.Prepare(activity);
        if (permissionIntent is not null)
        {
            AndroidVpnRuntime.ReportStatus("permission-needed", "پنجره مجوز VPN Android در حال باز شدن است؛ گزینه تأیید را بزن.");
            using var permissionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            permissionCts.CancelAfter(TimeSpan.FromSeconds(60));
            bool granted;
            try
            {
                granted = await activity.RequestVpnPermissionAsync(permissionIntent, permissionCts.Token);
            }
            catch (System.OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                AndroidVpnRuntime.ReportStatus("permission-timeout", "پنجره مجوز VPN پاسخ نداد. از دکمه «تنظیمات VPN اندروید» وضعیت مجوز را بررسی کن.", false);
                throw new TimeoutException("درخواست مجوز VPN Android ظرف 60 ثانیه پاسخ نداد.");
            }
            if (!granted)
            {
                AndroidVpnRuntime.ReportStatus("permission-denied", "مجوز VPN Android تأیید نشد.", false);
                throw new InvalidOperationException("مجوز ساخت VPN توسط کاربر تأیید نشد.");
            }
            AndroidVpnRuntime.ReportStatus("permission-granted", "مجوز VPN تأیید شد؛ در حال راه‌اندازی سرویس...");
        }
        else
        {
            // Android deliberately returns null when this package is already prepared.
            // In that case no system consent page is expected to appear.
            AndroidVpnRuntime.ReportStatus("permission-already-granted", "مجوز VPN قبلاً برای SaePar Tunnel صادر شده؛ Android دیگر پنجره مجوز را نشان نمی‌دهد. در حال اتصال...");
        }

        var xrayJson = _configBuilder.BuildAndroidTun(profile, settings, 1400);
        var allowedPackages = settings.EnableWhitelistRouting
            ? settings.WhitelistApplications
                .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.PackageName))
                .Where(x => string.IsNullOrWhiteSpace(x.Platform) || x.Platform.Equals("Android", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.PackageName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));
        var wait = AndroidVpnRuntime.PrepareStartWaitAsync(timeoutCts.Token);

        var intent = new Intent(activity, typeof(SaeParVpnService));
        intent.SetAction(SaeParVpnService.ActionConnect);
        intent.PutExtra(SaeParVpnService.ExtraXrayJson, xrayJson);
        intent.PutExtra(SaeParVpnService.ExtraProfileId, profile.Id);
        intent.PutExtra(SaeParVpnService.ExtraProfileName, profile.DisplayName);
        intent.PutExtra(SaeParVpnService.ExtraAllowedPackages, allowedPackages);

        AndroidVpnRuntime.ReportStatus("service-start", "سرویس VPN شروع شد؛ در حال ساخت TUN و راه‌اندازی Xray...");
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            activity.StartForegroundService(intent);
        else
            activity.StartService(intent);

        try
        {
            await wait.ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var stopIntent = new Intent(activity, typeof(SaeParVpnService));
            stopIntent.SetAction(SaeParVpnService.ActionDisconnect);
            activity.StartService(stopIntent);
            throw new TimeoutException("راه‌اندازی و تست اینترنت VPN Android بیشتر از 45 ثانیه طول کشید.");
        }
    }

    public async Task DisconnectAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var activity = Platform.CurrentActivity;
        if (activity is null)
        {
            AndroidVpnRuntime.SignalDisconnected();
            return;
        }

        if (!IsConnected)
        {
            var stopOnly = new Intent(activity, typeof(SaeParVpnService));
            stopOnly.SetAction(SaeParVpnService.ActionDisconnect);
            activity.StartService(stopOnly);
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        var wait = AndroidVpnRuntime.PrepareStopWaitAsync(timeoutCts.Token);

        var intent = new Intent(activity, typeof(SaeParVpnService));
        intent.SetAction(SaeParVpnService.ActionDisconnect);
        activity.StartService(intent);

        try { await wait.ConfigureAwait(false); }
        catch (System.OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AndroidVpnRuntime.SignalDisconnected();
        }
    }

    private static int FindFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static TestResult ParsePingResponse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return new TestResult(false, null, "libXray پاسخ خالی داد.", ValidationLevel.None);

        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        var success = root.TryGetProperty("success", out var successNode) && successNode.GetBoolean();
        var error = root.TryGetProperty("error", out var errorNode) ? errorNode.GetString() : null;

        long? delay = null;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("delay", out var delayNode) && delayNode.TryGetInt64(out var parsedDelay))
            delay = parsedDelay;

        if (!success || delay is null || delay < 0 || delay >= 10000)
            return new TestResult(false, null, error ?? "Full proxy test ناموفق بود.", ValidationLevel.None);

        var latency = delay > int.MaxValue ? int.MaxValue : (int)delay.Value;
        return new TestResult(true, latency, $"Full proxy OK • {latency} ms", ValidationLevel.FullProxy);
    }
}
#endif
