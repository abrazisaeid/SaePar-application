namespace SaeParTunnel.Core.Models;

public sealed class HomeServerSnapshot
{
    public List<ConfigProfile> Profiles { get; set; } = new();
}
