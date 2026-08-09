namespace SaeParTunnel.Core.Models;

public enum ProxyProtocol
{
    Vless,
    Vmess,
    Trojan,
    Shadowsocks,
    Unknown
}

public enum ProfileHealth
{
    Untested,
    Testing,
    Working,
    Failed,
    Unsupported,
    Reachable
}

public enum ValidationLevel
{
    None,
    EndpointOnly,
    FullProxy
}
