using Microsoft.Data.Sqlite;
using SaeParTunnel.App.Services;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class SqliteProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "saepar-sqlite-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "profiles.db");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static ConfigProfile Profile(int i) => new()
    {
        Id = i.ToString(), Address = "127.0.0.1", Port = 2000 + i,
        Protocol = ProxyProtocol.Shadowsocks, Encryption = "aes-128-gcm", Password = "test-secret",
        OriginalUri = $"ss://aes-128-gcm:test-secret@127.0.0.1:{2000+i}#server{i}",
        Health = ProfileHealth.Working, LatencyMs = 100 + i, LastTested = DateTime.Now,
        LastSuccessfulTest = DateTime.Now, LastSuccessfulLatencyMs = 100 + i,
        CommunityScore = 88, CommunitySource = "test", FailureCount = 2
    };

    [Fact]
    public async Task SnapshotUpdatesOnlyChangedRowsAndKeepsSecretsAndHistory()
    {
        var store = new SqliteProfileStore(Database);
        var profiles = Enumerable.Range(1, 200).Select(Profile).ToList();
        Assert.Equal(200, await store.SaveSnapshotAsync(profiles));
        Assert.Equal(0, await store.SaveSnapshotAsync(profiles));
        profiles[2].LatencyMs = 321;
        Assert.Equal(1, await store.SaveSnapshotAsync(profiles));
        var restored = (await new SqliteProfileStore(Database).LoadAsync())!;
        Assert.Equal(200, restored.Count);
        var third = restored.Single(p => p.Id == "3");
        Assert.Equal(321, third.LatencyMs);
        Assert.Equal("test-secret", third.Password);
        Assert.Equal(profiles[2].OriginalUri, third.OriginalUri);
        Assert.Equal(profiles[2].LastSuccessfulTest, third.LastSuccessfulTest);
        Assert.Equal(103, third.LastSuccessfulLatencyMs);
        Assert.Equal(88, third.CommunityScore);
        Assert.Equal(2, third.FailureCount);
    }

    [Fact]
    public async Task FailedSnapshotRollsBackEveryRowAndCanBeRetried()
    {
        var store = new SqliteProfileStore(Database);
        var first = Profile(1);
        await store.SaveSnapshotAsync([first]);
        first.Remark = "updated";
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSnapshotAsync([first, Profile(2), Profile(2)]));
        var restored = (await new SqliteProfileStore(Database).LoadAsync())!;
        Assert.Single(restored);
        Assert.Equal(string.Empty, restored[0].Remark);
        Assert.Equal(1, await store.SaveSnapshotAsync([first]));
    }

    [Fact]
    public async Task ConcurrentIndividualPingsAreCommittedWithoutOverwritingOtherServers()
    {
        var store = new SqliteProfileStore(Database);
        var profiles = Enumerable.Range(1, 20).Select(Profile).ToList();
        await store.SaveSnapshotAsync(profiles);
        await Task.WhenAll(profiles.Select(p => { p.LatencyMs += 500; return store.SaveProfileAsync(p); }));
        var restored = (await new SqliteProfileStore(Database).LoadAsync())!;
        Assert.All(restored, p => Assert.Equal(600 + int.Parse(p.Id), p.LatencyMs));
    }

    [Fact]
    public async Task LatePingCannotRecreateADeletedProfile()
    {
        var store = new SqliteProfileStore(Database);
        var removed = Profile(2);
        await store.SaveSnapshotAsync([Profile(1), removed]);
        await store.SaveSnapshotAsync([Profile(1)]);
        await Assert.ThrowsAsync<IOException>(() => store.SaveProfileAsync(removed));
        Assert.Single((await new SqliteProfileStore(Database).LoadAsync())!);
    }

    [Fact]
    public async Task CorruptRecordBlocksWritesUntilSuccessfulRecovery()
    {
        var store = new SqliteProfileStore(Database);
        await store.SaveSnapshotAsync([Profile(1)]);
        using (var connection = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE profiles SET data='{broken'";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAsync());
        await Assert.ThrowsAsync<IOException>(() => store.SaveSnapshotAsync([Profile(2)]));
        await Assert.ThrowsAsync<IOException>(() => store.SaveProfileAsync(Profile(1)));
    }

    [Fact]
    public async Task JsonMigrationKeepsOriginalFilesAndNeverRevivesDeletedRowsAfterRestart()
    {
        var json = Path.Combine(_root, "profiles.json");
        var profiles = new List<ConfigProfile> { Profile(1), Profile(2), Profile(3) };
        await new JsonCacheStore().WriteAsync(json, profiles);
        var original = await File.ReadAllBytesAsync(json);
        var store = new MauiJsonStore(_root);
        var migrated = await store.LoadProfilesAsync();
        Assert.Equal(3, migrated.Count);
        migrated.RemoveAll(p => p.Port == 2003);
        await store.SaveProfilesAsync(migrated);
        Assert.Equal(original, await File.ReadAllBytesAsync(json));
        var restarted = await new MauiJsonStore(_root).LoadProfilesAsync();
        Assert.Equal(2, restarted.Count);
        Assert.DoesNotContain(restarted, p => p.Port == 2003);
        Assert.All(restarted, p => Assert.NotNull(p.LastSuccessfulTest));
    }

    [Fact]
    public async Task BrokenLegacyArchiveCannotBeReplacedByAnEmptyMigration()
    {
        Directory.CreateDirectory(_root);
        var json = Path.Combine(_root, "profiles.json");
        await File.WriteAllTextAsync(json, "{broken");
        var store = new MauiJsonStore(_root);
        await Assert.ThrowsAsync<IOException>(() => store.LoadProfilesAsync());
        await Assert.ThrowsAsync<IOException>(() => store.SaveProfilesAsync([]));
        Assert.Equal("{broken", await File.ReadAllTextAsync(json));
        Assert.Null(await new SqliteProfileStore(Database).LoadAsync());
    }
}
