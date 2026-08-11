using System.Text;
using System.Text.RegularExpressions;
using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

public sealed partial class ConfigExtractor
{
    private const int MaxEncodedSubscriptionLength = 32 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ConfigParser _parser;

    public ConfigExtractor(ConfigParser parser) => _parser = parser;

    public IReadOnlyList<ConfigProfile> Extract(string text, string source)
    {
        text ??= string.Empty;
        var results = ExtractPlainText(text, source);
        if (results.Count > 0 || !TryDecodeSubscription(text, out var decoded))
            return results;

        return ExtractPlainText(decoded, source);
    }

    private List<ConfigProfile> ExtractPlainText(string text, string source)
    {
        var results = new List<ConfigProfile>();
        foreach (Match match in ConfigRegex().Matches(text))
        {
            var raw = match.Value.TrimEnd('.', ',', ';', ')', ']', '}', '>', '،', '؛');
            var parsed = _parser.Parse(raw, source, out _);
            if (parsed is not null) results.Add(parsed);
        }
        return results;
    }

    private static bool TryDecodeSubscription(string text, out string decoded)
    {
        decoded = string.Empty;
        if (text.Length is < 16 or > MaxEncodedSubscriptionLength)
            return false;

        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c))).TrimStart('\uFEFF');
        if (compact.Length < 16)
            return false;

        compact = compact.Replace('-', '+').Replace('_', '/');
        var remainder = compact.Length % 4;
        if (remainder == 1)
            return false;
        if (remainder > 0)
            compact = compact.PadRight(compact.Length + 4 - remainder, '=');

        try
        {
            decoded = StrictUtf8.GetString(Convert.FromBase64String(compact));
            return ConfigRegex().IsMatch(decoded);
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            decoded = string.Empty;
            return false;
        }
    }

    [GeneratedRegex(@"(?i)\b(?:vmess|vless|trojan|ss)://[^\s<>()""']+", RegexOptions.Compiled)]
    private static partial Regex ConfigRegex();
}
