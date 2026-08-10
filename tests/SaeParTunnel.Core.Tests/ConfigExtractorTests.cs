using System.Text;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class ConfigExtractorTests
{
    [Fact]
    public void ExtractFindsSupportedLinksAndTrimsTrailingPunctuation()
    {
        var parser = new ConfigParser();
        var extractor = new ConfigExtractor(parser);
        var credential = ToBase64Url("chacha20-ietf-poly1305:secret");
        var text =
            "first vless://11111111-1111-1111-1111-111111111111@example.com:443?type=tcp#One, " +
            $"second ss://{credential}@ss.example.com:8388#Two.";

        var profiles = extractor.Extract(text, "subscription");

        Assert.Equal(2, profiles.Count);
        Assert.All(profiles, p => Assert.Equal("subscription", p.Source));
        Assert.Equal(ProxyProtocol.Vless, profiles[0].Protocol);
        Assert.Equal(ProxyProtocol.Shadowsocks, profiles[1].Protocol);
        Assert.False(profiles[1].OriginalUri.EndsWith(".", StringComparison.Ordinal));
    }

    private static string ToBase64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
