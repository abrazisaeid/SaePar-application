using SaeParTunnel.App.Services;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class AppStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "saepar-app-store-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Theory]
    [InlineData("ss://aes-128-gcm:plugin=@server.example:443")]
    [InlineData("ss://aes-128-gcm:password@server.example:443#plugin=remark")]
    public void CachedShadowsocksPasswordAndRemarkAreNotTreatedAsPluginSettings(string originalUri)
    {
        var profile = new ConfigProfile
        {
            Protocol = ProxyProtocol.Shadowsocks, OriginalUri = originalUri,
            Address = "server.example", Port = 443, Encryption = "aes-128-gcm", Health = ProfileHealth.Working
        };
        ConfigParser.ValidateCachedProfile(profile);
        Assert.Equal(ProfileHealth.Working, profile.Health);
    }
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
    public async Task TenBackupServersSurviveFastCacheAndArchiveRestartWithSelectedServerPreserved()
    {
        var parser = new ConfigParser();
        var profiles = Enumerable.Range(1, 10).Select(i =>
        {
            var profile = parser.Parse($"vless://11111111-1111-1111-1111-111111111111@backup{i}.example:443?type=tcp&security=none#backup{i}", "test", out _)!;
            profile.Health = ProfileHealth.Working;
            profile.LastTested = DateTime.Now;
            profile.LatencyMs = 100 + i;
            return profile;
        }).ToList();
        var store = new MauiJsonStore(_directory);
        await store.SaveSettingsAsync(new AppSettings { SelectedServerId = profiles[2].Id });
        await store.SaveProfilesAsync(profiles);
        var restarted = new MauiJsonStore(_directory);
        await restarted.LoadSettingsAsync();
        Assert.Equal(10, (await restarted.LoadHomeServersAsync()).Count);
        var archive = await restarted.LoadProfilesAsync();
        Assert.Equal(10, archive.Count);
        Assert.Equal(profiles[2].Id, (await restarted.LoadSettingsAsync()).SelectedServerId);
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
        // Write the pre-snapshot contract to model an actual older installation.
        var store = new JsonCacheStore();
        var profiles = Profiles();
        var path = Path.Combine(_directory, "profiles.json");
        await store.WriteAsync(path, profiles);
        profiles.RemoveAt(4);
        foreach (var profile in profiles) { profile.Health = ProfileHealth.Failed; profile.LatencyMs = null; }
        await store.WriteAsync(path, profiles);
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

    [Fact]
    public async Task HomeSnapshotLoadsWithoutOpeningOrWaitingForTheLargeArchive()
    {
        var profiles = Profiles();
        var store = new MauiJsonStore(_directory);
        await store.SaveProfilesAsync(profiles);
        using var lockedArchive = File.Open(Path.Combine(_directory, "profiles.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var home = await new MauiJsonStore(_directory).LoadHomeServersAsync();
        Assert.Equal(5, home.Count);
        Assert.Equal(profiles.Select(p => p.Id).Order(), home.Select(p => p.Id).Order());
        Assert.All(home, p => Assert.Equal(ProfileHealth.Working, p.Health));
    }

    [Fact]
    public async Task SavedThirdServerIsAvailableFromHomeCacheBeforeArchiveRestoration()
    {
        var profiles = Profiles();
        var store = new MauiJsonStore(_directory);
        await store.SaveSettingsAsync(new AppSettings { SelectedServerId = profiles[2].Id });
        await store.SaveProfilesAsync(profiles);
        var restarted = new MauiJsonStore(_directory);
        var settings = await restarted.LoadSettingsAsync();
        var home = await restarted.LoadHomeServersAsync();
        Assert.Contains(home, p => p.Id == settings.SelectedServerId && p.OriginalUri == profiles[2].OriginalUri);
    }

    [Fact]
    public async Task CleanupAlsoRemovesServerFromFastSnapshot()
    {
        var profiles = Profiles();
        var store = new MauiJsonStore(_directory);
        await store.SaveProfilesAsync(profiles);
        var removedId = profiles[2].Id;
        profiles.RemoveAt(2);
        await store.SaveProfilesAsync(profiles);
        var home = await new MauiJsonStore(_directory).LoadHomeServersAsync();
        Assert.Equal(4, home.Count);
        Assert.DoesNotContain(home, p => p.Id == removedId);
    }

    [Fact]
    public async Task CorruptOptionalHomeCacheCannotPreventSavingTheServerArchive()
    {
        var store = new MauiJsonStore(_directory);
        var profiles = Profiles();
        await store.SaveProfilesAsync(profiles);
        await File.WriteAllTextAsync(Path.Combine(_directory, "home-servers.json"), "{broken");
        await Assert.ThrowsAsync<IOException>(() => store.LoadHomeServersAsync());
        profiles[2].Remark = "updated third server";
        await store.SaveProfilesAsync(profiles);
        var restored = await store.LoadProfilesAsync();
        Assert.Contains(restored, p => p.Id == profiles[2].Id && p.Remark == "updated third server");
    }
}
