using System.Text.RegularExpressions;
using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

public sealed partial class ConfigExtractor
{
    private readonly ConfigParser _parser;

    public ConfigExtractor(ConfigParser parser) => _parser = parser;

    public IReadOnlyList<ConfigProfile> Extract(string text, string source)
    {
        var results = new List<ConfigProfile>();
        foreach (Match match in ConfigRegex().Matches(text ?? string.Empty))
        {
            var raw = match.Value.TrimEnd('.', ',', ';', ')', ']', '}', '>', '،', '؛');
            var parsed = _parser.Parse(raw, source, out _);
            if (parsed is not null) results.Add(parsed);
        }
        return results;
    }

    [GeneratedRegex(@"(?i)\b(?:vmess|vless|trojan|ss)://[^\s<>()""']+", RegexOptions.Compiled)]
    private static partial Regex ConfigRegex();
}
