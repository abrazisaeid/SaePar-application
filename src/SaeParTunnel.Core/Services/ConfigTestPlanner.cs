using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

public static class ConfigTestPlanner
{
    // Searching should not follow alphabetical UI order through an ever-growing
    // archive of dead servers. Keep explicit/manual test ordering separate.
    private static int Priority(ConfigProfile profile) => profile.Health switch
        {
            ProfileHealth.Working => 0,
            ProfileHealth.Reachable => 1,
            ProfileHealth.Untested => 2,
            ProfileHealth.Unsupported => 4,
            _ => 3
        };

    public static IReadOnlyList<ConfigProfile> OrderForHealthySearch(IEnumerable<ConfigProfile> profiles)
    {
        var snapshot = profiles.ToList();
        var recentSuccesses = snapshot.Where(p => p.Health == ProfileHealth.Working &&
                p.LastTested >= DateTime.Now.AddHours(-24))
            .GroupBy(p => (p.Protocol, p.Network, p.Security))
            .ToDictionary(group => group.Key, group => group.Count());
        var ordered = snapshot.OrderBy(Priority)
        .ThenByDescending(p => recentSuccesses.GetValueOrDefault((p.Protocol, p.Network, p.Security)))
        .ThenByDescending(p => p.LastSeen.Date)
        .ThenBy(p => p.FailureCount)
        .ThenBy(p => p.LastTested ?? DateTime.MinValue)
        .ToList();

        // Try different servers before spending the search budget on many link
        // variants of one endpoint. Keep every variant for a later pass.
        return ordered.GroupBy(Priority).SelectMany(group =>
        {
            var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var first = new List<ConfigProfile>();
            var variants = new List<ConfigProfile>();
            foreach (var profile in group)
            {
                if (endpoints.Add($"{profile.Address}:{profile.Port}")) first.Add(profile);
                else variants.Add(profile);
            }
            return first.Concat(variants);
        }).ToList();
    }
}
