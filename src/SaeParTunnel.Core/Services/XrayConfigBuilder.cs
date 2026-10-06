using System.Globalization;
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
        var iranBypass = !testMode && settings?.EnableIranBypass == true;
        var selectiveRouting = whitelistEnabled && !iranBypass;

        var inbounds = new List<object>
        {
            BuildSocksInbound(socksPort, whitelistEnabled || iranBypass),
            BuildHttpInbound(httpPort, whitelistEnabled || iranBypass, "http-in")
        };

        object[] outbounds = selectiveRouting
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

        if (!testMode)
        {
            root["routing"] = (whitelistEnabled || iranBypass) && settings is not null
                ? BuildNormalRouting(settings, includeProcesses: true, "whitelist-websites")
                : BuildPrivateNetworkRouting();
            if (iranBypass) root["dns"] = BuildRoutingDns();
        }

        return JsonSerializer.Serialize(root, JsonOptions);
    }

    /// <summary>
    /// Builds an Android full-device TUN configuration. Android's VpnService owns
    /// the interface and libXray receives the real established fd through the
    /// root env key xray.tun.fd immediately before core startup. Application whitelisting is enforced by VpnService.Builder;
    /// website whitelisting remains an Xray routing concern.
    /// </summary>
    public string BuildAndroidTun(ConfigProfile profile, AppSettings settings, int mtu = 1500, int metricsPort = 0)
    {
        var json = BuildMobileTun(
            profile,
            settings,
            mtu,
            "saepar0",
            "android-whitelist-websites");
        return metricsPort == 0 ? json : XrayTrafficClient.EnableMetrics(json, metricsPort);
    }

    /// <summary>
    /// Builds an iOS full-device TUN configuration. NetworkExtension owns the
    /// utun interface, so Xray receives that descriptor through its root env.
    /// </summary>
    public string BuildIosTun(
        ConfigProfile profile,
        AppSettings settings,
        int tunFileDescriptor,
        int mtu = 1400)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tunFileDescriptor);

        return BuildMobileTun(
            profile,
            settings,
            mtu,
            "utun",
            "ios-whitelist-websites",
            new Dictionary<string, string>
            {
                ["xray.tun.fd"] = tunFileDescriptor.ToString(CultureInfo.InvariantCulture)
            });
    }

    private static string BuildMobileTun(
        ConfigProfile profile,
        AppSettings settings,
        int mtu,
        string interfaceName,
        string whitelistRuleTag,
        Dictionary<string, string>? environment = null)
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
                name = interfaceName,
                mtu
            },
            ["sniffing"] = BuildSniffing()
        };

        object[] outbounds = websiteWhitelistEnabled && !settings.EnableIranBypass
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
            // Mobile system DNS packets enter the TUN as ordinary traffic. Keep
            // them on the selected routing path; each platform runtime is
            // responsible for keeping Xray's own outbound sockets outside TUN.
            ["routing"] = BuildNormalRouting(settings, includeProcesses: false, whitelistRuleTag)
        };

        if (environment is not null)
            root["env"] = environment;
        if (settings.EnableIranBypass) root["dns"] = BuildRoutingDns();

        return JsonSerializer.Serialize(root, JsonOptions);
    }

    /// <summary>
    /// Real libXray proxy test configuration used on Android before connecting.
    /// It deliberately has only a SOCKS inbound and ignores whitelist settings,
    /// so a successful result validates the selected outbound itself.
    /// </summary>
    public string BuildAndroidProbe(ConfigProfile profile, int socksPort)
    {
        return BuildMobileProbe(profile, socksPort);
    }

    public string BuildIosProbe(ConfigProfile profile, int socksPort)
    {
        return BuildMobileProbe(profile, socksPort);
    }

    private static string BuildMobileProbe(ConfigProfile profile, int socksPort)
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

    private static object BuildNormalRouting(AppSettings settings, bool includeProcesses, string whitelistRuleTag)
    {
        // Probe rules always validate the proxy, including when a user has added
        // a probe host to their direct list. Unknown/foreign traffic stays proxied.
        var rules = new List<object> { BuildProbeRoutingRule(), BuildPrivateNetworkRule() };
        if (settings.EnableIranBypass)
        {
            // IP classification uses encrypted DNS through the proxy, so an ISP
            // block-page IP cannot silently classify a foreign domain as Iranian.
            rules.Add(new { type = "field", inboundTag = new[] { "routing-dns" }, outboundTag = "proxy", ruleTag = "routing-dns-proxy" });
            var catalog = IranRoutingCatalog.Default;
            var custom = settings.DirectRoutingEntries.Select(RoutingListParser.Normalize)
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var customDomains = custom.Where(x => !RoutingListParser.IsIpEntry(x)).Select(x => "domain:" + x).ToArray();
            var customIps = custom.Where(RoutingListParser.IsIpEntry).ToArray();
            if (customDomains.Length > 0) rules.Add(DomainRule(customDomains, "direct", "custom-direct-domains"));
            if (customIps.Length > 0) rules.Add(IpRule(customIps, "custom-direct-ips"));
            if (catalog.ProxyDomains.Length > 0) rules.Add(DomainRule(catalog.ProxyDomains, "proxy", "iran-proxy-exceptions"));
            rules.Add(DomainRule(catalog.DirectDomains, "direct", "iran-domains"));
            rules.Add(IpRule(catalog.IpRanges, "iran-ip-ranges"));
        }
        else if (settings.EnableWhitelistRouting)
        {
            if (includeProcesses)
            {
                var processes = settings.WhitelistApplications.Where(x => x is not null && !string.IsNullOrWhiteSpace(x.ExecutablePath))
                    .Select(x => x.WindowsRoutingPath).Distinct(StringComparer.Ordinal).ToArray();
                if (processes.Length > 0) rules.Add(new { type = "field", process = processes, outboundTag = "proxy", ruleTag = "whitelist-applications" });
            }
            var domains = settings.WhitelistWebsites.Select(NormalizeRoutingDomain).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (domains.Length > 0) rules.Add(DomainRule(domains, "proxy", whitelistRuleTag));
        }
        return new { domainStrategy = settings.EnableIranBypass ? "IPIfNonMatch" : "AsIs", rules = rules.ToArray() };
    }

    private static object DomainRule(string[] domains, string tag, string ruleTag) => new
    { type = "field", domain = domains, outboundTag = tag, ruleTag };

    private static object IpRule(string[] ips, string ruleTag) => new
    { type = "field", ip = ips, outboundTag = "direct", ruleTag };

    private static object BuildRoutingDns() => new
    {
        servers = new[] { "https://1.1.1.1/dns-query", "https://8.8.8.8/dns-query" },
        queryStrategy = "UseIP", disableCache = false, tag = "routing-dns"
    };

    private static object BuildPrivateNetworkRouting() => new
    {
        domainStrategy = "AsIs",
        rules = new[] { BuildPrivateNetworkRule() }
    };

    private static object BuildProbeRoutingRule() => new
    {
        type = "field",
        domain = new[] { "full:cp.cloudflare.com", "full:www.gstatic.com", "full:www.msftconnecttest.com" },
        outboundTag = "proxy",
        ruleTag = "connection-probes"
    };

    private static object BuildPrivateNetworkRule() => new
    {
        type = "field",
        // Equivalent of Loyalsoldier/geoip release/text/private.txt, with no
        // dependency on an external geoip.dat in mobile app-private storage.
        ip = new[]
        {
            "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8",
            "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24",
            "192.88.99.0/24", "192.168.0.0/16", "198.18.0.0/15",
            "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3",
            "::/127", "fc00::/7", "fe80::/10", "ff00::/8"
        },
        outboundTag = "direct",
        ruleTag = "private-networks"
    };

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
