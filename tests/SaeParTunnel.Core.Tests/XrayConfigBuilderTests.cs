using System.Text.Json;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class XrayConfigBuilderTests
{
    private readonly XrayConfigBuilder _builder = new();

    [Fact]
    public void BuildVlessWebSocketTlsUsesProxyPrimaryAndLocalInbounds()
    {
        var profile = VlessProfile(network: "websocket", security: "tls");
        profile.Host = "front.example.com";
        profile.Path = "/ws";
        profile.Sni = "sni.example.com";
        profile.Fingerprint = "chrome";
        profile.Alpn = "h2,http/1.1";

        using var doc = Build(profile, socksPort: 10808, httpPort: 10809);
        var root = doc.RootElement;

        var inbounds = root.GetProperty("inbounds").EnumerateArray().ToArray();
        Assert.Equal("socks", inbounds[0].GetProperty("protocol").GetString());
        Assert.Equal(10808, inbounds[0].GetProperty("port").GetInt32());
        Assert.Equal("http", inbounds[1].GetProperty("protocol").GetString());
        Assert.Equal(10809, inbounds[1].GetProperty("port").GetInt32());

        var proxy = root.GetProperty("outbounds")[0];
        Assert.Equal("proxy", proxy.GetProperty("tag").GetString());
        Assert.Equal("vless", proxy.GetProperty("protocol").GetString());
        Assert.Equal("example.com", proxy.GetProperty("settings").GetProperty("address").GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", proxy.GetProperty("settings").GetProperty("id").GetString());

        var stream = proxy.GetProperty("streamSettings");
        Assert.Equal("websocket", stream.GetProperty("method").GetString());
        Assert.Equal("tls", stream.GetProperty("security").GetString());
        Assert.Equal("/ws", stream.GetProperty("wsSettings").GetProperty("path").GetString());
        Assert.Equal("front.example.com", stream.GetProperty("wsSettings").GetProperty("headers").GetProperty("Host").GetString());
        Assert.Equal("sni.example.com", stream.GetProperty("tlsSettings").GetProperty("serverName").GetString());
        Assert.Equal(new[] { "h2", "http/1.1" }, Strings(stream.GetProperty("tlsSettings").GetProperty("alpn")));
    }

    [Fact]
    public void BuildWithWhitelistPlacesDirectFirstAndRoutesSelectedDomainsAndProcesses()
    {
        var settings = new AppSettings
        {
            EnableWhitelistRouting = true,
            WhitelistWebsites = new List<string> { "example.com", "domain:already.test" },
            WhitelistApplications = new List<WhitelistApplication>
            {
                new() { ExecutablePath = "C:\\Apps\\Telegram\\telegram.exe", Platform = "Windows" }
            }
        };

        using var doc = Build(VlessProfile(), settings: settings);
        var root = doc.RootElement;

        Assert.Equal("direct", root.GetProperty("outbounds")[0].GetProperty("tag").GetString());
        Assert.Equal("proxy", root.GetProperty("outbounds")[1].GetProperty("tag").GetString());

        var rules = root.GetProperty("routing").GetProperty("rules").EnumerateArray().ToArray();
        var processRule = rules.Single(x => x.GetProperty("ruleTag").GetString() == "whitelist-applications");
        var domainRule = rules.Single(x => x.GetProperty("ruleTag").GetString() == "whitelist-websites");

        Assert.Equal("proxy", processRule.GetProperty("outboundTag").GetString());
        Assert.Contains("C:/Apps/Telegram/telegram.exe", Strings(processRule.GetProperty("process")));
        Assert.Equal("proxy", domainRule.GetProperty("outboundTag").GetString());
        Assert.Contains("domain:example.com", Strings(domainRule.GetProperty("domain")));
        Assert.Contains("domain:already.test", Strings(domainRule.GetProperty("domain")));
    }

    [Fact]
    public void BuildInTestModeIgnoresWhitelistRouting()
    {
        var settings = new AppSettings
        {
            EnableWhitelistRouting = true,
            WhitelistWebsites = new List<string> { "example.com" }
        };

        using var doc = Build(VlessProfile(), testMode: true, settings: settings);
        var root = doc.RootElement;

        Assert.Equal("warning", root.GetProperty("log").GetProperty("loglevel").GetString());
        Assert.Equal("proxy", root.GetProperty("outbounds")[0].GetProperty("tag").GetString());
        Assert.False(root.TryGetProperty("routing", out _));
    }

    [Fact]
    public void BuildAndroidTunWithWebsiteWhitelistCreatesTunInboundAndDomainRule()
    {
        var settings = new AppSettings
        {
            EnableWhitelistRouting = true,
            WhitelistWebsites = new List<string> { ".example.com" }
        };

        using var doc = JsonDocument.Parse(_builder.BuildAndroidTun(VlessProfile(), settings, mtu: 1400));
        var root = doc.RootElement;

        var inbound = root.GetProperty("inbounds")[0];
        Assert.Equal("tun", inbound.GetProperty("protocol").GetString());
        Assert.Equal("saepar0", inbound.GetProperty("settings").GetProperty("name").GetString());
        Assert.Equal(1400, inbound.GetProperty("settings").GetProperty("mtu").GetInt32());
        Assert.Equal("direct", root.GetProperty("outbounds")[0].GetProperty("tag").GetString());

        var rule = root.GetProperty("routing").GetProperty("rules")[0];
        Assert.Equal("android-whitelist-websites", rule.GetProperty("ruleTag").GetString());
        Assert.Contains("domain:example.com", Strings(rule.GetProperty("domain")));
    }

    [Fact]
    public void BuildAndroidProbeCreatesOnlySocksInboundAndNoRouting()
    {
        using var doc = JsonDocument.Parse(_builder.BuildAndroidProbe(VlessProfile(), socksPort: 28080));
        var root = doc.RootElement;

        var inbounds = root.GetProperty("inbounds").EnumerateArray().ToArray();
        Assert.Single(inbounds);
        Assert.Equal("socks", inbounds[0].GetProperty("protocol").GetString());
        Assert.Equal(28080, inbounds[0].GetProperty("port").GetInt32());
        Assert.Equal("proxy", root.GetProperty("outbounds")[0].GetProperty("tag").GetString());
        Assert.False(root.TryGetProperty("routing", out _));
    }

    [Fact]
    public void BuildThrowsForUnsupportedProfile()
    {
        var profile = VlessProfile();
        profile.Health = ProfileHealth.Unsupported;
        profile.TestMessage = "unsupported";

        Assert.Throws<NotSupportedException>(() => _builder.Build(profile, 10808, 10809));
        Assert.Throws<NotSupportedException>(() => _builder.BuildAndroidTun(profile, new AppSettings()));
        Assert.Throws<NotSupportedException>(() => _builder.BuildAndroidProbe(profile, 10808));
    }

    private JsonDocument Build(
        ConfigProfile profile,
        int socksPort = 10808,
        int httpPort = 10809,
        bool testMode = false,
        AppSettings? settings = null) =>
        JsonDocument.Parse(_builder.Build(profile, socksPort, httpPort, testMode, settings));

    private static ConfigProfile VlessProfile(string network = "raw", string security = "none") => new()
    {
        Protocol = ProxyProtocol.Vless,
        Address = "example.com",
        Port = 443,
        UserId = "11111111-1111-1111-1111-111111111111",
        Encryption = "none",
        Network = network,
        Security = security
    };

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(x => x.GetString()!).ToArray();
}
