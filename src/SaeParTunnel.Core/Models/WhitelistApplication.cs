using System.Text.Json.Serialization;

namespace SaeParTunnel.Core.Models;

public sealed class WhitelistApplication
{
    // Kept for migration from the Windows v1.x settings file.
    public string ExecutablePath { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;

    [JsonIgnore]
    public string Identifier => !string.IsNullOrWhiteSpace(PackageName) ? PackageName : ExecutablePath;

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(FriendlyName)) return FriendlyName;
            if (!string.IsNullOrWhiteSpace(PackageName)) return PackageName;
            if (string.IsNullOrWhiteSpace(ExecutablePath)) return "برنامه نامشخص";
            try { return Path.GetFileName(ExecutablePath); }
            catch { return ExecutablePath; }
        }
    }

    [JsonIgnore]
    public string WindowsRoutingPath => (ExecutablePath ?? string.Empty).Replace('\\', '/');
}
