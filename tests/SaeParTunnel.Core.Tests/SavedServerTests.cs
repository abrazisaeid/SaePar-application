using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class SavedServerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "saepar-saved-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private static ConfigProfile Verified(int index) => new()
    {
        Id = index.ToString(), Remark = "سرور " + index, Health = ProfileHealth.Working,
        Address = "server" + index + ".example", Port = 443,
        OriginalUri = "vless://11111111-1111-1111-1111-111111111111@server" + index + ".example:443",
        LastTested = DateTime.Now.AddMinutes(-5), LatencyMs = 100 + index
    };

    [Fact]
    public async Task FailedRetestThenRestartKeepsArchiveHistoryButRemovesFailedHomeServers()
    {
        var profiles = Enumerable.Range(1, 5).Select(Verified).ToList();
        foreach (var profile in profiles) SavedServerPolicy.RememberSuccess(profile);
        var store = new JsonCacheStore();
        var profilesPath = Path.Combine(_directory, "profiles.json");
        var settingsPath = Path.Combine(_directory, "settings.json");
        await store.WriteAsync(profilesPath, profiles);
        await store.WriteAsync(settingsPath, new AppSettings { SelectedServerId = "3" });
        foreach (var profile in profiles)
        {
            profile.Health = ProfileHealth.Failed;
            profile.LastTested = DateTime.Now;
            profile.LatencyMs = null;
            profile.FailureCount++;
        }
        await store.WriteAsync(profilesPath, profiles);
        // New store represents a new application process, using only saved data.
        var restarted = new JsonCacheStore();
        var restored = (await restarted.ReadAsync<List<ConfigProfile>>(profilesPath))!;
        var settings = (await restarted.ReadAsync<AppSettings>(settingsPath))!;
        var home = SavedServerPolicy.ForHome(restored, settings.SelectedServerId);
        Assert.Empty(home);
        Assert.Equal(5, restored.Count);
        Assert.All(restored, p => Assert.Equal(ProfileHealth.Failed, p.Health));
        var third = restored.Single(p => p.Id == settings.SelectedServerId);
        Assert.Equal(profiles[2].OriginalUri, third.OriginalUri);
        Assert.Equal(103, third.LastSuccessfulLatencyMs);
        Assert.NotNull(third.LastSuccessfulTest);
    }

    [Fact]
    public void TcpOnlyAndNeverSuccessfulServersAreNotSavedHomeServers()
    {
        var failed = Verified(1); failed.Health = ProfileHealth.Failed;
        var reachable = Verified(2); reachable.Health = ProfileHealth.Reachable;
        SavedServerPolicy.RememberSuccess(failed);
        SavedServerPolicy.RememberSuccess(reachable);
        Assert.Empty(SavedServerPolicy.ForHome(new[] { failed, reachable }, null));
    }

    [Fact]
    public void RemovedProtocolIsNotOfferedEvenWhenItPreviouslyWorked()
    {
        var profile = Verified(1);
        SavedServerPolicy.RememberSuccess(profile);
        profile.Health = ProfileHealth.Unsupported;
        Assert.False(SavedServerPolicy.IsSaved(profile));
    }

    [Fact]
    public void LegacyWorkingServerCanMigrateAndSurviveAFailedRetest()
    {
        var profile = Verified(1);
        SavedServerPolicy.RememberSuccess(profile);
        profile.Health = ProfileHealth.Failed;
        profile.LatencyMs = null;
        SavedServerPolicy.RememberSuccess(profile);
        Assert.True(SavedServerPolicy.IsSaved(profile));
        Assert.False(SavedServerPolicy.IsReady(profile));
        Assert.Equal(101, profile.LastSuccessfulLatencyMs);
    }

    [Fact]
    public void SelectedServerOutsideFirstFiveIsStillShownAndListStaysSmall()
    {
        var profiles = Enumerable.Range(1, 100).Select(Verified).ToArray();
        var home = SavedServerPolicy.ForHome(profiles, "99");
        Assert.Equal(5, home.Count);
        Assert.Contains(home, p => p.Id == "99");
    }

    [Fact]
    public void OldBackupRecoversSuccessHistoryWithoutOverridingLatestFailure()
    {
        var current = Verified(1); current.Health = ProfileHealth.Failed; current.LatencyMs = null;
        var previous = Verified(1);
        SavedServerPolicy.RecoverHistory(current, previous);
        Assert.True(SavedServerPolicy.IsSaved(current));
        Assert.False(SavedServerPolicy.IsReady(current));
        Assert.Equal(ProfileHealth.Failed, current.Health);
        Assert.Null(current.LatencyMs);
        Assert.Equal(previous.LastTested, current.LastSuccessfulTest);
        Assert.Equal(previous.LatencyMs, current.LastSuccessfulLatencyMs);
    }

    [Fact]
    public void SelectedFailedServerDoesNotReturnToHomeAndRemainingServersStayInOrder()
    {
        var profiles = Enumerable.Range(1, 5).Select(Verified).ToArray();
        foreach (var profile in profiles) SavedServerPolicy.RememberSuccess(profile);
        profiles[2].Health = ProfileHealth.Failed;
        profiles[2].FailureCount = 1;
        profiles[2].LatencyMs = null;
        var home = SavedServerPolicy.ForHome(profiles, profiles[2].Id);
        Assert.Equal(new[] { "1", "2", "4", "5" }, home.Select(profile => profile.Id));
        Assert.DoesNotContain(home, profile => profile.Id == profiles[2].Id);
        Assert.True(SavedServerPolicy.IsSaved(profiles[2]));
    }

    [Fact]
    public void SuccessfulFullRetestReturnsHiddenServerToHome()
    {
        var profile = Verified(1);
        SavedServerPolicy.RememberSuccess(profile);
        profile.Health = ProfileHealth.Failed;
        profile.FailureCount = 2;
        Assert.Empty(SavedServerPolicy.ForHome(new[] { profile }, profile.Id));
        profile.Health = ProfileHealth.Testing;
        Assert.Empty(SavedServerPolicy.ForHome(new[] { profile }, profile.Id));
        profile.Health = ProfileHealth.Reachable;
        Assert.Empty(SavedServerPolicy.ForHome(new[] { profile }, profile.Id));
        profile.Health = ProfileHealth.Working;
        profile.LastTested = DateTime.Now;
        profile.LatencyMs = 80;
        profile.FailureCount = 0;
        SavedServerPolicy.RememberSuccess(profile);
        Assert.Same(profile, Assert.Single(SavedServerPolicy.ForHome(new[] { profile }, profile.Id)));
        Assert.Equal(80, profile.LastSuccessfulLatencyMs);
        Assert.Empty(ProfileCleanupPolicy.FindRemovable(new[] { profile }, DateTime.Now));
    }
}
