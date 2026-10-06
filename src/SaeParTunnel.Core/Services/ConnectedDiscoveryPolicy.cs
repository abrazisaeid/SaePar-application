using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

public static class ConnectedDiscoveryPolicy
{
    public const int HealthyTarget = 10;
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(30);

    public static bool IsFresh(ConfigProfile profile, DateTime now) =>
        profile.Health == ProfileHealth.Working && profile.LastTested is { } tested &&
        tested <= now && now - tested <= Freshness;

    public static int CountReady(IEnumerable<ConfigProfile> profiles, DateTime now) =>
        profiles.Where(profile => IsFresh(profile, now)).DistinctBy(profile => profile.Id).Count();

    public static IReadOnlyList<ConfigProfile> Candidates(IEnumerable<ConfigProfile> profiles,
        string? activeId, DateTime now) => ConfigTestPlanner.OrderForHealthySearch(profiles
            .Where(profile => profile.Id != activeId && profile.Health != ProfileHealth.Unsupported && !IsFresh(profile, now))
            .DistinctBy(profile => profile.Id));
}
