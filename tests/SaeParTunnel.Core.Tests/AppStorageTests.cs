using SaeParTunnel.App.Services;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class AppStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "saepar-app-store-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private static List<ConfigProfile> Profiles()
    {
        var parser = new ConfigParser();
        return Enumerable.Range(1, 5).Select(i =>
        {
            var profile = parser.Parse($"vless://11111111-1111-1111-1111-111111111111@server{i}.example:443?type=tcp&security=none#server{i}", "test", out _)!;
            profile.Health = ProfileHealth.Working;
            profile.LatencyMs = 100 + i;
            profile.LastTested = DateTime.Now.AddMinutes(-5);
            return profile;
        }).ToList();
    }

    [Fact]
    public async Task ActualAppStoreMigratesLegacyServersAndRestoresThirdServerAfterRestart()
    {
        var store = new MauiJsonStore(_directory);
        var profiles = Profiles();
        await store.SaveProfilesAsync(profiles);
        await store.SaveSettingsAsync(new AppSettings { SelectedServerId = profiles[2].Id });
        var restarted = new MauiJsonStore(_directory);
        var restored = await restarted.LoadProfilesAsync();
        var settings = await restarted.LoadSettingsAsync();
        Assert.Equal(5, SavedServerPolicy.ForHome(restored, settings.SelectedServerId).Count);
        Assert.All(restored, p => Assert.NotNull(p.LastSuccessfulTest));
        var third = restored.Single(p => p.Id == settings.SelectedServerId);
        Assert.Equal(profiles[2].OriginalUri, third.OriginalUri);
        Assert.Equal(103, third.LastSuccessfulLatencyMs);
        Assert.Equal(103, third.LatencyMs);
    }

    [Fact]
    public async Task UpgradeRecoversLegacySuccessFromBackupWithoutResurrectingDeletedServers()
    {
        var store = new MauiJsonStore(_directory);
        var profiles = Profiles();
        await store.SaveProfilesAsync(profiles);
        profiles.RemoveAt(4);
        foreach (var profile in profiles) { profile.Health = ProfileHealth.Failed; profile.LatencyMs = null; }
        await store.SaveProfilesAsync(profiles);
        var restored = await new MauiJsonStore(_directory).LoadProfilesAsync();
        Assert.Equal(4, restored.Count);
        Assert.Equal(4, SavedServerPolicy.ForHome(restored, null).Count);
        Assert.All(restored, p => Assert.Equal(ProfileHealth.Failed, p.Health));
        Assert.DoesNotContain(restored, p => p.Address == "server5.example");
    }

    [Fact]
    public async Task CorruptSettingsLeaveProfilesReadableAndPreventDefaultSettingsOverwrite()
    {
        var store = new MauiJsonStore(_directory);
        await store.SaveProfilesAsync(Profiles());
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "{broken");
        await Assert.ThrowsAsync<IOException>(() => store.LoadSettingsAsync());
        Assert.Equal(5, (await store.LoadProfilesAsync()).Count);
        await Assert.ThrowsAsync<IOException>(() => store.SaveSettingsAsync(new AppSettings()));
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
    }
}
