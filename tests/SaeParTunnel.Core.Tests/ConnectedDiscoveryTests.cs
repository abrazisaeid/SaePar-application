using System.Net;
using System.Net.Sockets;
using SaeParTunnel.Core.Abstractions;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class ConnectedDiscoveryTests
{
    [Fact]
    public void OnlyRecentFullSuccessCountsAndActiveServerIsNeverRetested()
    {
        var now = DateTime.Now;
        var profiles = new[]
        {
            Profile("active", ProfileHealth.Working, now),
            Profile("fresh", ProfileHealth.Working, now.AddMinutes(-10)),
            Profile("old", ProfileHealth.Working, now.AddHours(-1)),
            Profile("failed", ProfileHealth.Failed, now),
            Profile("tcp", ProfileHealth.Reachable, now),
            Profile("future", ProfileHealth.Working, now.AddDays(1)),
            Profile("unsupported", ProfileHealth.Unsupported, now)
        };
        Assert.Equal(2, ConnectedDiscoveryPolicy.CountReady(profiles, now));
        var candidates = ConnectedDiscoveryPolicy.Candidates(profiles, "active", now);
        Assert.DoesNotContain(candidates, profile => profile.Id is "active" or "fresh" or "unsupported");
        Assert.Equal(new[] { "failed", "future", "old", "tcp" }, candidates.Select(profile => profile.Id).Order());
        profiles[0].LastTested = now.AddHours(-1);
        Assert.DoesNotContain(ConnectedDiscoveryPolicy.Candidates(profiles, "active", now), profile => profile.Id == "active");
    }

    [Fact]
    public async Task DiscoveryTopsUpFiveExistingSuccessesToTenAndLeavesTheActiveProfileUntouched()
    {
        var now = DateTime.Now;
        var ready = Enumerable.Range(0, 5).Select(i => Profile($"ready-{i}", ProfileHealth.Working, now)).ToList();
        var profiles = ready.Concat(Enumerable.Range(0, 30).Select(i => Profile($"new-{i}", ProfileHealth.Untested, null))).ToList();
        var count = ConnectedDiscoveryPolicy.CountReady(profiles, now);
        var candidates = ConnectedDiscoveryPolicy.Candidates(profiles, ready[0].Id, now);
        var calls = 0;
        var remaining = await ConcurrentTestRunner.RunAsync(candidates, 1, (profile, ct) =>
        {
            calls++;
            profile.Health = ProfileHealth.Working;
            profile.LastTested = now;
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        }, () => count >= ConnectedDiscoveryPolicy.HealthyTarget, CancellationToken.None);
        Assert.Equal(5, calls);
        Assert.Equal(10, count);
        Assert.Equal(25, remaining.Count);
        Assert.Equal(now, ready[0].LastTested);
        Assert.Equal(10, SavedServerPolicy.ForHome(profiles, ready[0].Id, 10).Count);
    }

    [Fact]
    public void DuplicateProfilesCannotInflateReadyCountOrCandidateQueue()
    {
        var now = DateTime.Now;
        var success = Profile("same", ProfileHealth.Working, now);
        var untested = Profile("other", ProfileHealth.Untested, null);
        Assert.Equal(1, ConnectedDiscoveryPolicy.CountReady(new[] { success, success }, now));
        Assert.Single(ConnectedDiscoveryPolicy.Candidates(new[] { untested, untested }, "active", now));
    }

    [Fact]
    public async Task PhysicalNetworkFailureEscapesPrecheckInsteadOfMarkingServerDead()
    {
        var connector = new FakeConnector((_, _, _) => throw new TunnelStartupException("Physical network lost."));
        var precheck = new EndpointPrecheckService(connector);
        var server = Profile("saved", ProfileHealth.Working, DateTime.Now);
        await Assert.ThrowsAsync<TunnelStartupException>(() => precheck.TestAsync(server, TimeSpan.FromSeconds(1)));
        Assert.Equal(ProfileHealth.Working, server.Health);
        Assert.Equal(0, server.FailureCount);
    }

    [Fact]
    public async Task PrecheckUsesInjectedTransportAndClosesItsConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync();
        string? requestedHost = null;
        var connector = new FakeConnector(async (host, requestedPort, ct) =>
        {
            requestedHost = host;
            Assert.Equal(port, requestedPort);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        });
        var profile = Profile("saved", ProfileHealth.Untested, null);
        profile.Address = "physical-network.example";
        profile.Port = port;
        var result = await new EndpointPrecheckService(connector).TestAsync(profile, TimeSpan.FromSeconds(2));
        using var remote = await accepted.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Success);
        Assert.Equal("physical-network.example", requestedHost);
        Assert.Equal(0, await remote.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private sealed class FakeConnector(Func<string, int, CancellationToken, ValueTask<Stream>> connect) : IEndpointConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) => connect(host, port, cancellationToken);
    }

    private static ConfigProfile Profile(string id, ProfileHealth health, DateTime? tested) => new()
    {
        Id = id, Remark = id, Address = "127.0.0.1", Port = 12345,
        Health = health, LastTested = tested
    };
}
