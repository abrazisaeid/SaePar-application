using System.Text.Json;
using System.Text.Json.Serialization;
using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Tests;

public sealed class CommunityHealthTests
{
    [Fact]
    public void CommunityScoreImprovesUntestedProfileRanking()
    {
        var profile = new ConfigProfile
        {
            Health = ProfileHealth.Untested,
            Security = "none"
        };
        var baseline = profile.QualityScore;

        profile.CommunityScore = 95;
        profile.CommunityLatencyMs = 220;
        profile.CommunitySuccessCount = 12;
        profile.CommunityFailureCount = 1;

        Assert.True(profile.HasCommunityHealth);
        Assert.True(profile.QualityScore > baseline);
        Assert.Contains("امتیاز جمعی", profile.CommunityHealthText);
    }

    [Fact]
    public void LocalFailureStillLimitsCommunityBoost()
    {
        var profile = new ConfigProfile
        {
            Health = ProfileHealth.Failed,
            FailureCount = 2,
            CommunityScore = 95,
            CommunityLatencyMs = 220,
            CommunitySuccessCount = 12
        };

        Assert.InRange(profile.QualityScore, 0, 20);
    }

    [Fact]
    public void CommunityIndexReadsCamelCaseJson()
    {
        var json = """
        {
          "schemaVersion": 1,
          "generatedAtUtc": "2026-08-10T12:00:00Z",
          "profiles": [
            {
              "profileId": "abc",
              "endpoint": "vpn.example.com:443",
              "protocol": "VLESS",
              "medianLatencyMs": "180",
              "successCount": "9",
              "failureCount": 1,
              "successRate": "0.9",
              "score": "92",
              "lastSeenUtc": "2026-08-10T11:58:00Z",
              "network": "ws",
              "region": "IR"
            }
          ]
        }
        """;
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        var index = JsonSerializer.Deserialize<CommunityHealthIndex>(json, options);

        Assert.NotNull(index);
        var entry = Assert.Single(index!.Profiles);
        Assert.Equal("abc", entry.ProfileId);
        Assert.Equal(180, entry.MedianLatencyMs);
        Assert.Equal(10, entry.SampleCount);
        Assert.Equal(92, entry.Score);
    }
}
