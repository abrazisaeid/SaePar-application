using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

public static class ProfileCleanupPolicy
{
    public static IReadOnlyList<ConfigProfile> FindRemovable(
        IEnumerable<ConfigProfile> profiles, DateTime now, string? activeProfileId = null)
    {
        var snapshot = profiles.ToList();
        // A completely offline network must not empty the user's server list.
        if (!snapshot.Any(p => p.Health == ProfileHealth.Working &&
            p.LastTested >= now.AddMinutes(-15) && p.LastTested <= now))
            return Array.Empty<ConfigProfile>();

        return snapshot.Where(p => p.Id != activeProfileId &&
            p.Health == ProfileHealth.Failed && p.FailureCount >= 3 &&
            p.FirstSeen <= now.AddDays(-7) && p.LastTested.HasValue).ToList();
    }
}
