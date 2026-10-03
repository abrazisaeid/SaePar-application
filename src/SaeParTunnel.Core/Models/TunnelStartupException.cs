namespace SaeParTunnel.Core.Models;

// A failure to create/start the local VPN is not evidence of a bad server.
public sealed class TunnelStartupException : Exception
{
    public TunnelStartupException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

// The service started but its internet check failed. Another server can be tried.
public sealed class TunnelValidationException : Exception
{
    public TunnelValidationException(string message) : base(message) { }
}
