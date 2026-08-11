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
