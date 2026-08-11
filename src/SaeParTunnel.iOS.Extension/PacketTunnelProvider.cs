using System.Text.Json;
using Foundation;
using NetworkExtension;
using ObjCRuntime;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;
using SaeParTunnel.iOSBinding;

namespace SaeParTunnel.iOS.Extension;

[Register("PacketTunnelProvider")]
public class PacketTunnelProvider : NEPacketTunnelProvider
{
    private const int TunnelMtu = 1400;
    private const string ErrorDomain = "com.saepar.tunnel.packet-tunnel";
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly XrayConfigBuilder _configBuilder = new();
    private bool _xrayRunning;

    public PacketTunnelProvider()
    {
    }

    protected PacketTunnelProvider(NativeHandle handle) : base(handle)
    {
    }

    public override void StartTunnel(
        NSDictionary<NSString, NSObject>? options,
        Action<NSError> completionHandler)
    {
        _ = StartTunnelCoreAsync(completionHandler);
    }

    public override void StopTunnel(NEProviderStopReason reason, Action completionHandler)
    {
        _ = StopTunnelCoreAsync(completionHandler);
    }

    private async Task StartTunnelCoreAsync(Action<NSError> completionHandler)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_xrayRunning)
            {
                completionHandler(null!);
                return;
            }

            var request = ReadLaunchRequest();
            var networkSettings = CreateNetworkSettings(request.Profile.Address);
            await SetTunnelNetworkSettingsAsync(networkSettings).ConfigureAwait(false);

            var descriptor = UtunFileDescriptor.Find();
            var xrayConfig = _configBuilder.BuildIosTun(
                request.Profile,
                request.Settings,
                descriptor,
                TunnelMtu);
            var response = LibXray.Invoke(JsonSerializer.Serialize(new
            {
                apiVersion = 1,
                method = "runXrayFromJson",
                payload = new { configJSON = xrayConfig }
            }));
            EnsureLibXraySuccess(response, "start");

            _xrayRunning = true;
            completionHandler(null!);
        }
        catch (Exception ex)
        {
            TryStopXray(force: true);
            completionHandler(ToNSError(ex));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopTunnelCoreAsync(Action completionHandler)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            TryStopXray(force: true);
        }
        finally
        {
            _lifecycleGate.Release();
            completionHandler();
        }
    }

    private IosTunnelRequest ReadLaunchRequest()
    {
        if (ProtocolConfiguration is not NETunnelProviderProtocol protocol ||
            protocol.ProviderConfiguration is null)
        {
            throw new InvalidOperationException("The iOS VPN provider configuration is missing.");
        }

        using var key = new NSString(IosTunnelContract.ProviderRequestKey);
        if (protocol.ProviderConfiguration.ObjectForKey(key) is not NSString requestJson ||
            string.IsNullOrWhiteSpace(requestJson.ToString()))
        {
            throw new InvalidOperationException("The iOS VPN launch request is missing.");
        }

        return JsonSerializer.Deserialize(
                requestJson.ToString(),
                IosTunnelJsonContext.Default.IosTunnelRequest)
            ?? throw new InvalidOperationException("The iOS VPN launch request is invalid.");
    }

    private static NEPacketTunnelNetworkSettings CreateNetworkSettings(string remoteAddress)
    {
        var ipv4 = new NEIPv4Settings(
            new[] { "10.73.0.2" },
            new[] { "255.255.255.0" })
        {
            IncludedRoutes = new[] { NEIPv4Route.DefaultRoute },
            ExcludedRoutes = new[]
            {
                new NEIPv4Route("10.0.0.0", "255.0.0.0"),
                new NEIPv4Route("172.16.0.0", "255.240.0.0"),
                new NEIPv4Route("192.168.0.0", "255.255.0.0"),
                new NEIPv4Route("169.254.0.0", "255.255.0.0")
            }
        };

        var ipv6 = new NEIPv6Settings(
            new[] { "fd73:6170:6172::2" },
            new[] { NSNumber.FromInt32(64) })
        {
            IncludedRoutes = new[] { NEIPv6Route.DefaultRoute },
            ExcludedRoutes = new[]
            {
                new NEIPv6Route("fc00::", NSNumber.FromInt32(7)),
                new NEIPv6Route("fe80::", NSNumber.FromInt32(10))
            }
        };

        var dns = new NEDnsSettings(new[] { "1.1.1.1", "8.8.8.8" })
        {
            MatchDomains = new[] { string.Empty }
        };

        return new NEPacketTunnelNetworkSettings(
            string.IsNullOrWhiteSpace(remoteAddress) ? "SaePar Tunnel" : remoteAddress)
        {
            IPv4Settings = ipv4,
            IPv6Settings = ipv6,
            DnsSettings = dns,
            Mtu = NSNumber.FromInt32(TunnelMtu)
        };
    }

    private void TryStopXray(bool force = false)
    {
        if (!_xrayRunning && !force)
            return;

        try
        {
            var response = LibXray.Invoke("{\"apiVersion\":1,\"method\":\"stopXray\",\"payload\":{}}");
            EnsureLibXraySuccess(response, "stop");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to stop libXray: {ex}");
        }
        finally
        {
            _xrayRunning = false;
        }
    }

    private static void EnsureLibXraySuccess(string response, string operation)
    {
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        if (root.TryGetProperty("success", out var success) && success.GetBoolean())
            return;

        var error = root.TryGetProperty("error", out var errorNode)
            ? errorNode.GetString()
            : null;
        throw new InvalidOperationException($"libXray {operation} failed: {error ?? "unknown error"}");
    }

    private static NSError ToNSError(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        using var userInfo = new NSDictionary(
            NSError.LocalizedDescriptionKey,
            new NSString(message));
        return NSError.FromDomain(new NSString(ErrorDomain), 1, userInfo);
    }
}
