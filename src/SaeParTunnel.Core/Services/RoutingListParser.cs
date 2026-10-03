using System.Globalization;
using System.Net;

namespace SaeParTunnel.Core.Services;

public sealed record RoutingListResult(string[] Entries, int InvalidCount);

public static class RoutingListParser
{
    public const int MaxTextLength = 4 * 1024 * 1024;
    public const int MaxEntries = 100_000;

    public static RoutingListResult Parse(string text)
    {
        if (text.Length > MaxTextLength) throw new ArgumentException("حجم فهرست نباید بیشتر از ۴ مگابایت باشد.");
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalid = 0;
        foreach (var token in text.Split(new[] { '\r', '\n', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = token.Trim();
            if (line.StartsWith('#') || line.StartsWith("//")) continue;
            line = line.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var value = Normalize(line);
            if (value is null) { invalid++; continue; }
            entries.Add(value);
            if (entries.Count > MaxEntries) throw new ArgumentException("حداکثر ۱۰۰ هزار مورد می‌توان وارد کرد.");
        }
        return new(entries.Order(StringComparer.OrdinalIgnoreCase).ToArray(), invalid);
    }

    public static string? Normalize(string value)
    {
        value = value.Trim();
        if (value.StartsWith("domain:", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        if (value.StartsWith("*.", StringComparison.Ordinal)) value = value[2..];
        value = value.TrimStart('.');
        if (value.Contains('/') && !value.Contains("://"))
        {
            var parts = value.Split('/');
            if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var address) &&
                int.TryParse(parts[1], out var prefix) && prefix > 0 && prefix <= address.GetAddressBytes().Length * 8)
            {
                var bytes = address.GetAddressBytes();
                for (var i = 0; i < bytes.Length; i++)
                    bytes[i] &= (byte)(0xff << Math.Clamp(8 - (prefix - i * 8), 0, 8));
                return new IPAddress(bytes) + "/" + prefix.ToString(CultureInfo.InvariantCulture);
            }
            // Bare URLs with paths are accepted below, malformed CIDRs are not.
            if (IPAddress.TryParse(parts[0], out _)) return null;
        }
        if (IPAddress.TryParse(value.Trim('[', ']'), out var ip)) return ip.ToString();
        if (!Uri.TryCreate(value.Contains("://") ? value : "https://" + value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(host.Trim('[', ']'), out ip)) return ip.ToString();
        if (host is not ("ir" or "xn--mgba3a4f16a" or "localhost") && !host.Contains('.')) return null;
        if (host.Length > 253 || host.Split('.').Any(label => label.Length is < 1 or > 63 ||
            label.StartsWith('-') || label.EndsWith('-') || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))) return null;
        return host;
    }

    public static bool IsIpEntry(string entry) => entry.Contains('/') || IPAddress.TryParse(entry, out _);
}
