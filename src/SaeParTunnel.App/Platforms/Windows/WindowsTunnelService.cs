#if WINDOWS
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using SaeParTunnel.App.Services;
using SaeParTunnel.Core.Abstractions;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.App.Platforms.Windows;

public sealed class WindowsTunnelService : ITunnelService
{
    private const int MaxConcurrentXrayTests = 6;
    private static readonly ConcurrentDictionary<int, byte> ReservedPorts = new();
    private static readonly SemaphoreSlim XrayInstallGate = new(1, 1);
    private static readonly SemaphoreSlim XrayTestGate = new(MaxConcurrentXrayTests, MaxConcurrentXrayTests);
    private static readonly string[] ProxyValidationEndpoints =
    {
        "https://cp.cloudflare.com/generate_204",
        "https://www.gstatic.com/generate_204",
        "https://www.msftconnecttest.com/connecttest.txt"
    };
    private readonly XrayConfigBuilder _builder;
    private readonly EndpointPrecheckService _precheck;
    private readonly MauiJsonStore _store;
    private Process? _process;

    public WindowsTunnelService(XrayConfigBuilder builder, EndpointPrecheckService precheck, MauiJsonStore store)
    {
        _builder = builder;
        _precheck = precheck;
        _store = store;
    }

    public PlatformCapabilities Capabilities { get; } = new(
        "Windows",
        "Xray Core process + System Proxy",
        true,
        true,
        true,
        true,
        "Windows v1.x functionality is retained. TUN/Wintun can replace System Proxy in a later step without changing the shared UI/Core.");

    public bool IsConnected => _process is { HasExited: false };

    public async Task EnsureReadyAsync(AppSettings settings, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        _store.EnsureCreated();
        if (!string.IsNullOrWhiteSpace(settings.XrayPath) && File.Exists(settings.XrayPath)) return;

        await XrayInstallGate.WaitAsync(cancellationToken);
        try
        {
            // Another test worker may have completed installation while this one
            // was waiting. Always check again while holding the install gate.
            if (!string.IsNullOrWhiteSpace(settings.XrayPath) && File.Exists(settings.XrayPath)) return;

            var localCandidates = new[]
            {
                Path.Combine(_store.RuntimePath, "xray.exe"),
                Path.Combine(AppContext.BaseDirectory, "xray.exe")
            };
            var local = localCandidates.FirstOrDefault(File.Exists);
            if (local is not null)
            {
                settings.XrayPath = local;
                await _store.SaveSettingsAsync(settings);
                return;
            }

            settings.XrayPath = await InstallLatestXrayAsync(progress, cancellationToken);
            await _store.SaveSettingsAsync(settings);
        }
        finally
        {
            XrayInstallGate.Release();
        }
    }

    public async Task<TestResult> TestAsync(ConfigProfile profile, AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (profile.Health == ProfileHealth.Unsupported)
            return new TestResult(false, null, profile.TestMessage, ValidationLevel.FullProxy);

        await EnsureReadyAsync(settings, cancellationToken: cancellationToken);
        if (settings.FastTestMode && !string.Equals(profile.Network, "mkcp", StringComparison.OrdinalIgnoreCase))
        {
            var pre = await _precheck.TestAsync(profile, TimeSpan.FromSeconds(2), cancellationToken);
            if (!pre.Success) return pre with { Level = ValidationLevel.FullProxy };
        }

        await XrayTestGate.WaitAsync(cancellationToken);
        try
        {
            var (socks, http) = ReservePortPair();
            var temp = Path.Combine(_store.RuntimePath, $"test-{Guid.NewGuid():N}.json");
            Process? proc = null;
            var diagnostics = new ConcurrentQueue<string>();
            try
            {
                var json = _builder.Build(profile, socks, http, testMode: true);
                await File.WriteAllTextAsync(temp, json, cancellationToken);
                proc = StartXray(settings.XrayPath, temp, diagnostics);
                if (!await WaitForPortAsync(http, proc, settings.FastTestMode ? 3 : 5, cancellationToken))
                    return new TestResult(false, null, proc.HasExited ? "Xray کانفیگ را نپذیرفت. " + Tail(diagnostics) : "Proxy محلی آماده نشد. " + Tail(diagnostics), ValidationLevel.FullProxy);

                var validation = await ProbeProxyEndpointsAsync(
                    http,
                    TimeSpan.FromSeconds(settings.FastTestMode ? 4 : 7),
                    cancellationToken);
                return validation.Success
                    ? validation with { Message = "اتصال واقعی از Proxy برقرار شد • " + validation.Message }
                    : validation with { Message = validation.Message + " • " + Tail(diagnostics) };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return new TestResult(false, null, ex.Message + " " + Tail(diagnostics), ValidationLevel.FullProxy); }
            finally
            {
                try { if (proc is { HasExited: false }) { proc.Kill(true); await proc.WaitForExitAsync(CancellationToken.None); } } catch { }
                proc?.Dispose();
                try { File.Delete(temp); } catch { }
                ReleasePortPair(socks, http);
            }
        }
        finally
        {
            XrayTestGate.Release();
        }
    }

    public async Task<TestResult> TestCurrentConnectionAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            return new TestResult(false, null, "Xray محلی فعال نیست.", ValidationLevel.None);

        return await ProbeProxyEndpointsAsync(
            settings.HttpPort,
            TimeSpan.FromSeconds(settings.FastTestMode ? 3 : 5),
            cancellationToken);
    }

    private static async Task<TestResult> ProbeProxyEndpointsAsync(
        int httpPort,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = ProxyValidationEndpoints
            .Select(endpoint => ProbeProxyEndpointAsync(endpoint, httpPort, timeout, probeCts.Token))
            .ToList();
        var errors = new List<string>();
        TestResult? successful = null;

        try
        {
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                var result = await completed;
                if (result.Success)
                {
                    successful = result;
                    break;
                }

                errors.Add(result.Message);
            }
        }
        finally
        {
            if (pending.Count > 0)
            {
                probeCts.Cancel();
                try { await Task.WhenAll(pending); }
                catch (OperationCanceledException) { }
            }
        }

        if (successful is not null) return successful;
        return new TestResult(false, null, "تست اینترنت: " + string.Join(" | ", errors), ValidationLevel.FullProxy);
    }

    private static async Task<TestResult> ProbeProxyEndpointAsync(
        string endpoint,
        int httpPort,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var host = new Uri(endpoint).Host;
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{httpPort}"),
                UseProxy = true,
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SaeParTunnel/2.0");

            var sw = Stopwatch.StartNew();
            using var response = await client.GetAsync(
                endpoint,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            sw.Stop();

            var code = (int)response.StatusCode;
            return code >= 200 && code < 500 && code != (int)HttpStatusCode.ProxyAuthenticationRequired
                ? new TestResult(true, (int)sw.ElapsedMilliseconds, $"{host}=HTTP {code} • {sw.ElapsedMilliseconds:N0} ms", ValidationLevel.FullProxy)
                : new TestResult(false, null, $"{host}=HTTP {code}", ValidationLevel.FullProxy);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new TestResult(false, null, $"{host}=TIMEOUT", ValidationLevel.FullProxy);
        }
        catch (Exception ex)
        {
            return new TestResult(false, null, $"{host}={ex.GetBaseException().Message}", ValidationLevel.FullProxy);
        }
    }

    public async Task ConnectAsync(ConfigProfile profile, AppSettings settings, CancellationToken cancellationToken = default)
    {
        // A debugger stop or a different proxy client can leave 10808/10809 busy.
        // Do not kill arbitrary xray.exe processes owned by the user; select free
        // local ports automatically and retry only when Xray reports a bind failure.
        await DisconnectAsync(settings, cancellationToken);
        await EnsureReadyAsync(settings, cancellationToken: cancellationToken);

        var configPath = Path.Combine(_store.RuntimePath, "current-config.json");
        var failures = new List<string>();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var ports = ChooseConnectionPortPair(settings.SocksPort, settings.HttpPort, forceNew: attempt > 0);
            settings.SocksPort = ports.Socks;
            settings.HttpPort = ports.Http;

            var json = _builder.Build(profile, settings.SocksPort, settings.HttpPort, settings: settings);
            await File.WriteAllTextAsync(configPath, json, cancellationToken);

            var diagnostics = new ConcurrentQueue<string>();
            _process = StartXray(settings.XrayPath, configPath, diagnostics);
            if (await WaitForPortAsync(settings.HttpPort, _process, 5, cancellationToken))
            {
                if (settings.EnableSystemProxy) EnableSystemProxy(settings);
                await _store.SaveSettingsAsync(settings);
                return;
            }

            var detail = Tail(diagnostics);
            failures.Add(detail);
            await StopProcessOnlyAsync(cancellationToken);

            if (!IsBindFailure(detail))
                throw new InvalidOperationException("Xray اجرا شد ولی Proxy محلی آماده نشد. " + detail);
        }

        throw new InvalidOperationException(
            "پورت محلی Xray چند بار درگیر بود و اتصال ساخته نشد. " +
            string.Join(" | ", failures.Where(x => !string.IsNullOrWhiteSpace(x)).TakeLast(3)));
    }

    public async Task DisconnectAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var p = _process;
        _process = null;
        try
        {
            if (p is { HasExited: false }) { p.Kill(true); await p.WaitForExitAsync(cancellationToken); }
        }
        catch { }
        finally { p?.Dispose(); }
        if (settings.ProxyWasManaged) RestoreSystemProxy(settings);
        await _store.SaveSettingsAsync(settings);
    }

    private async Task<string> InstallLatestXrayAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_store.RuntimePath);
        const string latestApi = "https://api.github.com/repos/XTLS/Xray-core/releases/latest";
        var errors = new List<string>();

        string? releaseJson = null;
        foreach (var direct in new[] { false, true })
        {
            try
            {
                using var handler = new HttpClientHandler { UseProxy = !direct };
                using var client = CreateDownloadClient(handler);
                releaseJson = await client.GetStringAsync(latestApi, ct);
                if (!string.IsNullOrWhiteSpace(releaseJson)) break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                errors.Add($"GitHub API ({(direct ? "direct" : "system proxy")}): {FlattenException(ex)}");
            }
        }

        if (string.IsNullOrWhiteSpace(releaseJson))
            throw new HttpRequestException("دانلود خودکار Xray به GitHub دسترسی ندارد. از Settings > Windows گزینه Browse xray.exe را استفاده کن.\n" + string.Join("\n", errors.TakeLast(4)));

        using var doc = JsonDocument.Parse(releaseJson);
        string? url = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), "Xray-windows-64.zip", StringComparison.OrdinalIgnoreCase)) continue;
                url = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }
        if (url is null) throw new InvalidOperationException("Xray-windows-64.zip در آخرین Release رسمی پیدا نشد.");

        var zip = Path.Combine(_store.RuntimePath, "xray.zip");
        var downloaded = false;
        foreach (var direct in new[] { false, true })
        {
            try
            {
                using var handler = new HttpClientHandler { UseProxy = !direct };
                using var client = CreateDownloadClient(handler);
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(zip);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report(done * 100d / total.Value);
                }
                downloaded = true;
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                errors.Add($"Xray ZIP ({(direct ? "direct" : "system proxy")}): {FlattenException(ex)}");
                try { File.Delete(zip); } catch { }
            }
        }

        if (!downloaded)
            throw new HttpRequestException("دانلود Xray ناموفق بود. می‌توانی xray.exe را دستی از Settings > Windows انتخاب کنی.\n" + string.Join("\n", errors.TakeLast(4)));

        ZipFile.ExtractToDirectory(zip, _store.RuntimePath, true);
        File.Delete(zip);
        var xray = Path.Combine(_store.RuntimePath, "xray.exe");
        if (!File.Exists(xray)) throw new InvalidOperationException("xray.exe بعد از Extract پیدا نشد.");
        return xray;
    }

    private static HttpClient CreateDownloadClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SaeParTunnel", "2.0-preview12"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string FlattenException(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? current = ex; current is not null && parts.Count < 5; current = current.InnerException)
        {
            var message = (current.Message ?? string.Empty)
                .Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            if (message.Length > 0 && !parts.Contains(message, StringComparer.OrdinalIgnoreCase)) parts.Add(message);
        }
        return string.Join(" → ", parts);
    }

    private static Process StartXray(string exe, string config, ConcurrentQueue<string> diag)
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"run -c \"{config}\"",
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }, EnableRaisingEvents = true
        };
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) diag.Enqueue(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) diag.Enqueue(e.Data); };
        if (!p.Start()) throw new InvalidOperationException("اجرای Xray ممکن نشد.");
        p.BeginOutputReadLine(); p.BeginErrorReadLine(); return p;
    }

    private static async Task<bool> WaitForPortAsync(int port, Process p, int seconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (p.HasExited) return false;
            try { using var tcp = new TcpClient(); using var c = CancellationTokenSource.CreateLinkedTokenSource(ct); c.CancelAfter(350); await tcp.ConnectAsync("127.0.0.1", port, c.Token); return true; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { }
            await Task.Delay(100, ct);
        }
        return false;
    }

    private static (int Socks, int Http) ChooseConnectionPortPair(int requestedSocks, int requestedHttp, bool forceNew)
    {
        if (!forceNew &&
            IsValidUserPort(requestedSocks) && IsValidUserPort(requestedHttp) &&
            requestedSocks != requestedHttp &&
            IsPortAvailable(requestedSocks) && IsPortAvailable(requestedHttp))
        {
            return (requestedSocks, requestedHttp);
        }

        for (var i = 0; i < 1000; i++)
        {
            var socks = Random.Shared.Next(20000, 59000);
            var http = socks + 1;
            if (!IsPortAvailable(socks) || !IsPortAvailable(http)) continue;
            return (socks, http);
        }

        throw new InvalidOperationException("دو پورت آزاد برای Proxy محلی Xray پیدا نشد.");
    }

    private static bool IsValidUserPort(int port) => port > 1024 && port <= 65535;

    private static bool IsPortAvailable(int port)
    {
        if (ReservedPorts.ContainsKey(port)) return false;
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException) { return false; }
        finally { try { listener?.Stop(); } catch { } }
    }

    private async Task StopProcessOnlyAsync(CancellationToken cancellationToken)
    {
        var p = _process;
        _process = null;
        try
        {
            if (p is { HasExited: false })
            {
                p.Kill(true);
                await p.WaitForExitAsync(cancellationToken);
            }
        }
        catch { }
        finally { p?.Dispose(); }
    }

    private static bool IsBindFailure(string text) =>
        text.Contains("failed to listen", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase);

    private static (int Socks, int Http) ReservePortPair()
    {
        for (var i = 0; i < 3000; i++)
        {
            var s = Random.Shared.Next(22000, 50000); var h = s + 1;
            if (!IsPortAvailable(s) || !IsPortAvailable(h)) continue;
            if (!ReservedPorts.TryAdd(s, 0)) continue;
            if (ReservedPorts.TryAdd(h, 0)) return (s, h);
            ReservedPorts.TryRemove(s, out _);
        }
        throw new InvalidOperationException("پورت آزاد برای تست پیدا نشد؛ concurrency را کم کنید.");
    }
    private static void ReleasePortPair(int s, int h) { ReservedPorts.TryRemove(s, out _); ReservedPorts.TryRemove(h, out _); }
    private static string Tail(ConcurrentQueue<string> q) =>
        string.Join(" | ", q.TakeLast(4).Select(CompactDiagnosticLine));

    private static string CompactDiagnosticLine(string line)
    {
        const int maxLength = 260;
        if (line.Length <= maxLength) return line;
        return line[..80] + "…" + line[^170..];
    }

    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private static readonly string[] RequiredSystemProxyBypass =
    {
        "<local>",
        "localhost",
        "loopback",
        "127.*",
        "[::1]",
        "10.*",
        "169.254.*",
        "172.16.*",
        "172.17.*",
        "172.18.*",
        "172.19.*",
        "172.20.*",
        "172.21.*",
        "172.22.*",
        "172.23.*",
        "172.24.*",
        "172.25.*",
        "172.26.*",
        "172.27.*",
        "172.28.*",
        "172.29.*",
        "172.30.*",
        "172.31.*",
        "192.168.*",
        "*.local"
    };

    private static void EnableSystemProxy(AppSettings settings)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettings, true) ?? throw new InvalidOperationException("Internet Settings قابل دسترس نیست.");
        if (!settings.ProxyWasManaged)
        {
            settings.PreviousProxyEnabled = Convert.ToInt32(key.GetValue("ProxyEnable", 0)) == 1;
            settings.PreviousProxyServer = Convert.ToString(key.GetValue("ProxyServer", "")) ?? "";
            settings.PreviousProxyOverride = Convert.ToString(key.GetValue("ProxyOverride", "")) ?? "";
        }
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"http=127.0.0.1:{settings.HttpPort};https=127.0.0.1:{settings.HttpPort}");
        key.SetValue("ProxyOverride", BuildSystemProxyBypass(settings.PreviousProxyOverride), RegistryValueKind.String);
        settings.ProxyWasManaged = true; RefreshInternetOptions();
    }

    private static string BuildSystemProxyBypass(string? previousOverride) =>
        string.Join(';', RequiredSystemProxyBypass
            .Concat((previousOverride ?? string.Empty).Split(
                new[] { ';', ',' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !string.Equals(value, "<-loopback>", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static void RestoreSystemProxy(AppSettings settings)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettings, true); if (key is null) return;
        key.SetValue("ProxyEnable", settings.PreviousProxyEnabled ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", settings.PreviousProxyServer ?? "");
        key.SetValue("ProxyOverride", settings.PreviousProxyOverride ?? "");
        settings.ProxyWasManaged = false; RefreshInternetOptions();
    }
    private static void RefreshInternetOptions() { InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); }
    [DllImport("wininet.dll", SetLastError = true)] private static extern bool InternetSetOption(IntPtr h, int option, IntPtr buffer, int length);
}
#endif
