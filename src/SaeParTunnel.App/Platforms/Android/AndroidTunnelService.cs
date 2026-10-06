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
    private static readonly AndroidProbeProcess ProbeProcess = new();
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

        using var physicalScope = AndroidDirectNetwork.BeginScope();
        var physicalNetwork = AndroidDirectNetwork.GetNetwork();

        // Cheap endpoint rejection first; this keeps dead subscription entries from
        // paying the native Xray startup cost.
        if (settings.FastTestMode && !string.Equals(profile.Network, "mkcp", StringComparison.OrdinalIgnoreCase))
        {
            var precheck = await _precheck.TestAsync(
                profile,
                settings.FastTestMode ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(4),
                cancellationToken).ConfigureAwait(false);
            AndroidDirectNetwork.EnsureAvailable(physicalNetwork);
            if (!precheck.Success)
                return precheck;
        }

        try
        {
            var socksPort = FindFreeLoopbackPort();
            var config = _configBuilder.BuildAndroidProbe(profile, socksPort);
            var response = await ProbeProcess.RunAsync(config, socksPort,
                settings.FastTestMode ? 4 : 7, cancellationToken).ConfigureAwait(false);
            return ParsePingResponse(response);
        }
        catch (System.OperationCanceledException) { throw; }
        catch (TunnelStartupException) { throw; }
        catch (Exception ex)
        {
            return new TestResult(false, null, $"libXray: {ex.Message}", ValidationLevel.None);
        }
    }

    public async Task<TestResult> TestCurrentConnectionAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            return new TestResult(false, null, "VPN Android متصل نیست.", ValidationLevel.None);

        // The app is included in the VPN. Probe domains are explicitly routed
        // through the proxy even when website whitelisting is enabled.
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        foreach (var endpoint in new[] { "https://cp.cloudflare.com/generate_204", "https://www.gstatic.com/generate_204" })
        {
            try
            {
                var watch = Stopwatch.StartNew();
                using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NoContent)
                    return new TestResult(true, (int)watch.ElapsedMilliseconds, "پاسخ اینترنت از اتصال فعال دریافت شد.", ValidationLevel.FullProxy);
            }
            catch (System.OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or System.OperationCanceledException) { }
        }
        return new TestResult(false, null, "اتصال فعال به درخواست پینگ پاسخ نداد.", ValidationLevel.FullProxy);
    }

    public async Task ConnectAsync(
        ConfigProfile profile,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        try { await StartVpnAsync(profile, settings, cancellationToken); }
        catch (System.OperationCanceledException) { throw; }
        catch (TunnelStartupException) { throw; }
        catch (TunnelValidationException) { throw; }
        catch (Exception ex)
        {
            throw new TunnelStartupException("راه‌اندازی VPN اندروید انجام نشد: " + ex.Message, ex);
        }
    }

    private async Task StartVpnAsync(
        ConfigProfile profile,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ProbeProcess.StopAsync(cancellationToken).ConfigureAwait(false);
        await EnsureReadyAsync(settings, cancellationToken: cancellationToken);
        // A timeout/error may precede destruction of the previous service.
        if (AndroidVpnRuntime.IsServiceRunning)
            await DisconnectAsync(settings, cancellationToken);

        var activity = Platform.CurrentActivity as MainActivity
            ?? throw new InvalidOperationException("Android Activity برای درخواست مجوز VPN در دسترس نیست.");

        await AndroidNotificationPermission.RequestForConnectionAsync();
        cancellationToken.ThrowIfCancellationRequested();

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

        var metricsPort = FindFreeLoopbackPort();
        var xrayJson = await Task.Run(() => _configBuilder.BuildAndroidTun(profile, settings, 1400, metricsPort), cancellationToken);
        var allowedPackages = settings.EnableWhitelistRouting && !settings.EnableIranBypass
            ? settings.WhitelistApplications
                .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.PackageName))
                .Where(x => string.IsNullOrWhiteSpace(x.Platform) || x.Platform.Equals("Android", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.PackageName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(25));
        var configStore = new TunnelConfigurationStore(Path.Combine(FileSystem.CacheDirectory, "vpn-start"));
        var configToken = await configStore.WriteAsync(xrayJson, cancellationToken);
        var wait = AndroidVpnRuntime.PrepareStartWaitAsync(timeoutCts.Token);
        var intent = new Intent(activity, typeof(SaeParVpnService));
        intent.SetAction(SaeParVpnService.ActionConnect);
        intent.PutExtra(SaeParVpnService.ExtraConfigToken, configToken);
        intent.PutExtra(SaeParVpnService.ExtraProfileId, profile.Id);
        intent.PutExtra(SaeParVpnService.ExtraProfileName, profile.DisplayName);
        intent.PutExtra(SaeParVpnService.ExtraMetricsPort, metricsPort);
        intent.PutExtra(SaeParVpnService.ExtraAllowedPackages, allowedPackages);

        try
        {
            AndroidVpnRuntime.ReportStatus("service-start", "در حال شروع سرویس VPN و راه‌اندازی Xray...");
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                activity.StartForegroundService(intent);
            else
                activity.StartService(intent);
            await wait.ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
            await DisconnectAsync(settings, CancellationToken.None);
            if (AndroidVpnRuntime.WasStartCancelledByUser)
                throw new System.OperationCanceledException("اتصال از اعلان VPN لغو شد.");
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("راه‌اندازی و تست اینترنت VPN Android بیشتر از 25 ثانیه طول کشید.");
        }
        finally
        {
            AndroidVpnRuntime.CancelStartWait();
            // Observe the waiter even when StartForegroundService itself throws.
            try { await wait.ConfigureAwait(false); } catch { }
            configStore.Delete(configToken);
        }
    }

    public async Task DisconnectAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var activity = Platform.CurrentActivity;
        if (!AndroidVpnRuntime.IsServiceRunning) return;
        if (activity is null)
            throw new TunnelStartupException("برای قطع سرویس VPN، صفحهٔ برنامه باید باز باشد.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        var wait = AndroidVpnRuntime.PrepareStopWaitAsync(timeoutCts.Token);

        if (!AndroidVpnRuntime.IsServiceRunning)
            AndroidVpnRuntime.SignalDisconnected();
        else if (AndroidVpnRuntime.TryRequestStop())
        {
            try
            {
                var intent = new Intent(activity, typeof(SaeParVpnService));
                intent.SetAction(SaeParVpnService.ActionDisconnect);
                activity.StartService(intent);
            }
            catch
            {
                AndroidVpnRuntime.ResetStopRequest();
                throw;
            }
        }

        try { await wait.ConfigureAwait(false); }
        catch (System.OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TunnelStartupException("سرویس VPN قبلی هنوز بسته نشده است. از تنظیمات VPN اندروید اتصال را قطع کن و دوباره تلاش کن.");
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
