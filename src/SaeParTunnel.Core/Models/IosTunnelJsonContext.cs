using System.Text.Json.Serialization;

namespace SaeParTunnel.Core.Models;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(IosTunnelRequest))]
public partial class IosTunnelJsonContext : JsonSerializerContext
{
}
