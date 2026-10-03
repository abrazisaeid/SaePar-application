using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

public static class ConnectionAttemptPolicy
{
    public static bool ShouldTryAnotherServer(Exception error) =>
        error is not TunnelStartupException && error is not OperationCanceledException;

    public static void RecordFailure(ConfigProfile profile, string message)
    {
        // Connection/TUN validation and standalone server testing are different
        // observations. Only a standalone test may demote server health or count
        // toward deleting old profiles. Keep the last successful test timestamp.
        profile.TestMessage = message;
    }
}
