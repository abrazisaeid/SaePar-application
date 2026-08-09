#if IOS
using SaeParTunnel.Core.Abstractions;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.App.Platforms.iOS;

public sealed class IosTunnelService : ITunnelService
{
    private readonly EndpointPrecheckService _precheck;
    public IosTunnelService(EndpointPrecheckService precheck) => _precheck = precheck;

    public PlatformCapabilities Capabilities { get; } = new(
        "iOS",
        "NetworkExtension + libXray XCFramework (مرحله بعد)",
        false,
        false,
        false,
        true,
        "UI و منطق مشترک روی iPhone اجرا می‌شود. تونل واقعی نیازمند Packet Tunnel Extension، entitlement و LibXray.xcframework است و باید روی Mac امضا شود.");

    public bool IsConnected => false;
    public Task EnsureReadyAsync(AppSettings settings, IProgress<double>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<TestResult> TestAsync(ConfigProfile profile, AppSettings settings, CancellationToken cancellationToken = default) =>
        _precheck.TestAsync(profile, settings.FastTestMode ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(5), cancellationToken);
    public Task<TestResult> TestCurrentConnectionAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TestResult(false, null, "Packet Tunnel واقعی iOS هنوز فعال نشده است.", ValidationLevel.None));
    public Task ConnectAsync(ConfigProfile profile, AppSettings settings, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("iOS Packet Tunnel Extension هنوز به Native bridge وصل نشده است. راهنمای native/ios را ببینید.");
    public Task DisconnectAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
#endif
