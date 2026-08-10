#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Com.Saepar.Tunnel.Bridge;

namespace SaeParTunnel.App.Platforms.Android;

[Service(
    Name = "com.saepar.tunnel.SaeParVpnService",
    Permission = global::Android.Manifest.Permission.BindVpnService,
    Exported = true,
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class SaeParVpnService : VpnService
{
    public const string ActionConnect = "com.saepar.tunnel.action.CONNECT";
    public const string ActionDisconnect = "com.saepar.tunnel.action.DISCONNECT";
    public const string ExtraXrayJson = "xray_json";
    public const string ExtraProfileId = "profile_id";
    public const string ExtraProfileName = "profile_name";
    public const string ExtraAllowedPackages = "allowed_packages";

    private const string NotificationChannelId = "saepar_vpn";
    private const int NotificationId = 42017;
    private static readonly TimeSpan ValidationTcpTimeout = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan ValidationHttpTimeout = TimeSpan.FromMilliseconds(3500);
    private static readonly string[] ValidationEndpoints =
    {
        "https://cp.cloudflare.com/generate_204",
        "https://www.gstatic.com/generate_204",
        "https://www.google.com/generate_204"
    };

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private ParcelFileDescriptor? _vpnInterface;
    private bool _stopping;

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var action = intent?.Action ?? ActionConnect;
        if (action == ActionDisconnect)
        {
            PromoteToForeground("در حال قطع اتصال...");
            _ = Task.Run(StopTunnelAsync);
            return StartCommandResult.NotSticky;
        }

        // Promote immediately. Service work is intentionally moved off Android's
        // main thread to avoid ANR while libXray parses/starts the native core.
        PromoteToForeground("در حال برقراری VPN...");
        AndroidVpnRuntime.ReportStatus("service-running", "سرویس VPN اجرا شد؛ در حال آماده‌سازی رابط TUN...");

        var xrayJson = intent?.GetStringExtra(ExtraXrayJson) ?? string.Empty;
        var profileId = intent?.GetStringExtra(ExtraProfileId) ?? string.Empty;
        var profileName = intent?.GetStringExtra(ExtraProfileName) ?? "SaePar Tunnel";
        var allowedPackages = intent?.GetStringArrayExtra(ExtraAllowedPackages) ?? Array.Empty<string>();

        _ = Task.Run(() => StartTunnelAsync(xrayJson, profileId, profileName, allowedPackages));
        return StartCommandResult.NotSticky;
    }

    public override void OnRevoke()
    {
        _ = Task.Run(StopTunnelAsync);
        base.OnRevoke();
    }

    public override void OnDestroy()
    {
        // Android may destroy the service without sending our explicit stop action.
        // Keep cleanup idempotent and bounded.
        try { SaeParXrayBridge.DetachTun(); } catch { }
        try { _vpnInterface?.Close(); } catch { }
        _vpnInterface = null;
        AndroidVpnRuntime.SignalDisconnected();
        base.OnDestroy();
    }

    private async Task StartTunnelAsync(string xrayJson, string profileId, string profileName, string[] allowedPackages)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopping = false;
            await StopCoreAndInterfaceOnlyAsync().ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(xrayJson))
                throw new InvalidOperationException("Android Xray configuration is empty.");

            // Match the currently working OneXray Android TUN layout as closely as
            // possible. 198.18.0.0/15 is the benchmarking range commonly used for
            // user-space TUN stacks and avoids colliding with home/mobile LANs.
            // Keep the first production tunnel IPv4-only until the data path is proven.
            const string tunAddress = "198.18.0.1";
            const string tunDns = "8.8.8.8";
            const int tunMtu = 1500;

            AndroidVpnRuntime.ReportStatus(
                "dns-selected",
                $"Android TUN: {tunAddress}/32 • DNS: {tunDns} • MTU: {tunMtu}");

            var builder = new VpnService.Builder(this)
                .SetSession("SaePar Tunnel")
                .SetMtu(tunMtu)
                .AddAddress(tunAddress, 32)
                .AddRoute("0.0.0.0", 0)
                .AddDnsServer(tunDns);

            var configureIntent = PackageManager?.GetLaunchIntentForPackage(PackageName);
            if (configureIntent is not null)
            {
                var configurePending = PendingIntent.GetActivity(
                    this, 102, configureIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                if (configurePending is not null) builder.SetConfigureIntent(configurePending);
            }

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                builder.SetMetered(false);

            var packages = allowedPackages
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (packages.Length > 0)
            {
                // Keep the SaePar process inside the VPN too. This lets us perform a
                // real post-connect data-plane check from the app itself. Xray's own
                // upstream sockets are kept OUTSIDE the VPN with VpnService.protect(fd)
                // through libXray's DialerController, which is the Android-recommended
                // way to avoid a tunnel loop.
                var allowedCount = 0;
                try
                {
                    builder.AddAllowedApplication(PackageName);
                    allowedCount++;
                }
                catch (global::Android.Content.PM.PackageManager.NameNotFoundException) { }

                foreach (var packageName in packages)
                {
                    if (packageName.Equals(PackageName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        builder.AddAllowedApplication(packageName);
                        allowedCount++;
                    }
                    catch (global::Android.Content.PM.PackageManager.NameNotFoundException) { }
                }

                AndroidVpnRuntime.ReportStatus(
                    "whitelist",
                    $"Whitelist Android فعال است؛ {Math.Max(0, allowedCount - 1)} برنامه انتخاب‌شده از VPN عبور می‌کند.");
            }
            // Full-tunnel mode deliberately does NOT call AddDisallowedApplication
            // for SaePar itself. The app must traverse the TUN so the post-connect
            // validation below verifies the same data path used by Telegram/Chrome.
            // libXray protects only its upstream sockets with VpnService.protect(fd).

            AndroidVpnRuntime.ReportStatus("tun-establish", "در حال ساخت رابط VPN/TUN Android...");
            _vpnInterface = builder.Establish()
                ?? throw new InvalidOperationException("Android VpnService.Builder.establish() returned null.");

            AndroidVpnRuntime.ReportStatus("tun-ready", "رابط TUN ساخته شد؛ در حال اتصال آن به libXray...");
            // libXray's own resolver must bypass the VPN to prevent a recursion loop.
            // OneXray uses the configured TUN DNS here and protects the resulting DNS
            // socket with VpnService.protect().
            SaeParXrayBridge.AttachTun(this, _vpnInterface, $"{tunDns}:53");

            // IMPORTANT: pass the *actual* Android TUN descriptor through Xray's
            // per-config env map. This remains correct even if libXray/Go was already
            // initialized earlier by a Full-Test. Using a fake reserved fd + dup2
            // proved unreliable on real Android devices and could leave every app
            // request stuck until timeout while Xray itself appeared to be running.
            var tunFd = _vpnInterface.Fd;
            var runtimeXrayJson = InjectTunFdIntoConfig(xrayJson, tunFd);
            AndroidVpnRuntime.ReportStatus(
                "tun-fd",
                $"TUN واقعی Android با fd={tunFd} به Xray تحویل شد؛ در حال راه‌اندازی Core...");

            AndroidVpnRuntime.ReportStatus("xray-start", "TUN آماده است؛ در حال راه‌اندازی Xray با کانفیگ انتخاب‌شده...");
            var request = System.Text.Json.JsonSerializer.Serialize(new
            {
                apiVersion = 1,
                method = "runXrayFromJson",
                payload = new { configJSON = runtimeXrayJson }
            });

            var response = SaeParXrayBridge.Invoke(request);
            EnsureLibXraySuccess(response, "شروع Xray");

            AndroidVpnRuntime.ReportStatus(
                "data-plane-check",
                "VPN و Xray آماده‌اند؛ در حال تست عبور واقعی اینترنت از TUN...");
            UpdateNotification($"در حال تست اینترنت • {profileName}");

            var validation = await ValidateTunnelTrafficAsync().ConfigureAwait(false);
            if (validation.Success)
            {
                AndroidVpnRuntime.ReportStatus(
                    "data-plane-ok",
                    $"مسیر TUN → Xray → Internet تأیید شد ({validation.Message}) • fd={tunFd} • DNS={tunDns}.",
                    null,
                    profileId);
                AndroidVpnRuntime.SignalConnected(profileId);
                UpdateNotification($"متصل و تست‌شده • {profileName}");
                return;
            }

            await StopCoreAndInterfaceOnlyAsync().ConfigureAwait(false);
            AndroidVpnRuntime.SignalError(
                $"تست اینترنت از VPN تأیید نشد • fd={tunFd} • TUN={tunAddress}/32 • DNS={tunDns}: " + validation.Message);
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
        catch (Exception ex)
        {
            try { await StopCoreAndInterfaceOnlyAsync().ConfigureAwait(false); } catch { }
            AndroidVpnRuntime.SignalError(ex.Message);
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopTunnelAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopping) return;
            _stopping = true;
            await StopCoreAndInterfaceOnlyAsync().ConfigureAwait(false);
            AndroidVpnRuntime.SignalDisconnected();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private Task StopCoreAndInterfaceOnlyAsync()
    {
        try { SaeParXrayBridge.DetachTun(); } catch { }
        try { _vpnInterface?.Close(); } catch { }
        _vpnInterface?.Dispose();
        _vpnInterface = null;
        return Task.CompletedTask;
    }


    private static async Task<(bool Success, string Message)> ValidateTunnelTrafficAsync()
    {
        // Run probes in parallel so a blocked or slow validation endpoint does not
        // keep the UI stuck on "testing internet" while another endpoint is healthy.
        var tcpTask = ProbeTcpAsync();
        var pendingHttp = ValidationEndpoints.Select(ProbeHttpAsync).ToList();
        var errors = new List<string>();

        while (pendingHttp.Count > 0)
        {
            var completed = await Task.WhenAny(pendingHttp).ConfigureAwait(false);
            pendingHttp.Remove(completed);
            var result = await completed.ConfigureAwait(false);
            if (result.Success)
            {
                var tcpProbeResult = tcpTask.IsCompleted
                    ? await tcpTask.ConfigureAwait(false)
                    : "TCP 1.1.1.1:443=در حال بررسی";
                return (true, $"{tcpProbeResult}; {result.Message}");
            }

            errors.Add(result.Message);
        }

        var tcpResult = await tcpTask.ConfigureAwait(false);
        return (false, tcpResult + " | " + string.Join(" | ", errors));
    }

    private static async Task<string> ProbeTcpAsync()
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var tcpCts = new CancellationTokenSource(ValidationTcpTimeout);
            await tcp.ConnectAsync("1.1.1.1", 443, tcpCts.Token).ConfigureAwait(false);
            return "TCP 1.1.1.1:443=OK";
        }
        catch (Exception ex)
        {
            return "TCP 1.1.1.1:443=" + NormalizeDiagnosticException(ex);
        }
    }

    private static async Task<(bool Success, string Message)> ProbeHttpAsync(string endpoint)
    {
        var host = new System.Uri(endpoint).Host;
        try
        {
            using var handler = new HttpClientHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler)
            {
                Timeout = ValidationHttpTimeout
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            var code = (int)response.StatusCode;
            return code >= 200 && code < 500
                ? (true, $"{host}=HTTP {code}")
                : (false, $"{host}=HTTP {code}");
        }
        catch (Exception ex)
        {
            return (false, $"{host}={NormalizeDiagnosticException(ex)}");
        }
    }

    private static string NormalizeDiagnosticException(Exception ex)
    {
        var root = ex.GetBaseException();
        if (root is System.OperationCanceledException or TaskCanceledException)
            return "TIMEOUT";
        return root.Message.Replace("\r", " ").Replace("\n", " ").Trim();
    }


    private static string InjectTunFdIntoConfig(string xrayJson, int tunFd)
    {
        if (tunFd < 0)
            throw new InvalidOperationException($"Android TUN fd نامعتبر است: {tunFd}");

        var root = System.Text.Json.Nodes.JsonNode.Parse(xrayJson) as System.Text.Json.Nodes.JsonObject
            ?? throw new InvalidOperationException("ریشه کانفیگ Xray باید یک JSON object باشد.");

        var env = root["env"] as System.Text.Json.Nodes.JsonObject;
        if (env is null)
        {
            env = new System.Text.Json.Nodes.JsonObject();
            root["env"] = env;
        }

        env["xray.tun.fd"] = tunFd.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
    }

    private string[] GetPhysicalDnsServers()
    {
        try
        {
            var cm = (ConnectivityManager?)GetSystemService(ConnectivityService);
            var network = cm?.ActiveNetwork;
            var props = network is null ? null : cm?.GetLinkProperties(network);
            var values = props?.DnsServers?
                .Select(x => x?.HostAddress)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                // Prefer IPv4 for the first production Android tunnel.
                .OrderBy(x => x.Contains(':') ? 1 : 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (values is { Length: > 0 })
                return values;
        }
        catch { }

        // Last-resort fallbacks only. In the normal case the Wi-Fi/mobile DNS above
        // is used, which avoids relying on public DNS reachability.
        return new[] { "8.8.8.8", "1.1.1.1" };
    }

    private static string FormatDnsEndpoint(string address)
        => address.Contains(':', StringComparison.Ordinal)
            ? $"[{address}]:53"
            : $"{address}:53";

    private static void EnsureLibXraySuccess(string? response, string operation)
    {
        if (string.IsNullOrWhiteSpace(response))
            throw new InvalidOperationException($"{operation}: libXray پاسخ خالی داد.");

        using var doc = System.Text.Json.JsonDocument.Parse(response);
        var root = doc.RootElement;
        var success = root.TryGetProperty("success", out var successNode) && successNode.GetBoolean();
        if (success) return;

        var error = root.TryGetProperty("error", out var errorNode) ? errorNode.GetString() : null;
        throw new InvalidOperationException($"{operation}: {error ?? "خطای ناشناخته libXray"}");
    }


    private void PromoteToForeground(string text)
    {
        var notification = BuildNotification(text);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, notification);
    }

    private void EnsureNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager?.GetNotificationChannel(NotificationChannelId) is not null) return;
        var channel = new NotificationChannel(NotificationChannelId, "SaePar VPN", NotificationImportance.Low)
        {
            Description = "وضعیت اتصال VPN برنامه SaePar Tunnel"
        };
        manager?.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification(string text)
    {
        var launchIntent = PackageManager?.GetLaunchIntentForPackage(PackageName);
        var launchPending = launchIntent is null ? null : PendingIntent.GetActivity(
            this,
            101,
            launchIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        Notification.Builder builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, NotificationChannelId)
            : new Notification.Builder(this);

        builder
            .SetContentTitle("SaePar Tunnel")
            .SetContentText(text)
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
            .SetOngoing(true)
            .SetCategory(Notification.CategoryService)
            .SetOnlyAlertOnce(true);

        if (launchPending is not null)
            builder.SetContentIntent(launchPending);

        return builder.Build();
    }

    private void UpdateNotification(string text)
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.Notify(NotificationId, BuildNotification(text));
    }
}
#endif
