using System.Text.Json;
using System.Text.Json.Serialization;
using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

public sealed class XrayConfigBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Build(
        ConfigProfile profile,
        int socksPort,
        int httpPort,
        bool testMode = false,
        AppSettings? settings = null)
    {
        if (profile.Health == ProfileHealth.Unsupported)
            throw new NotSupportedException(profile.TestMessage);

        // Test mode must always test the selected proxy itself and therefore ignores
        // split-tunneling/whitelist rules. In normal mode, whitelist routing places
        // the direct outbound first so every unmatched request goes directly.
        var whitelistEnabled = !testMode && settings?.EnableWhitelistRouting == true;

        var inbounds = new List<object>
        {
            BuildSocksInbound(socksPort, whitelistEnabled),
            BuildHttpInbound(httpPort, whitelistEnabled, "http-in")
        };

        object[] outbounds = whitelistEnabled
            ? new object[]
            {
                new { tag = "direct", protocol = "freedom", settings = new { } },
                BuildProxyOutbound(profile),
                new { tag = "block", protocol = "blackhole", settings = new { } }
            }
            : new object[]
            {
                BuildProxyOutbound(profile),
                new { tag = "direct", protocol = "freedom", settings = new { } },
                new { tag = "block", protocol = "blackhole", settings = new { } }
            };

        var root = new Dictionary<string, object?>
        {
            ["log"] = new { loglevel = testMode ? "warning" : "info" },
            ["inbounds"] = inbounds.ToArray(),
            ["outbounds"] = outbounds
        };

        if (whitelistEnabled && settings is not null)
            root["routing"] = BuildWhitelistRouting(settings);

        return JsonSerializer.Serialize(root, JsonOptions);
    }

    /// <summary>
    /// Builds an Android full-device TUN configuration. Android's VpnService owns
    /// the interface and libXray receives the real established fd through the
    /// root env key xray.tun.fd immediately before core startup. Application whitelisting is enforced by VpnService.Builder;
    /// website whitelisting remains an Xray routing concern.
    /// </summary>
    public string BuildAndroidTun(ConfigProfile profile, AppSettings settings, int mtu = 1500)
    {
        if (profile.Health == ProfileHealth.Unsupported)
            throw new NotSupportedException(profile.TestMessage);

        var websiteWhitelistEnabled = settings.EnableWhitelistRouting &&
            settings.WhitelistWebsites.Any(x => !string.IsNullOrWhiteSpace(x));

        var tunInbound = new Dictionary<string, object?>
        {
            ["tag"] = "tun-in",
            ["port"] = 0,
            ["protocol"] = "tun",
            ["settings"] = new
            {
                name = "saepar0",
                mtu
            },
            ["sniffing"] = BuildSniffing()
        };

        object[] outbounds = websiteWhitelistEnabled
            ? new object[]
            {
                new { tag = "direct", protocol = "freedom", settings = new { } },
                BuildProxyOutbound(profile),
                new { tag = "block", protocol = "blackhole", settings = new { } }
            }
            : new object[]
            {
                BuildProxyOutbound(profile),
                new { tag = "direct", protocol = "freedom", settings = new { } },
                new { tag = "block", protocol = "blackhole", settings = new { } }
            };

        var root = new Dictionary<string, object?>
        {
            ["log"] = new { loglevel = "info" },
            ["inbounds"] = new object[] { tunInbound },
            ["outbounds"] = outbounds,
            // Android system DNS packets enter the TUN as ordinary UDP/TCP :53
            // traffic. Route them direct so DNS does not depend on UDP support of
            // the selected proxy. The DialerController protects these direct
            // sockets from re-entering VpnService, avoiding a routing loop.
            ["routing"] = BuildAndroidRouting(settings, websiteWhitelistEnabled)
        };

        return JsonSerializer.Serialize(root, JsonOptions);
    }

    /// <summary>
    /// Real libXray proxy test configuration used on Android before connecting.
    /// It deliberately has only a SOCKS inbound and ignores whitelist settings,
    /// so a successful result validates the selected outbound itself.
    /// </summary>
    public string BuildAndroidProbe(ConfigProfile profile, int socksPort)
    {
        if (profile.Health == ProfileHealth.Unsupported)
            throw new NotSupportedException(profile.TestMessage);

        var root = new Dictionary<string, object?>
        {
            ["log"] = new { loglevel = "warning" },
            ["inbounds"] = new object[] { BuildSocksInbound(socksPort, false) },
            ["outbounds"] = new object[]
            {
                BuildProxyOutbound(profile),
                new { tag = "direct", protocol = "freedom", settings = new { } },
                new { tag = "block", protocol = "blackhole", settings = new { } }
            }
        };

        return JsonSerializer.Serialize(root, JsonOptions);
    }

    private static object BuildAndroidRouting(AppSettings settings, bool websiteWhitelistEnabled)
    {
        // Do not force Android DNS direct here. Android sends its configured DNS
        // traffic into the VPN like any other packet; keeping it on the proxy path
        // matches the known-working OneXray architecture and avoids ISP-side DNS
        // filtering/hijacking. libXray's *internal* resolver is separately protected
        // by VpnService.protect() in the Java bridge.
        var rules = new List<object>();

        if (websiteWhitelistEnabled)
        {
            var domains = settings.WhitelistWebsites
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeRoutingDomain)
                .Where(x => x is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (domains.Length > 0)
            {
                rules.Add(new
                {
                    domain = domains,
                    outboundTag = "proxy",
                    ruleTag = "android-whitelist-websites"
                });
            }
        }

        return new
        {
            domainStrategy = "AsIs",
            rules = rules.ToArray()
        };
    }

    private static object BuildSocksInbound(int port, bool enableSniffing)
    {
        var inbound = new Dictionary<string, object?>
        {
            ["tag"] = "socks-in",
            ["listen"] = "127.0.0.1",
            ["port"] = port,
            ["protocol"] = "socks",
            ["settings"] = new { auth = "noauth", udp = true }
        };

        if (enableSniffing)
            inbound["sniffing"] = BuildSniffing();

        return inbound;
    }

    private static object BuildHttpInbound(int port, bool enableSniffing, string tag)
    {
        var inbound = new Dictionary<string, object?>
        {
            ["tag"] = tag,
            ["listen"] = "127.0.0.1",
            ["port"] = port,
            ["protocol"] = "http",
            ["settings"] = new { }
        };

        if (enableSniffing)
            inbound["sniffing"] = BuildSniffing();

        return inbound;
    }

    private static object BuildSniffing() => new
    {
        enabled = true,
        destOverride = new[] { "http", "tls", "quic" },
        metadataOnly = false,
        routeOnly = true
    };

    private static object BuildWhitelistRouting(AppSettings settings)
    {
        var rules = new List<object>();

        var processes = settings.WhitelistApplications
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.ExecutablePath))
            .Select(x => x.WindowsRoutingPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (processes.Length > 0)
        {
            rules.Add(new
            {
                process = processes,
                outboundTag = "proxy",
                ruleTag = "whitelist-applications"
            });
        }

        var domains = settings.WhitelistWebsites
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeRoutingDomain)
            .Where(x => x is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (domains.Length > 0)
        {
            rules.Add(new
            {
                domain = domains,
                outboundTag = "proxy",
                ruleTag = "whitelist-websites"
            });
        }

        return new
        {
            domainStrategy = "AsIs",
            rules
        };
    }

    private static string? NormalizeRoutingDomain(string value)
    {
        var domain = value.Trim();
        if (domain.Length == 0) return null;

        // User-facing whitelist entries are stored as plain host names. Xray's
        // domain: matcher includes the domain itself and all of its subdomains.
        if (domain.StartsWith("domain:", StringComparison.OrdinalIgnoreCase) ||
            domain.StartsWith("full:", StringComparison.OrdinalIgnoreCase) ||
            domain.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase) ||
            domain.StartsWith("keyword:", StringComparison.OrdinalIgnoreCase))
            return domain;

        return "domain:" + domain.TrimStart('.');
    }

    private static Dictionary<string, object?> BuildProxyOutbound(ConfigProfile profile)
    {
        var outbound = new Dictionary<string, object?>
        {
            ["tag"] = "proxy",
            ["protocol"] = profile.Protocol switch
            {
                ProxyProtocol.Vless => "vless",
                ProxyProtocol.Vmess => "vmess",
                ProxyProtocol.Trojan => "trojan",
                ProxyProtocol.Shadowsocks => "shadowsocks",
                _ => throw new NotSupportedException("پروتکل پشتیبانی نمی‌شود.")
            },
            ["settings"] = BuildProtocolSettings(profile)
        };

        if (profile.Protocol != ProxyProtocol.Shadowsocks ||
            profile.Network != "raw" ||
            profile.Security != "none")
        {
            outbound["streamSettings"] = BuildStreamSettings(profile);
        }

        return outbound;
    }

    private static object BuildProtocolSettings(ConfigProfile profile) => profile.Protocol switch
    {
        ProxyProtocol.Vless => new
        {
            address = profile.Address,
            port = profile.Port,
            id = profile.UserId,
            encryption = string.IsNullOrWhiteSpace(profile.Encryption) ? "none" : profile.Encryption,
            flow = NullIfEmpty(profile.Flow),
            level = 0
        },
        ProxyProtocol.Vmess => new
        {
            address = profile.Address,
            port = profile.Port,
            id = profile.UserId,
            security = string.IsNullOrWhiteSpace(profile.Encryption) ? "auto" : profile.Encryption,
            level = 0
        },
        ProxyProtocol.Trojan => new
        {
            address = profile.Address,
            port = profile.Port,
            password = profile.Password,
            level = 0
        },
        ProxyProtocol.Shadowsocks => new
        {
            address = profile.Address,
            port = profile.Port,
            method = profile.Encryption,
            password = profile.Password,
            level = 0
        },
        _ => throw new NotSupportedException("پروتکل پشتیبانی نمی‌شود.")
    };

    private static Dictionary<string, object?> BuildStreamSettings(ConfigProfile profile)
    {
        var method = string.IsNullOrWhiteSpace(profile.Network) ? "raw" : profile.Network.ToLowerInvariant();
        var security = string.IsNullOrWhiteSpace(profile.Security) ? "none" : profile.Security.ToLowerInvariant();

        var stream = new Dictionary<string, object?>
        {
            ["method"] = method,
            ["security"] = security
        };

        switch (method)
        {
            case "raw":
                stream["rawSettings"] = new { header = BuildRawHeader(profile) };
                break;
            case "websocket":
                stream["wsSettings"] = new
                {
                    path = string.IsNullOrWhiteSpace(profile.Path) ? "/" : profile.Path,
                    host = NullIfEmpty(profile.Host),
                    headers = string.IsNullOrWhiteSpace(profile.Host)
                        ? null
                        : new Dictionary<string, string> { ["Host"] = profile.Host }
                };
                break;
            case "grpc":
                stream["grpcSettings"] = new
                {
                    serviceName = profile.ServiceName,
                    authority = NullIfEmpty(profile.Authority),
                    multiMode = string.Equals(profile.Mode, "multi", StringComparison.OrdinalIgnoreCase)
                };
                break;
            case "xhttp":
                stream["xhttpSettings"] = new
                {
                    host = NullIfEmpty(profile.Host),
                    path = string.IsNullOrWhiteSpace(profile.Path) ? "/" : profile.Path,
                    mode = NullIfEmpty(profile.Mode)
                };
                break;
            case "httpupgrade":
                stream["httpupgradeSettings"] = new
                {
                    host = NullIfEmpty(profile.Host),
                    path = string.IsNullOrWhiteSpace(profile.Path) ? "/" : profile.Path
                };
                break;
            case "mkcp":
                if (!string.IsNullOrWhiteSpace(profile.Path) ||
                    !string.Equals(profile.HeaderType, "none", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException("mKCP قدیمی با seed/header به FinalMask نیاز دارد.");
                }

                stream["kcpSettings"] = new
                {
                    mtu = 1350,
                    tti = 50,
                    uplinkCapacity = 5,
                    downlinkCapacity = 20,
                    congestion = false,
                    readBufferSize = 1,
                    writeBufferSize = 1
                };
                break;
            default:
                throw new NotSupportedException($"Transport «{profile.Network}» در این نسخه پشتیبانی نمی‌شود.");
        }

        if (security == "tls")
        {
            stream["tlsSettings"] = new
            {
                // Share links often omit SNI when the HTTP transport host already
                // carries the certificate name. Xray otherwise falls back to the
                // server address, which is commonly a CDN IP and fails validation.
                serverName = FirstNonEmpty(profile.Sni, profile.Host, profile.Authority),
                allowInsecure = profile.AllowInsecure,
                fingerprint = NullIfEmpty(profile.Fingerprint),
                alpn = ParseAlpn(profile.Alpn)
            };
        }
        else if (security == "reality")
        {
            if (method is not ("raw" or "xhttp" or "grpc"))
                throw new NotSupportedException("REALITY فقط با RAW، XHTTP یا gRPC قابل استفاده است.");

            stream["realitySettings"] = new
            {
                serverName = NullIfEmpty(profile.Sni),
                fingerprint = string.IsNullOrWhiteSpace(profile.Fingerprint) ? "chrome" : profile.Fingerprint,
                password = profile.PublicKey,
                shortId = profile.ShortId,
                spiderX = string.IsNullOrWhiteSpace(profile.SpiderX) ? "/" : profile.SpiderX
            };
        }

        return stream;
    }

    private static object BuildRawHeader(ConfigProfile profile)
    {
        if (!string.Equals(profile.HeaderType, "http", StringComparison.OrdinalIgnoreCase))
            return new { type = "none" };

        return new
        {
            type = "http",
            request = new
            {
                version = "1.1",
                method = "GET",
                path = string.IsNullOrWhiteSpace(profile.Path) ? new[] { "/" } : new[] { profile.Path },
                headers = string.IsNullOrWhiteSpace(profile.Host)
                    ? null
                    : new Dictionary<string, string[]> { ["Host"] = new[] { profile.Host } }
            }
        };
    }

    private static string[]? ParseAlpn(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
