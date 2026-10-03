using System.Text.Json;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class CachePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "saepar-cache-test-" + Guid.NewGuid().ToString("N"));
    private string PathFor(string name) => Path.Combine(_directory, name);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private static List<ConfigProfile> Healthy() => Enumerable.Range(0, 5).Select(i => new ConfigProfile
    {
        Id = i.ToString(), Remark = "سرور " + i, Protocol = ProxyProtocol.Vless,
        Address = "127.0.0.1", Port = 443, UserId = "11111111-1111-1111-1111-111111111111",
        Health = ProfileHealth.Working, LatencyMs = 100 + i,
        LastTested = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Local),
        TestMessage = "تست سالم", FailureCount = 0
    }).ToList();

    [Fact]
    public async Task RestartRestoresAllFiveVerifiedServersAndTheirPing()
    {
        var path = PathFor("profiles.json");
        var expected = Healthy();
        await new JsonCacheStore().WriteAsync(path, expected);
        var restored = await new JsonCacheStore().ReadAsync<List<ConfigProfile>>(path);
        Assert.Equal(5, restored!.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(ProfileHealth.Working, restored[i].Health);
            Assert.Equal(expected[i].Remark, restored[i].Remark);
            Assert.Equal(expected[i].LatencyMs, restored[i].LatencyMs);
            Assert.Equal(expected[i].LastTested, restored[i].LastTested);
            Assert.Equal(expected[i].UserId, restored[i].UserId);
        }
    }

    [Fact]
    public async Task CorruptPrimaryRecoversPreviousVerifiedServersAndRepairsPrimary()
    {
        var path = PathFor("profiles.json");
        var store = new JsonCacheStore();
        await store.WriteAsync(path, Healthy());
        await store.WriteAsync(path, new List<ConfigProfile>());
        await File.WriteAllTextAsync(path, "[broken");
        var restored = await new JsonCacheStore().ReadAsync<List<ConfigProfile>>(path);
        Assert.Equal(5, restored!.Count);
        Assert.All(restored, p => Assert.Equal(ProfileHealth.Working, p.Health));
        Assert.Equal(5, (await new JsonCacheStore().ReadAsync<List<ConfigProfile>>(path))!.Count);
    }

    [Fact]
    public async Task CompletedFirstWriteCanBeRecoveredAfterProcessDiesBeforeRename()
    {
        var path = PathFor("profiles.json");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(Healthy(), StorageJsonContext.Default.ListConfigProfile));
        Assert.Equal(5, (await new JsonCacheStore().ReadAsync<List<ConfigProfile>>(path))!.Count);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task UnreadableCacheReportsErrorAndNeverBecomesASilentEmptyList()
    {
        var path = PathFor("profiles.json");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(path, "[broken");
        await File.WriteAllTextAsync(path + ".bak", "null");
        await Assert.ThrowsAsync<IOException>(() => new JsonCacheStore().ReadAsync<List<ConfigProfile>>(path));
        Assert.Equal("[broken", await File.ReadAllTextAsync(path));
        Assert.Equal("null", await File.ReadAllTextAsync(path + ".bak"));
    }

    [Fact]
    public async Task SourceGeneratedSettingsContractPreservesRoutingAndSubscriptions()
    {
        var settings = new AppSettings
        {
            EnableIranBypass = true, EnableWhitelistRouting = false,
            DirectRoutingEntries = new() { "example.ir", "192.0.2.0/24" },
            SuppressedProfileIds = new() { ["old"] = DateTime.UtcNow.AddDays(7) },
            Subscriptions = new() { new SubscriptionSource { Name = "test", Url = "https://example.com/sub", IsEnabled = true } }
        };
        var path = PathFor("settings.json");
        await new JsonCacheStore().WriteAsync(path, settings);
        var restored = await new JsonCacheStore().ReadAsync<AppSettings>(path);
        Assert.True(restored!.EnableIranBypass);
        Assert.Equal(settings.DirectRoutingEntries, restored.DirectRoutingEntries);
        Assert.Equal(settings.Subscriptions[0].Url, restored.Subscriptions[0].Url);
        Assert.Equal(settings.SuppressedProfileIds["old"], restored.SuppressedProfileIds["old"]);
    }

    [Fact]
    public async Task MissingCacheIsDifferentFromUnreadableCache()
    {
        Assert.Null(await new JsonCacheStore().ReadAsync<List<ConfigProfile>>(PathFor("profiles.json")));
    }
}
