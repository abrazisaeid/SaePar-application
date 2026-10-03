namespace SaeParTunnel.Core.Models;

public sealed class AppSettings
{
    public int DataSchemaVersion { get; set; } = 28;
    public string XrayPath { get; set; } = string.Empty;
    public int SocksPort { get; set; } = 10808;
    public int HttpPort { get; set; } = 10809;
    public int ProbePort { get; set; } = 10810;
    public bool EnableSystemProxy { get; set; } = true;
    public bool AutoTestNewProfiles { get; set; } = false;
    public bool RemoveDuplicates { get; set; } = true;
    public bool AutoCleanupOldServers { get; set; } = true;
    public Dictionary<string, DateTime> SuppressedProfileIds { get; set; } = new();
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
    public bool EnableIranBypass { get; set; } = true;
    public List<string> DirectRoutingEntries { get; set; } = new();
    public List<WhitelistApplication> WhitelistApplications { get; set; } = new();
    public List<string> WhitelistWebsites { get; set; } = new();

    public List<SubscriptionSource> Subscriptions { get; set; } = new();

    // Retained so settings from older releases can be migrated without data loss.
    public string GitHubSubscriptionUrl { get; set; } =
        SubscriptionSource.BuiltInUrl;
    public string GitHubETag { get; set; } = string.Empty;
    public DateTime? LastGitHubFetchUtc { get; set; }

    // Windows system-proxy restore state. Harmless on mobile and retained for migration.
    public bool ProxyWasManaged { get; set; }
    public bool PreviousProxyEnabled { get; set; }
    public string PreviousProxyServer { get; set; } = string.Empty;
    public string PreviousProxyOverride { get; set; } = string.Empty;
}
