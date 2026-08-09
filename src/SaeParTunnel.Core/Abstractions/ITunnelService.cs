using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Abstractions;

public sealed record PlatformCapabilities(
    string PlatformName,
    string BackendName,
    bool SupportsTunnel,
    bool SupportsFullProxyTest,
    bool SupportsApplicationWhitelist,
    bool SupportsWebsiteWhitelist,
    string Note);

public interface ITunnelService
{
    PlatformCapabilities Capabilities { get; }
    bool IsConnected { get; }
    Task EnsureReadyAsync(AppSettings settings, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<TestResult> TestAsync(ConfigProfile profile, AppSettings settings, CancellationToken cancellationToken = default);
    Task<TestResult> TestCurrentConnectionAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task ConnectAsync(ConfigProfile profile, AppSettings settings, CancellationToken cancellationToken = default);
    Task DisconnectAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
