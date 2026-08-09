using SaeParTunnel.Core.Abstractions;
using SaeParTunnel.Core.Models;

namespace SaeParTunnel.App.Services;

public sealed class UnsupportedTunnelService : ITunnelService
{
    public PlatformCapabilities Capabilities { get; } = new("Unknown", "Unavailable", false, false, false, true, "این پلتفرم هنوز Backend ندارد.");
    public bool IsConnected => false;
    public Task EnsureReadyAsync(AppSettings settings, IProgress<double>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<TestResult> TestAsync(ConfigProfile profile, AppSettings settings, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TestResult(false, null, "Backend روی این پلتفرم موجود نیست.", ValidationLevel.None));
    public Task<TestResult> TestCurrentConnectionAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TestResult(false, null, "اتصال فعالی برای تست وجود ندارد.", ValidationLevel.None));
    public Task ConnectAsync(ConfigProfile profile, AppSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException(Capabilities.Note);
    public Task DisconnectAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
