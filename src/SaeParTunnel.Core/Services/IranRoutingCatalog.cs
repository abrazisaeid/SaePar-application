using System.IO.Compression;
using System.Text.Json;

namespace SaeParTunnel.Core.Services;

public sealed class IranRoutingCatalog
{
    private static readonly Lazy<IranRoutingCatalog> Bundled = new(Load);
    public static IranRoutingCatalog Default => Bundled.Value;
    public string DomainSnapshot { get; init; } = "";
    public string IpSnapshot { get; init; } = "";
    public string[] DirectDomains { get; init; } = Array.Empty<string>();
    public string[] ProxyDomains { get; init; } = Array.Empty<string>();
    public string[] IpRanges { get; init; } = Array.Empty<string>();

    private static IranRoutingCatalog Load()
    {
        using var resource = typeof(IranRoutingCatalog).Assembly.GetManifestResourceStream("IranRoutingCatalog.json.gz")
            ?? throw new InvalidDataException("Bundled Iran routing data is missing.");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        var catalog = JsonSerializer.Deserialize(gzip, IranRoutingJsonContext.Default.IranRoutingCatalog)
            ?? throw new InvalidDataException("Bundled Iran routing data is invalid.");
        if (catalog.DirectDomains.Length < 1000 || catalog.IpRanges.Length < 100)
            throw new InvalidDataException("Bundled Iran routing data is incomplete.");
        return catalog;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(IranRoutingCatalog))]
internal partial class IranRoutingJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
