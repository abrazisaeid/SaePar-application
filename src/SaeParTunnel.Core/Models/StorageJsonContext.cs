using System.Text.Json.Serialization;

namespace SaeParTunnel.Core.Models;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<ConfigProfile>))]
[JsonSerializable(typeof(HomeServerSnapshot))]
public partial class StorageJsonContext : JsonSerializerContext { }
