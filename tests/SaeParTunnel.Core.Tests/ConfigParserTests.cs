using System.Text;
using System.Text.Json;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class ConfigParserTests
{
    private readonly ConfigParser _parser = new();

    [Fact]
    public void ParseVlessRealityGrpcLinkMapsShareFields()
    {
        var link = "vless://11111111-1111-1111-1111-111111111111@example.com:443" +
                   "?type=grpc&security=reality&sni=cdn.example.com&fp=chrome" +
                   "&pbk=publicKey123&sid=abcd&serviceName=svc&authority=auth.example.com" +
                   "&mode=multi&encryption=none#Reality%20Grpc";

        var profile = ParseOk(link);

        Assert.Equal(ProxyProtocol.Vless, profile.Protocol);
        Assert.Equal("example.com", profile.Address);
        Assert.Equal(443, profile.Port);
        Assert.Equal("11111111-1111-1111-1111-111111111111", profile.UserId);
        Assert.Equal("grpc", profile.Network);
        Assert.Equal("reality", profile.Security);
        Assert.Equal("cdn.example.com", profile.Sni);
        Assert.Equal("publicKey123", profile.PublicKey);
        Assert.Equal("abcd", profile.ShortId);
        Assert.Equal("svc", profile.ServiceName);
        Assert.Equal("auth.example.com", profile.Authority);
        Assert.Equal("auth.example.com", profile.Host);
        Assert.Equal("multi", profile.Mode);
        Assert.Equal("Reality Grpc", profile.Remark);
        Assert.Equal(ProfileHealth.Untested, profile.Health);
        Assert.False(string.IsNullOrWhiteSpace(profile.Id));
    }

    [Fact]
    public void ParseTrojanWithoutSecurityDefaultsToTls()
    {
        var profile = ParseOk("trojan://secret@example.net:443?type=ws&host=front.example.net&path=%2Fws#Trojan");

        Assert.Equal(ProxyProtocol.Trojan, profile.Protocol);
        Assert.Equal("secret", profile.Password);
        Assert.Equal("websocket", profile.Network);
        Assert.Equal("tls", profile.Security);
        Assert.Equal("front.example.net", profile.Host);
        Assert.Equal("/ws", profile.Path);
        Assert.Equal("Trojan", profile.Remark);
    }

    [Fact]
    public void ParseUriProfileRecognizesSkipCertVerifyAlias()
    {
        var profile = ParseOk(
            "vless://11111111-1111-1111-1111-111111111111@example.com:443" +
            "?type=ws&security=tls&host=edge.example.com&skip-cert-verify=true");

        Assert.True(profile.AllowInsecure);
    }

    [Fact]
    public void ParseVmessBase64PayloadMapsTransportAndTls()
    {
        var payload = JsonSerializer.Serialize(new
        {
            v = "2",
            ps = "VMess WS",
            add = "vmess.example.com",
            port = "443",
            id = "22222222-2222-2222-2222-222222222222",
            aid = "0",
            scy = "auto",
            net = "ws",
            tls = "tls",
            sni = "sni.example.com",
            host = "host.example.com",
            path = "/vmess",
            type = "none",
            fp = "chrome",
            alpn = "h2,http/1.1"
        });
        var profile = ParseOk("vmess://" + ToBase64Url(payload));

        Assert.Equal(ProxyProtocol.Vmess, profile.Protocol);
        Assert.Equal("vmess.example.com", profile.Address);
        Assert.Equal(443, profile.Port);
        Assert.Equal("22222222-2222-2222-2222-222222222222", profile.UserId);
        Assert.Equal(0, profile.AlterId);
        Assert.Equal("auto", profile.Encryption);
        Assert.Equal("websocket", profile.Network);
        Assert.Equal("tls", profile.Security);
        Assert.Equal("sni.example.com", profile.Sni);
        Assert.Equal("host.example.com", profile.Host);
        Assert.Equal("/vmess", profile.Path);
        Assert.Equal("chrome", profile.Fingerprint);
        Assert.Equal("h2,http/1.1", profile.Alpn);
        Assert.Equal("VMess WS", profile.Remark);
    }

    [Fact]
    public void ParseVmessRecognizesSkipCertVerifyAlias()
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["add"] = "vmess.example.com",
            ["port"] = "443",
            ["id"] = "22222222-2222-2222-2222-222222222222",
            ["aid"] = "0",
            ["net"] = "ws",
            ["tls"] = "tls",
            ["skip-cert-verify"] = true
        });

        var profile = ParseOk("vmess://" + ToBase64Url(payload));

        Assert.True(profile.AllowInsecure);
    }

    [Fact]
    public void ParseShadowsocksUserInfoLinkDecodesCredentials()
    {
        var credential = ToBase64Url("aes-256-gcm:s3cr3t");
        var profile = ParseOk($"ss://{credential}@ss.example.com:8388#SS");

        Assert.Equal(ProxyProtocol.Shadowsocks, profile.Protocol);
        Assert.Equal("ss.example.com", profile.Address);
        Assert.Equal(8388, profile.Port);
        Assert.Equal("aes-256-gcm", profile.Encryption);
        Assert.Equal("s3cr3t", profile.Password);
        Assert.Equal("raw", profile.Network);
        Assert.Equal("none", profile.Security);
        Assert.Equal("SS", profile.Remark);
    }

    [Fact]
    public void ParseVmessWithAlterIdMarksUnsupported()
    {
        var payload = JsonSerializer.Serialize(new
        {
            add = "legacy.example.com",
            port = "443",
            id = "33333333-3333-3333-3333-333333333333",
            aid = "64"
        });
        var profile = ParseOk("vmess://" + ToBase64Url(payload));

        Assert.Equal(ProfileHealth.Unsupported, profile.Health);
        Assert.False(string.IsNullOrWhiteSpace(profile.TestMessage));
    }

    [Fact]
    public void ParseUnknownProtocolReturnsNullAndError()
    {
        var profile = _parser.Parse("https://example.com", "test", out var error);

        Assert.Null(profile);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ParseXhttpEchPreservesEncodedDnsSettingAndTransportOptions()
    {
        var profile = ParseOk("vless://11111111-1111-1111-1111-111111111111@127.0.0.1:443?encryption=none&security=tls&sni=edge.example.test&fp=unsafe&alpn=h2&ech=public.example%2Budp%3A%2F%2F127.0.0.1&type=xhttp&host=edge.example.test&path=%2Fxh-test&mode=packet-up#ECH");
        Assert.Equal("public.example+udp://127.0.0.1", profile.EchConfigList);
        Assert.Equal("xhttp", profile.Network);
        Assert.Equal("packet-up", profile.Mode);
        Assert.Equal("unsafe", profile.Fingerprint);
        Assert.Equal("h2", profile.Alpn);
        Assert.Equal("/xh-test", profile.Path);
        Assert.Equal(ProfileHealth.Untested, profile.Health);
    }

    [Fact]
    public void EchIsPartOfIdentityWhileEmptyEchKeepsLegacyId()
    {
        var profile = ParseOk("vless://11111111-1111-1111-1111-111111111111@example.com:443?security=tls");
        var previousId = profile.Id;
        Assert.Equal(previousId, ConfigParser.ComputeIdWithoutEch(profile));
        profile.EchConfigList = "public.example+udp://127.0.0.1";
        var echId = ConfigParser.ComputeId(profile);
        Assert.NotEqual(previousId, echId);
        Assert.Equal(previousId, ConfigParser.ComputeIdWithoutEch(profile));
        profile.EchConfigList = "public.example+https://127.0.0.1/dns-query";
        Assert.NotEqual(echId, ConfigParser.ComputeId(profile));
    }

    [Fact]
    public void NonEchIdentityMatchesThePreviousPublishedContract()
    {
        var profile = new ConfigProfile
        {
            Protocol = ProxyProtocol.Vless, Address = "example.com", Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111"
        };
        Assert.Equal("ab69fc6adc7a247a933bc4413617127782b22a07ad2d93fadd2ce82c8c46f01e", ConfigParser.ComputeId(profile));
    }

    [Fact]
    public void VmessJsonPreservesEch()
    {
        var payload = JsonSerializer.Serialize(new
        {
            add = "127.0.0.1", port = 443, id = "11111111-1111-1111-1111-111111111111",
            net = "xhttp", tls = "tls", ech = "public.example+udp://127.0.0.1"
        });
        Assert.Equal("public.example+udp://127.0.0.1", ParseOk("vmess://" + ToBase64Url(payload)).EchConfigList);
    }

    [Fact]
    public void CachedEchRepairResetsAnOldFalseFailureWithoutOverwritingExplicitSettings()
    {
        var profile = ParseOk("vless://11111111-1111-1111-1111-111111111111@127.0.0.1:443?security=tls&type=xhttp&ech=public.example%2Budp%3A%2F%2F127.0.0.1");
        profile.EchConfigList = "";
        profile.Health = ProfileHealth.Failed;
        profile.FailureCount = 3;
        ConfigParser.ValidateCachedProfile(profile);
        Assert.Equal("public.example+udp://127.0.0.1", profile.EchConfigList);
        Assert.Equal(ProfileHealth.Untested, profile.Health);
        Assert.Equal(0, profile.FailureCount);
        profile.EchConfigList = "explicit-setting";
        ConfigParser.ValidateCachedProfile(profile);
        Assert.Equal("explicit-setting", profile.EchConfigList);
    }

    private ConfigProfile ParseOk(string link)
    {
        var profile = _parser.Parse(link, "test-source", out var error);

        Assert.True(profile is not null, error);
        Assert.Equal("test-source", profile!.Source);
        Assert.Equal(link, profile.OriginalUri);
        return profile;
    }

    private static string ToBase64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
