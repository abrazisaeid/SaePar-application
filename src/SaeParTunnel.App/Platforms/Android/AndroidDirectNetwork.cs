#if ANDROID
using Android.Content;
using Android.Net;
using Android.OS;
using System.Net;
using System.Net.Sockets;
using SaeParTunnel.Core.Abstractions;
using SaeParTunnel.Core.Models;
using Network = Android.Net.Network;
using SocketType = System.Net.Sockets.SocketType;
using OperationCanceledException = System.OperationCanceledException;

namespace SaeParTunnel.App.Platforms.Android;

// Only discovery sockets bypass the VPN. Never bind the main app process:
// post-connect validation must still exercise the real TUN.
internal sealed class AndroidDirectNetwork : IEndpointConnector
{
    private static readonly AsyncLocal<Network?> SelectedNetwork = new();
    private static readonly SemaphoreSlim ResolverGate = new(4, 4);
    internal static ConnectivityManager Manager =>
        (ConnectivityManager)global::Android.App.Application.Context.GetSystemService(Context.ConnectivityService)!;

    internal static Network GetNetwork()
    {
        var cm = Manager;
        if (SelectedNetwork.Value is { } selected)
        {
            EnsureAvailable(selected);
            return selected;
        }
        var active = cm.ActiveNetwork;
#pragma warning disable CA1422 // One bounded snapshot per search; supports minimum API 24.
        var networks = cm.GetAllNetworks()
            .Where(network => IsPhysical(cm.GetNetworkCapabilities(network)))
            .OrderByDescending(network => network.Equals(active))
            .ThenByDescending(network => cm.GetNetworkCapabilities(network)?.HasCapability(NetCapability.Validated) == true)
            .ThenByDescending(network => cm.GetNetworkCapabilities(network)?.HasTransport(TransportType.Wifi) == true);
        return networks.FirstOrDefault() ?? throw Unavailable();
#pragma warning restore CA1422
    }

    internal static void EnsureAvailable(Network network)
    {
        if (!IsPhysical(Manager.GetNetworkCapabilities(network))) throw Unavailable();
    }

    private static bool IsPhysical(NetworkCapabilities? capabilities) =>
        capabilities?.HasCapability(NetCapability.Internet) == true &&
        capabilities.HasCapability(NetCapability.NotVpn) && !capabilities.HasTransport(TransportType.Vpn);

    private static TunnelStartupException Unavailable() => new(
        "اینترنت مستقیم گوشی قطع یا عوض شد؛ جست‌وجو متوقف شد و سرورهای ذخیره‌شده حفظ شدند.");

    internal static IDisposable BeginScope()
    {
        var previous = SelectedNetwork.Value;
        SelectedNetwork.Value = GetNetwork();
        return new Scope(() => SelectedNetwork.Value = previous);
    }
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }

    public HttpMessageHandler CreateHttpHandler() => new SocketsHttpHandler
    {
        UseProxy = false,
        // A pooled connection must not carry a later search onto an old network.
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectCallback = (context, token) => ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, token)
    };

    public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var network = GetNetwork();
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal)) addresses = new[] { literal };
        else
        {
            try
            {
                await ResolverGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                addresses = await Task.Run(() =>
                {
                    try
                    {
                        var values = network.GetAllByName(host) ?? Array.Empty<Java.Net.InetAddress>();
                        try { return values.Select(value => IPAddress.Parse(value.HostAddress!)).ToArray(); }
                        finally { foreach (var value in values) value.Dispose(); }
                    }
                    finally { ResolverGate.Release(); }
                }).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { EnsureAvailable(network); throw; }
        }

        Exception? lastError = null;
        foreach (var address in addresses.OrderBy(ip => ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).Take(4))
        {
            EnsureAvailable(network);
            var socket = new System.Net.Sockets.Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // Dup shares the socket's network mark. Close only the duplicate;
                // the managed socket keeps ownership of its original descriptor.
                try
                {
                    using var duplicate = ParcelFileDescriptor.FromFd(socket.Handle.ToInt32())
                        ?? throw new IOException("Socket descriptor duplication failed.");
                    try { network.BindSocket(duplicate.FileDescriptor!); }
                    finally { duplicate.Close(); }
                }
                catch (Exception ex)
                {
                    throw new TunnelStartupException("اتصال تست به اینترنت مستقیم گوشی ممکن نشد؛ جست‌وجو متوقف شد.", ex);
                }
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
                EnsureAvailable(network);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) { socket.Dispose(); throw; }
            catch (TunnelStartupException) { socket.Dispose(); throw; }
            catch (Exception ex)
            {
                socket.Dispose();
                EnsureAvailable(network);
                lastError = ex;
            }
        }
        throw lastError ?? new SocketException((int)SocketError.HostNotFound);
    }
}
#endif
