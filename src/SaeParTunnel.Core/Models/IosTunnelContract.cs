namespace SaeParTunnel.Core.Models;

public sealed record IosTunnelRequest(ConfigProfile Profile, AppSettings Settings);

public static class IosTunnelContract
{
    public const string ExtensionBundleIdentifier = "com.saepar.tunnel.packet-tunnel";
    public const string ProviderRequestKey = "saepar.tunnel.request";
}
