namespace SaeParTunnel.Core.Models;

public sealed class AppSettings
{
    public int DataSchemaVersion { get; set; } = 25;
    public string XrayPath { get; set; } = string.Empty;
    public int SocksPort { get; set; } = 10808;
    public int HttpPort { get; set; } = 10809;
    public int ProbePort { get; set; } = 10810;
    public bool EnableSystemProxy { get; set; } = true;
    public bool AutoTestNewProfiles { get; set; } = false;
    public bool RemoveDuplicates { get; set; } = true;
    public int TestConcurrency { get; set; } = 0;
    public bool FastTestMode { get; set; } = true;
    public bool QuickMode { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public int AutoReconnectAttempts { get; set; } = 3;
    public bool EnableCommunityHealth { get; set; }
    public string CommunityHealthIndexUrl { get; set; } = string.Empty;
    public string CommunityHealthETag { get; set; } = string.Empty;
    public DateTime? LastCommunityHealthFetchUtc { get; set; }

    public bool EnableWhitelistRouting { get; set; }
    public List<WhitelistApplication> WhitelistApplications { get; set; } = new();
    public List<string> WhitelistWebsites { get; set; } = new();

    public string GitHubSubscriptionUrl { get; set; } =
        "https://raw.githubusercontent.com/Epodonios/v2ray-configs/main/All_Configs_Sub.txt";
    public string GitHubETag { get; set; } = string.Empty;
    public DateTime? LastGitHubFetchUtc { get; set; }

    // Windows system-proxy restore state. Harmless on mobile and retained for migration.
    public bool ProxyWasManaged { get; set; }
    public bool PreviousProxyEnabled { get; set; }
    public string PreviousProxyServer { get; set; } = string.Empty;
    public string PreviousProxyOverride { get; set; } = string.Empty;
}
