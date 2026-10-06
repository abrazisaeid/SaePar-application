#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Com.Saepar.Tunnel.Bridge;
using SaeParTunnel.Core.Services;

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
    public const string ExtraConfigToken = "config_token";
    public const string ExtraProfileId = "profile_id";
    public const string ExtraProfileName = "profile_name";
    public const string ExtraAllowedPackages = "allowed_packages";
    public const string ExtraMetricsPort = "metrics_port";
    private const string ExtraNotificationSession = "notification_session";
    private const string ExtraUserDisconnect = "user_disconnect";

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
    private readonly object _notificationGate = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly string _notificationSession = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? _trafficCts;
    private Task _trafficTask = Task.CompletedTask;
    private TrafficSnapshot? _trafficSnapshot;
    private string _notificationText = "در حال اتصال";
    private string _notificationProfileName = string.Empty;
    private bool _trafficUnavailable;
    private bool _isForeground;
    private volatile bool _destroyed;
    private int _disconnectRequested;
    private ParcelFileDescriptor? _vpnInterface;
    private bool _stopping;

    public override void OnCreate()
    {
        base.OnCreate();
        AndroidVpnRuntime.ReportServiceCreated();
        EnsureNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var action = intent?.Action ?? ActionConnect;
#if SAEPAR_NOTIFICATION_DIAGNOSTICS
        if (action == ActionNotificationDiagnostic)
        {
            lock (_notificationGate) _notificationProfileName = "آزمایش محلی";
            PromoteToForeground("در حال آزمایش اعلان");
            _ = Task.Run(() => StartNotificationDiagnosticAsync(
                intent?.GetStringExtra(ExtraConfigToken) ?? string.Empty,
                intent?.GetIntExtra(ExtraMetricsPort, 0) ?? 0));
            return StartCommandResult.NotSticky;
        }
#endif
        if (action == ActionDisconnect)
        {
            var session = intent?.GetStringExtra(ExtraNotificationSession);
            if (session is not null && session != _notificationSession)
            {
                // An action saved from an old notification must never stop a
                // subsequent connection or leave a newly-created service alive.
                lock (_notificationGate) { if (!_isForeground) StopSelf(startId); }
                return StartCommandResult.NotSticky;
            }
            if (intent?.GetBooleanExtra(ExtraUserDisconnect, false) == true)
                AndroidVpnRuntime.MarkUserStopRequested();
            RequestShutdown();
            PromoteToForeground("در حال قطع اتصال...");
            _ = Task.Run(StopTunnelAsync);
            return StartCommandResult.NotSticky;
        }

        // Promote immediately. Service work is intentionally moved off Android's
        // main thread to avoid ANR while libXray parses/starts the native core.
        lock (_notificationGate)
            _notificationProfileName = CleanProfileName(intent?.GetStringExtra(ExtraProfileName));
        PromoteToForeground("در حال برقراری VPN...");
        AndroidVpnRuntime.ReportStatus("service-running", "سرویس VPN اجرا شد؛ در حال آماده‌سازی رابط TUN...");

        var configToken = intent?.GetStringExtra(ExtraConfigToken) ?? string.Empty;
        var profileId = intent?.GetStringExtra(ExtraProfileId) ?? string.Empty;
        var profileName = intent?.GetStringExtra(ExtraProfileName) ?? "SaePar Tunnel";
        var allowedPackages = intent?.GetStringArrayExtra(ExtraAllowedPackages) ?? Array.Empty<string>();
        var metricsPort = intent?.GetIntExtra(ExtraMetricsPort, 0) ?? 0;

        _ = Task.Run(() => StartTunnelAsync(configToken, profileId, profileName, allowedPackages, metricsPort));
        return StartCommandResult.NotSticky;
    }

    public override void OnRevoke()
    {
        RequestShutdown();
        _ = Task.Run(StopTunnelAsync);
        base.OnRevoke();
    }

    public override void OnDestroy()
    {
        _destroyed = true;
        RequestShutdown();
        lock (_notificationGate) _isForeground = false;
        // Native start/stop may hold the Java bridge lock. Never wait for that
        // lock on Android's main thread; acknowledge shutdown after cleanup.
        _ = Task.Run(FinalizeDestroyedServiceAsync);
        base.OnDestroy();
    }

    private async Task FinalizeDestroyedServiceAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopping = true;
            await StopCoreAndInterfaceOnlyAsync().ConfigureAwait(false);
        }
        catch { }
        finally
        {
            AndroidVpnRuntime.ReportServiceStopped();
            _lifecycleGate.Release();
        }
    }

    private async Task StartTunnelAsync(string configToken, string profileId, string profileName, string[] allowedPackages, int metricsPort)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _shutdownCts.Token.ThrowIfCancellationRequested();
            _stopping = false;
            await StopCoreAndInterfaceOnlyAsync().ConfigureAwait(false);

            var configStore = new TunnelConfigurationStore(Path.Combine(
                Microsoft.Maui.Storage.FileSystem.CacheDirectory, "vpn-start"));
            var xrayJson = await configStore.ConsumeAsync(configToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(xrayJson))
                throw new InvalidOperationException("Android Xray configuration is empty.");

            // Match the currently working OneXray Android TUN layout as closely as
            // possible. 198.18.0.0/15 is the benchmarking range commonly used for
            // user-space TUN stacks and avoids colliding with home/mobile LANs.
            // Keep the first production tunnel IPv4-only until the data path is proven.
            const string tunAddress = "198.18.0.1";
            const string tunDns = "8.8.8.8";
            const int tunMtu = 1400;
            // Capture before Establish changes ActiveNetwork to our own VPN.
            var physicalDns = GetPhysicalDnsServers()[0];

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
            SaeParXrayBridge.AttachTun(this, _vpnInterface, FormatDnsEndpoint(physicalDns));

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
            UpdateNotification("در حال بررسی اتصال");

            var validation = await ValidateTunnelTrafficAsync(_shutdownCts.Token).ConfigureAwait(false);
            _shutdownCts.Token.ThrowIfCancellationRequested();
            if (validation.Success)
            {
                AndroidVpnRuntime.ReportStatus(
                    "data-plane-ok",
                    $"مسیر TUN → Xray → Internet تأیید شد ({validation.Message}) • fd={tunFd} • DNS={tunDns}.",
                    null,
                    profileId);
                AndroidVpnRuntime.SignalConnected(profileId);
                UpdateNotification("متصل");
                if (metricsPort != 0) StartTrafficUpdates(metricsPort);
                return;
            }

            await StopCoreAndInterfaceOnlyAsync().ConfigureAwait(false);
            AndroidVpnRuntime.SignalError(
                $"تست اینترنت از VPN تأیید نشد • fd={tunFd} • TUN={tunAddress}/32 • DNS={tunDns}: " + validation.Message,
                startupFailure: false);
            RemoveForegroundNotification();
            StopSelf();
        }
        catch (Exception) when (_shutdownCts.IsCancellationRequested)
        {
            try { await StopCoreAndInterfaceOnlyAsync().ConfigureAwait(false); } catch { }
            RemoveForegroundNotification();
            StopSelf();
        }
        catch (Exception ex)
        {
            try { await StopCoreAndInterfaceOnlyAsync().ConfigureAwait(false); } catch { }
            AndroidVpnRuntime.SignalError(ex.Message);
            RemoveForegroundNotification();
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
            RemoveForegroundNotification();
            StopSelf();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopCoreAndInterfaceOnlyAsync()
    {
        await StopTrafficUpdatesAsync().ConfigureAwait(false);
        try { SaeParXrayBridge.DetachTun(); } catch { }
        try { _vpnInterface?.Close(); } catch { }
        _vpnInterface?.Dispose();
        _vpnInterface = null;
    }


    private static async Task<(bool Success, string Message)> ValidateTunnelTrafficAsync(CancellationToken cancellationToken)
    {
        using var validationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Run probes in parallel so a blocked or slow validation endpoint does not
        // keep the UI stuck on "testing internet" while another endpoint is healthy.
        var tcpTask = ProbeTcpAsync(validationCts.Token);
        var pendingHttp = ValidationEndpoints.Select(endpoint => ProbeHttpAsync(endpoint, validationCts.Token)).ToList();
        var errors = new List<string>();
        try
        {
            while (pendingHttp.Count > 0)
            {
                var completed = await Task.WhenAny(pendingHttp).ConfigureAwait(false);
                pendingHttp.Remove(completed);
                var result = await completed.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
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
        finally { validationCts.Cancel(); }
    }

    private static async Task<string> ProbeTcpAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var tcpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            tcpCts.CancelAfter(ValidationTcpTimeout);
            await tcp.ConnectAsync("1.1.1.1", 443, tcpCts.Token).ConfigureAwait(false);
            return "TCP 1.1.1.1:443=OK";
        }
        catch (Exception ex)
        {
            return "TCP 1.1.1.1:443=" + NormalizeDiagnosticException(ex);
        }
    }

    private static async Task<(bool Success, string Message)> ProbeHttpAsync(string endpoint, CancellationToken cancellationToken)
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
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            var code = (int)response.StatusCode;
            return code == 204
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
        lock (_notificationGate)
        {
            if (_destroyed) return;
            _notificationText = text;
            _trafficSnapshot = null;
            _trafficUnavailable = false;
            var notification = BuildNotification();
            if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
                StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
            else
                StartForeground(NotificationId, notification);
            _isForeground = true;
        }
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

    private Notification BuildNotification()
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

        if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
            builder.SetForegroundServiceBehavior((int)NotificationForegroundService.Immediate);

        builder
            .SetContentTitle("SaePar Tunnel • " + _notificationText)
            .SetContentText(_trafficSnapshot is { } traffic ? RateLine(traffic)
                : _trafficUnavailable ? "آمار موقتاً در دسترس نیست" : _notificationProfileName)
            .SetSmallIcon(global::SaeParTunnel.App.Resource.Drawable.ic_stat_saepar)
            .SetOngoing(true)
            .SetCategory(Notification.CategoryService)
            .SetOnlyAlertOnce(true)
            .SetShowWhen(false)
            .SetVisibility(NotificationVisibility.Private);

        var expanded = _notificationText;
        if (!string.IsNullOrWhiteSpace(_notificationProfileName)) expanded += "\nسرور: " + _notificationProfileName;
        if (_trafficSnapshot is { } snapshot)
        {
            expanded += "\n" + RateLine(snapshot)
                + "\nحجم ارسال: " + Ltr(XrayTrafficClient.FormatBytes(snapshot.SentBytes))
                + "\nحجم دریافت: " + Ltr(XrayTrafficClient.FormatBytes(snapshot.ReceivedBytes));
        }
        else if (_trafficUnavailable) expanded += "\nآمار موقتاً در دسترس نیست";
        builder.SetStyle(new Notification.BigTextStyle().BigText(expanded));

        if (Volatile.Read(ref _disconnectRequested) == 0)
        {
            var stopIntent = new Intent(this, typeof(SaeParVpnService));
            stopIntent.SetAction(ActionDisconnect);
            stopIntent.SetData(global::Android.Net.Uri.Parse("saepar://disconnect/" + _notificationSession));
            stopIntent.PutExtra(ExtraNotificationSession, _notificationSession);
            stopIntent.PutExtra(ExtraUserDisconnect, true);
            var stopPending = PendingIntent.GetService(this, 103, stopIntent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            if (stopPending is not null)
            {
                using var actionIcon = global::Android.Graphics.Drawables.Icon.CreateWithResource(
                    this, global::SaeParTunnel.App.Resource.Drawable.ic_stat_saepar);
                builder.AddAction(new Notification.Action.Builder(
                    actionIcon, "قطع VPN", stopPending).Build());
            }
        }

        if (launchPending is not null)
            builder.SetContentIntent(launchPending);

        return builder.Build();
    }

    private void UpdateNotification(string text)
    {
        lock (_notificationGate)
        {
            if (!_isForeground || _destroyed || Volatile.Read(ref _disconnectRequested) != 0) return;
            _notificationText = text;
            NotifyLocked();
        }
    }

    private void NotifyLocked()
        => ((NotificationManager?)GetSystemService(NotificationService))?.Notify(NotificationId, BuildNotification());

    private void RemoveForegroundNotification()
    {
        lock (_notificationGate)
        {
            _isForeground = false;
            _trafficSnapshot = null;
            StopForeground(StopForegroundFlags.Remove);
        }
    }

    private void RequestShutdown()
    {
        Interlocked.Exchange(ref _disconnectRequested, 1);
        _shutdownCts.Cancel();
        lock (_notificationGate) _trafficCts?.Cancel();
    }

    private void StartTrafficUpdates(int metricsPort)
    {
        lock (_notificationGate)
        {
            if (_destroyed || !_isForeground || Volatile.Read(ref _disconnectRequested) != 0) return;
            var cts = new CancellationTokenSource();
            _trafficCts = cts;
            _trafficTask = Task.Run(() => RunTrafficUpdatesAsync(metricsPort, cts.Token));
        }
    }

    private async Task RunTrafficUpdatesAsync(int metricsPort, CancellationToken cancellationToken)
    {
        using var client = new XrayTrafficClient(metricsPort);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var tracker = new TrafficRateTracker();
        do
        {
            TrafficSnapshot? sample = null;
            try { sample = tracker.Update(await client.ReadAsync(cancellationToken).ConfigureAwait(false), watch.Elapsed); }
            catch (System.OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception) { /* A failed statistics read is not a VPN failure. */ }
            lock (_notificationGate)
            {
                if (cancellationToken.IsCancellationRequested || !_isForeground || _destroyed ||
                    Volatile.Read(ref _disconnectRequested) != 0) break;
                _trafficSnapshot = sample;
                _trafficUnavailable = sample is null;
                try { NotifyLocked(); } catch (Exception) { /* Notifications may be disabled by the user. */ }
            }
        } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task StopTrafficUpdatesAsync()
    {
        CancellationTokenSource? cts;
        Task task;
        lock (_notificationGate)
        {
            cts = _trafficCts;
            task = _trafficTask;
            _trafficCts = null;
            _trafficTask = Task.CompletedTask;
            cts?.Cancel();
        }
        try { await task.ConfigureAwait(false); } catch (Exception) { }
        finally { cts?.Dispose(); }
    }

    private static string Ltr(string value) => "\u2066" + value + "\u2069";
    private static string RateLine(TrafficSnapshot sample) => Ltr(
        "↑ " + XrayTrafficClient.FormatBytes(sample.SendBytesPerSecond) + "/s    ↓ "
        + XrayTrafficClient.FormatBytes(sample.ReceiveBytesPerSecond) + "/s");
    private static string CleanProfileName(string? name)
    {
        var text = (name ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 80 ? text[..80] : text;
    }

#if SAEPAR_NOTIFICATION_DIAGNOSTICS
    internal const string ActionNotificationDiagnostic = "com.saepar.tunnel.action.LOCAL_NOTIFICATION_TEST";
    internal static TaskCompletionSource<bool> NotificationDiagnosticReady { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Debug opt-in only. Runs a loopback-only SOCKS configuration without
    // establishing a VPN or contacting public servers. Production has no hook.
    private async Task StartNotificationDiagnosticAsync(string token, int metricsPort)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var store = new TunnelConfigurationStore(Path.Combine(Microsoft.Maui.Storage.FileSystem.CacheDirectory, "vpn-start"));
            var config = await store.ConsumeAsync(token).ConfigureAwait(false);
            var response = SaeParXrayBridge.Invoke(System.Text.Json.JsonSerializer.Serialize(new
            {
                apiVersion = 1, method = "runXrayFromJson", payload = new { configJSON = config }
            }));
            EnsureLibXraySuccess(response, "آزمایش محلی");
            UpdateNotification("متصل");
            StartTrafficUpdates(metricsPort);
            NotificationDiagnosticReady.TrySetResult(true);
        }
        catch (Exception ex)
        {
            NotificationDiagnosticReady.TrySetException(ex);
            await StopCoreAndInterfaceOnlyAsync().ConfigureAwait(false);
            RemoveForegroundNotification();
            StopSelf();
        }
        finally { _lifecycleGate.Release(); }
    }
#endif
}
#endif
