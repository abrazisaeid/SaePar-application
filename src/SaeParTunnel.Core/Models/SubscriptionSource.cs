namespace SaeParTunnel.Core.Models;

public sealed class SubscriptionSource
{
    public const string BuiltInId = "builtin-epodonios";
    public const string BuiltInName = "منبع پیش‌فرض";
    public const string BuiltInUrl =
        "https://raw.githubusercontent.com/Epodonios/v2ray-configs/main/All_Configs_Sub.txt";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ETag { get; set; } = string.Empty;
    public DateTime? LastFetchedUtc { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsBuiltIn { get; set; }

    public static SubscriptionSource CreateBuiltIn() => new()
    {
        Id = BuiltInId,
        Name = BuiltInName,
        Url = BuiltInUrl,
        IsBuiltIn = true,
        IsEnabled = true
    };
}
