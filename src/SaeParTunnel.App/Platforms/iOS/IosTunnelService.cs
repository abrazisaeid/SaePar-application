#if IOS
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Foundation;
using Microsoft.Maui.Storage;
using NetworkExtension;
using SaeParTunnel.Core.Abstractions;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;
using SaeParTunnel.iOSBinding;

namespace SaeParTunnel.App.Platforms.iOS;

public sealed class IosTunnelService : ITunnelService
{
    private static readonly SemaphoreSlim LibXrayTestGate = new(1, 1);
    private static readonly string[] ValidationEndpoints =
    {
        "https://cp.cloudflare.com/generate_204",
        "https://www.gstatic.com/generate_204"
    };

    private readonly EndpointPrecheckService _precheck;
    private readonly XrayConfigBuilder _configBuilder;
    private readonly SemaphoreSlim _managerGate = new(1, 1);
    private NETunnelProviderManager? _manager;

    public IosTunnelService(
        EndpointPrecheckService precheck,
        XrayConfigBuilder configBuilder)
    {
        _precheck = precheck;
        _configBuilder = configBuilder;
    }

    public PlatformCapabilities Capabilities { get; } = new(
        "iOS",
        "NetworkExtension + XTLS libXray v26.7.28",
        true,
        true,
        false,
        true,
        "VPN واقعی iOS فعال است. برنامه‌ها به‌صورت Full Tunnel عبور می‌کنند، شبکه محلی مستقیم می‌ماند و فهرست سفید وب‌سایت‌ها پشتیبانی می‌شود.");

    public bool IsConnected => _manager?.Connection.Status == NEVpnStatus.Connected;

    public async Task EnsureReadyAsync(
        AppSettings settings,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = await GetManagerAsync(cancellationToken);
        progress?.Report(1);
    }

    public async Task<TestResult> TestAsync(
        ConfigProfile profile,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (profile.Health == ProfileHealth.Unsupported)
            return new TestResult(false, null, profile.TestMessage, ValidationLevel.None);

        if (settings.FastTestMode && !string.Equals(profile.Network, "mkcp", StringComparison.OrdinalIgnoreCase))
        {
            var precheck = await _precheck.TestAsync(
                profile,
                settings.FastTestMode ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(4),
                cancellationToken).ConfigureAwait(false);
            if (!precheck.Success)
                return precheck;
        }

        await LibXrayTestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? configPath = null;
        try
        {
            var socksPort = FindFreeLoopbackPort();
            var config = _configBuilder.BuildIosProbe(profile, socksPort);
            configPath = Path.Combine(
                FileSystem.CacheDirectory,
                $"saepar-ios-probe-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(configPath, config, cancellationToken).ConfigureAwait(false);

            var request = JsonSerializer.Serialize(new
            {
                apiVersion = 1,
                method = "ping",
                payload = new
                {
                    configPath,
                    timeout = settings.FastTestMode ? 4 : 7,
                    url = "https://cp.cloudflare.com/",
                    proxy = $"socks5://127.0.0.1:{socksPort}"
                }
            });
            var response = await Task.Run(
                () => LibXray.Invoke(request),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return ParsePingResponse(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TestResult(false, null, $"libXray: {ex.GetBaseException().Message}", ValidationLevel.None);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(configPath))
            {
                try { File.Delete(configPath); } catch { }
            }

            LibXrayTestGate.Release();
        }
    }

    public async Task<TestResult> TestCurrentConnectionAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var manager = await GetManagerAsync(cancellationToken);
        if (manager.Connection.Status != NEVpnStatus.Connected)
            return new TestResult(false, null, "VPN iOS متصل نیست.", ValidationLevel.None);

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = TimeSpan.FromSeconds(settings.FastTestMode ? 4 : 7);
        var pending = ValidationEndpoints
            .Select(endpoint => ProbeTunnelEndpointAsync(endpoint, timeout, probeCts.Token))
            .ToList();
        var errors = new List<string>();

        try
        {
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                var result = await completed.ConfigureAwait(false);
                if (result.Success)
                {
                    probeCts.Cancel();
                    return result;
                }

                errors.Add(result.Message);
            }
        }
        finally
        {
            if (pending.Count > 0)
            {
                probeCts.Cancel();
                try { await Task.WhenAll(pending).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }

        return new TestResult(
            false,
            null,
            "تست اینترنت iOS: " + string.Join(" | ", errors),
            ValidationLevel.FullProxy);
    }

    public async Task ConnectAsync(
        ConfigProfile profile,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manager = await GetManagerAsync(cancellationToken);
        if (manager.Connection.Status == NEVpnStatus.Connected)
            return;

        var launchRequest = JsonSerializer.Serialize(
            new IosTunnelRequest(profile, settings),
            IosTunnelJsonContext.Default.IosTunnelRequest);
        using var requestKey = new NSString(IosTunnelContract.ProviderRequestKey);
        using var requestValue = new NSString(launchRequest);
        using var providerConfiguration = new NSDictionary<NSString, NSObject>(
            requestKey,
            requestValue);

        using var protocol = new NETunnelProviderProtocol
        {
            ProviderBundleIdentifier = IosTunnelContract.ExtensionBundleIdentifier,
            ProviderConfiguration = providerConfiguration,
            ServerAddress = string.IsNullOrWhiteSpace(profile.Address)
                ? "SaePar Tunnel"
                : profile.Address,
            DisconnectOnSleep = false
        };

        manager.ProtocolConfiguration = protocol;
        manager.LocalizedDescription = "SaePar Tunnel";
        manager.Enabled = true;
        await SaveToPreferencesAsync(manager, cancellationToken);
        await LoadFromPreferencesAsync(manager, cancellationToken);

        if (!manager.Connection.StartVpnTunnel(out var startError))
        {
            throw new InvalidOperationException(
                startError?.LocalizedDescription ?? "iOS could not start the VPN tunnel.");
        }

        try
        {
            await WaitForConnectedAsync(
                manager.Connection,
                TimeSpan.FromSeconds(20),
                cancellationToken);
        }
        catch
        {
            manager.Connection.StopVpnTunnel();
            throw;
        }
    }

    public async Task DisconnectAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var manager = await GetManagerAsync(cancellationToken);
        if (manager.Connection.Status is NEVpnStatus.Disconnected or NEVpnStatus.Invalid)
            return;

        manager.Connection.StopVpnTunnel();
        await WaitForDisconnectedAsync(
            manager.Connection,
            TimeSpan.FromSeconds(10),
            cancellationToken);
    }

    private async Task<NETunnelProviderManager> GetManagerAsync(CancellationToken cancellationToken)
    {
        if (_manager is not null)
            return _manager;

        await _managerGate.WaitAsync(cancellationToken);
        try
        {
            if (_manager is not null)
                return _manager;

            var managers = await NETunnelProviderManager.LoadAllFromPreferencesAsync()
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            _manager = managers.OfType<NETunnelProviderManager>().FirstOrDefault(manager =>
                    manager.ProtocolConfiguration is NETunnelProviderProtocol protocol &&
                    string.Equals(
                        protocol.ProviderBundleIdentifier,
                        IosTunnelContract.ExtensionBundleIdentifier,
                        StringComparison.Ordinal))
                ?? new NETunnelProviderManager();
            return _manager;
        }
        finally
        {
            _managerGate.Release();
        }
    }

    private static Task SaveToPreferencesAsync(
        NETunnelProviderManager manager,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.SaveToPreferences(error =>
        {
            if (error is null)
                completion.TrySetResult();
            else
                completion.TrySetException(new InvalidOperationException(error.LocalizedDescription));
        });
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
    }

    private static Task LoadFromPreferencesAsync(
        NETunnelProviderManager manager,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.LoadFromPreferences(error =>
        {
            if (error is null)
                completion.TrySetResult();
            else
                completion.TrySetException(new InvalidOperationException(error.LocalizedDescription));
        });
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    private static async Task WaitForConnectedAsync(
        NEVpnConnection connection,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var sawConnecting = false;
        while (timer.Elapsed < timeout)
        {
            var status = connection.Status;
            if (status == NEVpnStatus.Connected)
                return;
            if (status is NEVpnStatus.Connecting or NEVpnStatus.Reasserting)
                sawConnecting = true;
            if (status == NEVpnStatus.Invalid ||
                (sawConnecting && status == NEVpnStatus.Disconnected))
            {
                throw new InvalidOperationException("iOS stopped the VPN before it connected.");
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("راه‌اندازی VPN iOS بیشتر از ۲۰ ثانیه طول کشید.");
    }

    private static async Task WaitForDisconnectedAsync(
        NEVpnConnection connection,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (connection.Status is NEVpnStatus.Disconnected or NEVpnStatus.Invalid)
                return;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("قطع VPN iOS بیشتر از ۱۰ ثانیه طول کشید.");
    }

    private static async Task<TestResult> ProbeTunnelEndpointAsync(
        string endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var host = new Uri(endpoint).Host;
        try
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            };
            using var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SaeParTunnel-iOS/2.0");

            var timer = Stopwatch.StartNew();
            using var response = await client.GetAsync(
                endpoint,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            timer.Stop();

            var statusCode = (int)response.StatusCode;
            return response.StatusCode == HttpStatusCode.NoContent
                ? new TestResult(
                    true,
                    (int)timer.ElapsedMilliseconds,
                    $"{host}=HTTP {statusCode} • {timer.ElapsedMilliseconds:N0} ms",
                    ValidationLevel.FullProxy)
                : new TestResult(false, null, $"{host}=HTTP {statusCode}", ValidationLevel.FullProxy);
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
            return new TestResult(
                false,
                null,
                $"{host}={ex.GetBaseException().Message}",
                ValidationLevel.FullProxy);
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

        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        var success = root.TryGetProperty("success", out var successNode) && successNode.GetBoolean();
        var error = root.TryGetProperty("error", out var errorNode) ? errorNode.GetString() : null;

        long? delay = null;
        if (root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("delay", out var delayNode) &&
            delayNode.TryGetInt64(out var parsedDelay))
        {
            delay = parsedDelay;
        }

        if (!success || delay is null || delay < 0 || delay >= 10000)
            return new TestResult(false, null, error ?? "Full proxy test ناموفق بود.", ValidationLevel.None);

        var latency = delay > int.MaxValue ? int.MaxValue : (int)delay.Value;
        return new TestResult(
            true,
            latency,
            $"Full proxy OK • {latency} ms",
            ValidationLevel.FullProxy);
    }
}
#endif
