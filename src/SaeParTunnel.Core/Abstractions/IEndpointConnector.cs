namespace SaeParTunnel.Core.Abstractions;

// The Android implementation pins both DNS and sockets to a non-VPN network.
public interface IEndpointConnector
{
    ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken);
}
