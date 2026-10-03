using System.Text.Json;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class IranRoutingTests
{
    [Theory]
    [InlineData("windows")]
    [InlineData("android")]
    [InlineData("ios")]
    public void DomesticBypassKeepsForeignDefaultAndProbesOnProxy(string platform)
    {
        // Legacy selection must never make unrelated foreign traffic direct
        // when the Iran policy is enabled (including migrated/hand-edited JSON).
        var settings = new AppSettings
        {
            EnableWhitelistRouting = true,
            DirectRoutingEntries = new() { "https://cp.cloudflare.com", "my-company.example", "192.0.2.129/24", "bad regexp:*" }
        };
        using var doc = Build(platform, settings);
        var root = doc.RootElement;
        Assert.Equal("proxy", root.GetProperty("outbounds")[0].GetProperty("tag").GetString());
        Assert.Equal("IPIfNonMatch", root.GetProperty("routing").GetProperty("domainStrategy").GetString());
        Assert.True(root.GetProperty("inbounds")[0].GetProperty("sniffing").GetProperty("enabled").GetBoolean());
        var rules = root.GetProperty("routing").GetProperty("rules").EnumerateArray().ToArray();
        Assert.Equal("connection-probes", Tag(rules[0]));
        Assert.Equal("proxy", rules[0].GetProperty("outboundTag").GetString());
        Assert.Contains("domain:my-company.example", Values(Find(rules, "custom-direct-domains"), "domain"));
        Assert.Contains("192.0.2.0/24", Values(Find(rules, "custom-direct-ips"), "ip"));
        var domestic = Find(rules, "iran-domains");
        Assert.Equal("direct", domestic.GetProperty("outboundTag").GetString());
        Assert.Contains("domain:ir", Values(domestic, "domain"));
        Assert.Contains("domain:digikala.com", Values(domestic, "domain"));
        Assert.Contains("domain:xn--mgba3a4f16a", Values(domestic, "domain"));
        Assert.True(Array.FindIndex(rules, r => Tag(r) == "iran-proxy-exceptions") < Array.FindIndex(rules, r => Tag(r) == "iran-domains"));
        Assert.Contains("domain:animelist.ir", Values(Find(rules, "iran-proxy-exceptions"), "domain"));
        Assert.Contains(Values(Find(rules, "iran-ip-ranges"), "ip"), ip => ip.Contains(':'));
        Assert.Equal("private-networks", Tag(rules[1]));
        Assert.Equal("routing-dns", root.GetProperty("dns").GetProperty("tag").GetString());
        Assert.All(Values(root.GetProperty("dns"), "servers"), server => Assert.StartsWith("https://", server));
        Assert.Equal("proxy", Find(rules, "routing-dns-proxy").GetProperty("outboundTag").GetString());
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("android")]
    [InlineData("ios")]
    public void DisablingIranBypassRemovesItsRules(string platform)
    {
        using var doc = Build(platform, new AppSettings { EnableIranBypass = false, DirectRoutingEntries = new() { "example.com" } });
        Assert.Equal("proxy", doc.RootElement.GetProperty("outbounds")[0].GetProperty("tag").GetString());
        Assert.False(doc.RootElement.TryGetProperty("dns", out _));
        var rules = doc.RootElement.GetProperty("routing").GetProperty("rules").EnumerateArray();
        Assert.DoesNotContain(rules, r => Tag(r).StartsWith("iran-") || Tag(r).StartsWith("custom-direct"));
    }

    [Fact]
    public void PreconnectionTestNeverUsesDomesticOrUserBypass()
    {
        var settings = new AppSettings { DirectRoutingEntries = new() { "cp.cloudflare.com", "0.0.0.0/1" } };
        using var doc = JsonDocument.Parse(new XrayConfigBuilder().Build(Profile(), 19001, 19002, testMode: true, settings: settings));
        Assert.False(doc.RootElement.TryGetProperty("routing", out _));
        Assert.Equal("proxy", doc.RootElement.GetProperty("outbounds")[0].GetProperty("tag").GetString());
    }

    [Fact]
    public void BundledSnapshotIsSharedAndContainsBothIpFamiliesAndKnownBanks()
    {
        var catalog = IranRoutingCatalog.Default;
        Assert.Same(catalog, IranRoutingCatalog.Default);
        Assert.True(catalog.DirectDomains.Length > 50_000);
        Assert.True(catalog.IpRanges.Length > 2000);
        Assert.Contains("domain:ir", catalog.DirectDomains);
        Assert.DoesNotContain("domain:google.com", catalog.DirectDomains);
        Assert.Contains(catalog.IpRanges, ip => ip.Contains(':'));
        Assert.Contains(catalog.IpRanges, ip => ip.Contains('.'));
    }

    [Theory]
    [InlineData("*.Example.IR", "example.ir")]
    [InlineData("https://Example.com/path?q=hello", "example.com")]
    [InlineData("domain:company.example", "company.example")]
    [InlineData("192.0.2.129/24", "192.0.2.0/24")]
    [InlineData("2001:db8:abcd::1/32", "2001:db8::/32")]
    [InlineData("[2001:db8::1]", "2001:db8::1")]
    public void ImportedRoutesAreCanonical(string input, string expected) => Assert.Equal(expected, RoutingListParser.Normalize(input));

    [Theory]
    [InlineData("regexp:.*")]
    [InlineData("0.0.0.0/0")]
    [InlineData("2001:db8::/129")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://password@site.example")]
    [InlineData("com")]
    [InlineData("not a valid domain")]
    public void MalformedOrCatchAllUserRoutesAreRejected(string input) => Assert.Null(RoutingListParser.Normalize(input));

    [Fact]
    public void ListImportDeduplicatesAndReportsInvalidEntriesWithoutDiscardingValidOnes()
    {
        var result = RoutingListParser.Parse("# my list\n*.Example.IR\nhttps://example.ir/path\n192.0.2.7/24 # office\nnot a domain\n");
        Assert.Equal(2, result.Entries.Length);
        Assert.Equal(1, result.InvalidCount);
        Assert.Contains("example.ir", result.Entries);
        Assert.Contains("192.0.2.0/24", result.Entries);
        Assert.Throws<ArgumentException>(() => RoutingListParser.Parse(new string('x', RoutingListParser.MaxTextLength + 1)));
    }

    [Theory]
    [InlineData("windows", true)]
    [InlineData("windows", false)]
    [InlineData("android", true)]
    [InlineData("android", false)]
    [InlineData("ios", true)]
    [InlineData("ios", false)]
    public void ProductionRoutingNeedsNoExternalGeoipOrGeositeFiles(string platform, bool iranBypass)
    {
        using var doc = Build(platform, new AppSettings { EnableIranBypass = iranBypass });
        var json = doc.RootElement.GetRawText();
        Assert.DoesNotContain("geoip:", json);
        Assert.DoesNotContain("geosite:", json);
        Assert.DoesNotContain("ext:", json);
        var rules = doc.RootElement.GetProperty("routing").GetProperty("rules").EnumerateArray().ToArray();
        var privateIps = Values(Find(rules, "private-networks"), "ip");
        Assert.Contains("192.168.0.0/16", privateIps);
        Assert.Contains("127.0.0.0/8", privateIps);
        Assert.Contains("fc00::/7", privateIps);
        foreach (var rule in rules.Where(r => r.TryGetProperty("ip", out _)))
            Assert.All(Values(rule, "ip"), ip => Assert.NotNull(RoutingListParser.Normalize(ip)));
    }

    private static string Tag(JsonElement rule) => rule.GetProperty("ruleTag").GetString()!;
    private static JsonElement Find(JsonElement[] rules, string tag) => rules.Single(r => Tag(r) == tag);
    private static string[] Values(JsonElement rule, string name) => rule.GetProperty(name).EnumerateArray().Select(v => v.GetString()!).ToArray();
    private static ConfigProfile Profile() => new() { Protocol = ProxyProtocol.Vless, Address = "127.0.0.1", Port = 443, UserId = "11111111-1111-1111-1111-111111111111", Encryption = "none", Network = "raw", Security = "none" };
    private static JsonDocument Build(string platform, AppSettings settings)
    {
        var builder = new XrayConfigBuilder();
        return JsonDocument.Parse(platform switch
        {
            "android" => builder.BuildAndroidTun(Profile(), settings),
            "ios" => builder.BuildIosTun(Profile(), settings, 17),
            _ => builder.Build(Profile(), 19001, 19002, settings: settings)
        });
    }
}
