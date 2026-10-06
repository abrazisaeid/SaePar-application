using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

public static class SavedServerPolicy
{
    public static void RememberSuccess(ConfigProfile profile)
    {
        if (profile.Health != ProfileHealth.Working) return;
        profile.LastSuccessfulTest = profile.LastTested ?? DateTime.Now;
        profile.LastSuccessfulLatencyMs = profile.LatencyMs;
    }

    public static bool IsSaved(ConfigProfile profile) =>
        profile.Health != ProfileHealth.Unsupported &&
        (profile.Health == ProfileHealth.Working || profile.LastSuccessfulTest.HasValue);

    public static IReadOnlyList<ConfigProfile> ForHome(IEnumerable<ConfigProfile> profiles,
        string? selectedId, int limit = 5)
    {
        var saved = profiles.Where(IsSaved)
            .OrderByDescending(p => p.Health == ProfileHealth.Working)
            .ThenByDescending(p => p.QualityScore)
            .ThenBy(p => p.LatencyMs ?? p.LastSuccessfulLatencyMs ?? int.MaxValue)
            .ThenByDescending(p => p.LastSuccessfulTest ?? p.LastTested).ToList();
        var visible = saved.Take(limit).ToList();
        var selected = saved.FirstOrDefault(p => p.Id == selectedId);
        // Selecting the third row must not move it to the top on every refresh.
        if (limit > 0 && selected is not null && !visible.Contains(selected)) visible[^1] = selected;
        return visible;
    }

    public static void RecoverHistory(ConfigProfile profile, ConfigProfile previous)
    {
        if (profile.LastSuccessfulTest.HasValue || profile.Health == ProfileHealth.Unsupported) return;
        if (!IsSaved(previous)) return;
        profile.LastSuccessfulTest = previous.LastSuccessfulTest ?? previous.LastTested;
        profile.LastSuccessfulLatencyMs = previous.LastSuccessfulLatencyMs ?? previous.LatencyMs;
    }
}
