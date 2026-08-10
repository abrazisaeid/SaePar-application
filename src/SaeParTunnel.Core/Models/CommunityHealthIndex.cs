using System.Text.Json.Serialization;

namespace SaeParTunnel.Core.Models;

public sealed class CommunityHealthIndex
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset? GeneratedAtUtc { get; set; }
    public List<CommunityServerHealth> Profiles { get; set; } = new();
}

public sealed class CommunityServerHealth
{
    public string ProfileId { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public int? MedianLatencyMs { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public double SuccessRate { get; set; }
    public int? Score { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public string Network { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;

    [JsonIgnore]
    public int SampleCount => Math.Max(0, SuccessCount) + Math.Max(0, FailureCount);
}

public sealed class CommunityHealthFetchResult
{
    public bool NotModified { get; set; }
    public CommunityHealthIndex Index { get; set; } = new();
    public string ETag { get; set; } = string.Empty;
    public DateTimeOffset? LastModified { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public bool UsedDirectConnection { get; set; }
}
