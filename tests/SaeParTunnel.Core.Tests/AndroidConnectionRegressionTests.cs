using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class AndroidConnectionRegressionTests
{
    [Fact]
    public void DeviceStartupFailureStopsRetriesButTrafficFailureCanTryAnotherServer()
    {
        Assert.False(ConnectionAttemptPolicy.ShouldTryAnotherServer(new TunnelStartupException("Permission denied")));
        Assert.False(ConnectionAttemptPolicy.ShouldTryAnotherServer(new OperationCanceledException()));
        Assert.True(ConnectionAttemptPolicy.ShouldTryAnotherServer(new TunnelValidationException("HTTP timeout")));
    }

    [Fact]
    public async Task LargeRoutingConfigTransfersWithSmallTokenAndIsDeletedAfterConsumption()
    {
        var directory = Path.Combine(Path.GetTempPath(), "saepar-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TunnelConfigurationStore(directory);
            var profile = new ConfigProfile
            {
                Protocol = ProxyProtocol.Vless, Address = "127.0.0.1", Port = 443,
                UserId = "11111111-1111-1111-1111-111111111111", Encryption = "none",
                Network = "raw", Security = "none"
            };
            var json = new XrayConfigBuilder().BuildAndroidTun(profile, new AppSettings());
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) > 1024 * 1024);
            var token = await store.WriteAsync(json);
            Assert.Equal(32, token.Length);
            Assert.Single(Directory.GetFiles(directory));
            Assert.Equal(json, await store.ConsumeAsync(token));
            Assert.Empty(Directory.GetFiles(directory));
            store.Delete(token); // caller cleanup is safe after service consumption
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("C:\\outside.json")]
    [InlineData("")]
    [InlineData("0000000000000000000000000000000/")]
    public async Task ServiceRejectsArbitraryPaths(string token)
    {
        var store = new TunnelConfigurationStore(Path.GetTempPath());
        await Assert.ThrowsAsync<ArgumentException>(() => store.ConsumeAsync(token));
        Assert.Throws<ArgumentException>(() => store.Delete(token));
    }

    [Fact]
    public async Task CancelledWriteLeavesNoConfigurationBehind()
    {
        var directory = Path.Combine(Path.GetTempPath(), "saepar-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TunnelConfigurationStore(directory);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync("{}", cts.Token));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void FailedVpnStartupDoesNotEraseFivePreviouslyVerifiedServers()
    {
        var testedAt = DateTime.Now.AddMinutes(-2);
        var profiles = Enumerable.Range(0, 5).Select(i => new ConfigProfile
        {
            Id = i.ToString(), Health = ProfileHealth.Working, LastTested = testedAt,
            LatencyMs = 123, FailureCount = 0
        }).ToArray();
        foreach (var profile in profiles)
            ConnectionAttemptPolicy.RecordFailure(profile, "VPN permission/service/TUN failure");
        Assert.Equal(5, profiles.Count(x => x.Health == ProfileHealth.Working));
        Assert.All(profiles, p =>
        {
            Assert.Equal(testedAt, p.LastTested);
            Assert.Equal(123, p.LatencyMs);
            Assert.Equal(0, p.FailureCount);
        });
        Assert.Empty(ProfileCleanupPolicy.FindRemovable(profiles, DateTime.Now));
    }
}
