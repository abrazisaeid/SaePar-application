using System.Security.Cryptography;
using System.Net;
using System.Text;
using System.Text.Json;
using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

public sealed class ConfigParser
{
    // Keep aligned with Xray v26.7.28 common/geodata/consts.go.
    private static readonly IPNetwork[] PrivateNetworks = new[]
    {
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16",
        "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24", "192.168.0.0/16",
        "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3",
        "::/127", "fc00::/7", "fe80::/10", "ff00::/8"
    }.Select(IPNetwork.Parse).ToArray();

    private static bool IsPrivateEndpoint(string address)
    {
        if (IPAddress.TryParse(address, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            return PrivateNetworks.Any(network => network.Contains(ip));
        }
        var domain = address.TrimEnd('.').ToLowerInvariant();
        return !domain.Contains('.') || new[] { "lan", "localdomain", "example", "invalid", "localhost", "test", "local", "home.arpa", "internal" }
            .Any(suffix => domain == suffix || domain.EndsWith("." + suffix, StringComparison.Ordinal));
    }
    public ConfigProfile? Parse(string raw, string source, out string error)
    {
        error = string.Empty;
        raw = raw.Trim();

        try
        {
            ConfigProfile? profile = raw.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
                ? ParseUriProfile(raw, ProxyProtocol.Vless)
                : raw.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)
                    ? ParseUriProfile(raw, ProxyProtocol.Trojan)
                    : raw.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
                        ? ParseVmess(raw)
                        : raw.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)
                            ? ParseShadowsocks(raw)
                            : null;

            if (profile is null)
            {
                error = "پروتکل پشتیبانی نمی‌شود.";
                return null;
            }

            profile.OriginalUri = raw;
            profile.Source = source;

            if (string.IsNullOrWhiteSpace(profile.Address) || profile.Port is < 1 or > 65535)
            {
                error = "آدرس یا پورت معتبر نیست.";
                return null;
            }

            if (profile.Protocol is ProxyProtocol.Vless or ProxyProtocol.Vmess &&
                string.IsNullOrWhiteSpace(profile.UserId))
            {
                error = "شناسه کاربر موجود نیست.";
                return null;
            }

            if (profile.Protocol is ProxyProtocol.Trojan or ProxyProtocol.Shadowsocks &&
                string.IsNullOrWhiteSpace(profile.Password))
            {
                error = "رمز عبور موجود نیست.";
                return null;
            }

            ApplyCurrentXrayCompatibility(profile);
            profile.Id = ComputeId(profile);
            return profile;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static ConfigProfile ParseUriProfile(string raw, ProxyProtocol protocol)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            throw new FormatException("لینک قابل خواندن نیست.");

        var query = ParseQuery(uri.Query);
        var userInfo = Uri.UnescapeDataString(uri.UserInfo);
        var security = Get(query, "security");

        // Trojan share links commonly omit ?security=tls because TLS is implicit
        // in the trojan:// scheme used by popular clients.
        if (protocol == ProxyProtocol.Trojan && string.IsNullOrWhiteSpace(security))
            security = "tls";

        var profile = new ConfigProfile
        {
            Protocol = protocol,
            Address = uri.Host.Trim('[', ']'),
            Port = uri.Port,
            UserId = protocol == ProxyProtocol.Vless ? userInfo : string.Empty,
            Password = protocol == ProxyProtocol.Trojan ? userInfo : string.Empty,
            Encryption = Get(query, "encryption", "none"),
            Flow = Get(query, "flow"),
            Network = NormalizeNetwork(Get(query, "type", "raw")),
            Security = string.IsNullOrWhiteSpace(security) ? "none" : security.Trim().ToLowerInvariant(),
            Sni = Get(query, "sni"),
            Host = Get(query, "host"),
            Path = Get(query, "path"),
            Fingerprint = Get(query, "fp"),
            PublicKey = Get(query, "pbk"),
            ShortId = Get(query, "sid"),
            SpiderX = Get(query, "spx"),
            ServiceName = Get(query, "serviceName"),
            Authority = Get(query, "authority"),
            HeaderType = Get(query, "headerType", "none"),
            Mode = Get(query, "mode"),
            Alpn = Get(query, "alpn"),
            AllowInsecure = IsTrue(Get(query, "allowInsecure")) ||
                IsTrue(Get(query, "insecure")) ||
                IsTrue(Get(query, "skip-cert-verify")),
            Remark = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'))
        };

        if (string.IsNullOrWhiteSpace(profile.Host) && profile.Network == "grpc")
            profile.Host = profile.Authority;

        return profile;
    }

    private static ConfigProfile ParseVmess(string raw)
    {
        var payload = raw["vmess://".Length..];
        var json = Base64Url.DecodeToString(payload);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string S(string name, string fallback = "") =>
            root.TryGetProperty(name, out var p) ? p.ToString() : fallback;

        int I(string name, int fallback = 0) =>
            int.TryParse(S(name), out var value) ? value : fallback;

        return new ConfigProfile
        {
            Protocol = ProxyProtocol.Vmess,
            Address = S("add"),
            Port = I("port"),
            UserId = S("id"),
            AlterId = I("aid"),
            Encryption = S("scy", "auto"),
            Network = NormalizeNetwork(S("net", "raw")),
            Security = string.IsNullOrWhiteSpace(S("tls")) ? "none" : S("tls").Trim().ToLowerInvariant(),
            Sni = S("sni"),
            Host = S("host"),
            Path = S("path"),
            HeaderType = S("type", "none"),
            Fingerprint = S("fp"),
            Alpn = S("alpn"),
            AllowInsecure = IsTrue(S("allowInsecure")) ||
                IsTrue(S("insecure")) ||
                IsTrue(S("skip-cert-verify")),
            Remark = S("ps")
        };
    }

    private static ConfigProfile ParseShadowsocks(string raw)
    {
        var value = raw["ss://".Length..];
        var hashIndex = value.IndexOf('#');
        var remark = hashIndex >= 0 ? Uri.UnescapeDataString(value[(hashIndex + 1)..]) : string.Empty;
        if (hashIndex >= 0) value = value[..hashIndex];

        var queryIndex = value.IndexOf('?');
        var query = queryIndex >= 0 ? value[(queryIndex + 1)..] : string.Empty;
        if (queryIndex >= 0) value = value[..queryIndex];

        string decoded;
        if (value.Contains('@'))
        {
            var at = value.LastIndexOf('@');
            var credential = value[..at];
            var endpoint = value[(at + 1)..];

            if (!credential.Contains(':'))
                credential = Base64Url.DecodeToString(credential);

            decoded = $"{credential}@{endpoint}";
        }
        else
        {
            decoded = Base64Url.DecodeToString(value);
        }

        var atIndex = decoded.LastIndexOf('@');
        if (atIndex < 0) throw new FormatException("ساختار Shadowsocks معتبر نیست.");

        var credentials = decoded[..atIndex];
        var endpointPart = decoded[(atIndex + 1)..].TrimEnd('/');
        var colon = credentials.IndexOf(':');
        if (colon < 0) throw new FormatException("روش رمزنگاری Shadowsocks موجود نیست.");

        var method = credentials[..colon];
        var password = credentials[(colon + 1)..];

        string host;
        int port;
        if (endpointPart.StartsWith('['))
        {
            var close = endpointPart.IndexOf(']');
            if (close < 0 || close + 2 > endpointPart.Length)
                throw new FormatException("آدرس IPv6 در Shadowsocks معتبر نیست.");

            host = endpointPart[1..close];
            port = int.Parse(endpointPart[(close + 2)..]);
        }
        else
        {
            var lastColon = endpointPart.LastIndexOf(':');
            if (lastColon < 1)
                throw new FormatException("آدرس Shadowsocks معتبر نیست.");

            host = endpointPart[..lastColon];
            port = int.Parse(endpointPart[(lastColon + 1)..]);
        }

        var profile = new ConfigProfile
        {
            Protocol = ProxyProtocol.Shadowsocks,
            Address = host,
            Port = port,
            Encryption = method,
            Password = Uri.UnescapeDataString(password),
            Remark = remark,
            Network = "raw",
            Security = "none"
        };

        if (query.Contains("plugin=", StringComparison.OrdinalIgnoreCase))
        {
            profile.Health = ProfileHealth.Unsupported;
            profile.TestMessage = "پلاگین Shadowsocks در این نسخه پشتیبانی نمی‌شود.";
        }

        return profile;
    }

    private static void ApplyCurrentXrayCompatibility(ConfigProfile profile)
    {
        if (profile.Health == ProfileHealth.Unsupported)
            return;

        if (profile.Security is not ("none" or "" or "tls" or "reality"))
        {
            MarkUnsupported(profile, $"Security «{profile.Security}» در Xray پشتیبانی نمی‌شود.");
            return;
        }

        if (profile.Security == "tls" && profile.AllowInsecure)
        {
            MarkUnsupported(profile, "گزینه allowInsecure در Xray 26.7.28 حذف شده؛ لینک معتبر با تأیید گواهی TLS لازم است.");
            return;
        }

        if ((profile.Security is "none" or "") && !IsPrivateEndpoint(profile.Address) &&
            (profile.Protocol == ProxyProtocol.Trojan ||
             (profile.Protocol == ProxyProtocol.Vless && profile.Encryption is "none" or "")))
        {
            MarkUnsupported(profile, "Xray 26.7.28 اتصال عمومی VLESS/Trojan بدون TLS یا رمزنگاری را نمی‌پذیرد.");
            return;
        }

        if (profile.Network is not ("raw" or "websocket" or "grpc" or "xhttp" or "httpupgrade" or "mkcp" or "http" or "quic"))
        {
            MarkUnsupported(profile, $"Transport «{profile.Network}» پشتیبانی نمی‌شود.");
            return;
        }

        if (profile.Protocol == ProxyProtocol.Vmess && profile.AlterId > 0)
        {
            MarkUnsupported(profile, "VMess با AlterId در Xray جدید پشتیبانی نمی‌شود.");
            return;
        }

        if (profile.Protocol == ProxyProtocol.Trojan && !string.IsNullOrWhiteSpace(profile.Flow))
        {
            MarkUnsupported(profile, "پارامتر Flow برای Trojan در ساختار جدید Xray پشتیبانی نمی‌شود.");
            return;
        }

        if (profile.Network is "http" or "quic")
        {
            MarkUnsupported(profile, "Transport قدیمی h2/http یا QUIC در ساختار جدید Xray پشتیبانی نمی‌شود.");
            return;
        }

        if (profile.Network == "mkcp" &&
            (!string.IsNullOrWhiteSpace(profile.Path) ||
             !string.Equals(profile.HeaderType, "none", StringComparison.OrdinalIgnoreCase)))
        {
            MarkUnsupported(profile, "این لینک از seed/header قدیمی mKCP استفاده می‌کند؛ Xray جدید برای آن FinalMask می‌خواهد.");
            return;
        }

        if (string.Equals(profile.Security, "reality", StringComparison.OrdinalIgnoreCase))
        {
            if (profile.Network is not ("raw" or "xhttp" or "grpc"))
            {
                MarkUnsupported(profile, "REALITY فقط با RAW، XHTTP یا gRPC پشتیبانی می‌شود.");
                return;
            }

            if (string.IsNullOrWhiteSpace(profile.PublicKey))
                MarkUnsupported(profile, "کلید عمومی REALITY در لینک موجود نیست.");
        }
    }

    private static void MarkUnsupported(ConfigProfile profile, string message)
    {
        profile.Health = ProfileHealth.Unsupported;
        profile.TestMessage = message;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pieces[0]);
            var value = pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
            result[key] = value;
        }
        return result;
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key, string fallback = "") =>
        values.TryGetValue(key, out var value) ? value : fallback;

    private static bool IsTrue(string value) => value.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" => true,
        _ => false
    };

    private static string NormalizeNetwork(string network) => network.Trim().ToLowerInvariant() switch
    {
        "" or "tcp" or "raw" => "raw",
        "ws" or "websocket" => "websocket",
        "kcp" or "mkcp" => "mkcp",
        "h2" or "http" => "http",
        "splithttp" or "xhttp" => "xhttp",
        "httpupgrade" => "httpupgrade",
        "grpc" => "grpc",
        "quic" => "quic",
        var value => value
    };

    public static string ComputeId(ConfigProfile profile)
    {
        // JSON preserves field boundaries even when credentials contain '|'.
        var canonical = JsonSerializer.Serialize(new object[] {
            profile.Protocol,
            profile.Address.ToLowerInvariant(),
            profile.Port,
            profile.UserId,
            profile.Password,
            profile.Encryption,
            profile.Network,
            profile.Security,
            profile.Sni,
            profile.Host,
            profile.Path,
            profile.Flow,
            profile.PublicKey,
            profile.ShortId,
            profile.AllowInsecure,
            profile.Fingerprint,
            profile.ServiceName,
            profile.Authority,
            profile.HeaderType,
            profile.Mode,
            profile.Alpn,
            profile.SpiderX,
            profile.AlterId });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
