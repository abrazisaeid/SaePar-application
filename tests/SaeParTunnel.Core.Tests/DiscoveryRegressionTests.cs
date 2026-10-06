using System.Net;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class DiscoveryRegressionTests
{
    [Theory]
    [InlineData("vless://user@8.8.8.8:80?security=none", true)]
    [InlineData("vless://user@example.com:80?security=none", true)]
    [InlineData("trojan://secret@example.com:80?security=none", true)]
    [InlineData("vless://user@192.168.1.1:80?security=none", false)]
    [InlineData("vless://user@router.local:80?security=none", false)]
    [InlineData("vless://user@example.com:443?security=tls", false)]
    [InlineData("vless://user@example.com:443?security=tls&allowInsecure=1", true)]
    [InlineData("vless://user@example.com:443?security=tls&type=garbage", true)]
    public void RemovedEngineFeaturesAreClassifiedBeforeNetworkTests(string uri, bool unsupported)
    {
        var profile = new ConfigParser().Parse(uri, "test", out _)!;
        Assert.Equal(unsupported, profile.Health == ProfileHealth.Unsupported);
    }

    [Theory]
    [InlineData("serviceName", "a", "b")]
    [InlineData("fp", "chrome", "firefox")]
    [InlineData("mode", "gun", "multi")]
    [InlineData("alpn", "h2", "http%2F1.1")]
    [InlineData("headerType", "none", "http")]
    public void ConnectionParametersMustNotBeDeduplicated(string key, string first, string second)
    {
        var parser = new ConfigParser();
        var prefix = "vless://user@example.com:443?type=grpc&" + key + "=";
        var a = parser.Parse(prefix + first, "test", out _)!;
        var b = parser.Parse(prefix + second, "test", out _)!;
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(a.Id, parser.Parse(prefix + first + "#new-name", "other", out _)!.Id);
    }

    [Fact]
    public void SearchPrioritizesFreshUntestedProfilesOverFailedArchive()
    {
        var failed = new ConfigProfile { Health = ProfileHealth.Failed };
        var fresh = new ConfigProfile { LastSeen = DateTime.Today };
        var old = new ConfigProfile { LastSeen = DateTime.Today.AddDays(-3) };
        Assert.Equal(new[] { fresh, old, failed }, ConfigTestPlanner.OrderForHealthySearch(new[] { failed, old, fresh }));
    }

    [Fact]
    public void SearchTriesDifferentEndpointsBeforeRepeatingOneServer()
    {
        var first = new ConfigProfile { Address = "same.example", Port = 443 };
        var variant = new ConfigProfile { Address = "SAME.example", Port = 443, Network = "websocket" };
        var other = new ConfigProfile { Address = "other.example", Port = 443 };
        var result = ConfigTestPlanner.OrderForHealthySearch(new[] { first, variant, other });
        Assert.Equal(new[] { first, other, variant }, result);
    }

    [Fact]
    public void ReachableEndpointIsTriedBeforeAnUnknownEndpoint()
    {
        var unknown = new ConfigProfile { Address = "unknown.example", Port = 443 };
        var reachable = new ConfigProfile { Address = "reachable.example", Port = 443, Health = ProfileHealth.Reachable };
        Assert.Equal(new[] { reachable, unknown }, ConfigTestPlanner.OrderForHealthySearch(new[] { unknown, reachable }));
    }

    [Fact]
    public void RecentLocalSuccessPrioritizesMatchingConnectionMethods()
    {
        var unknown = new ConfigProfile { Address = "unknown.example", Port = 443, Network = "grpc" };
        var matching = new ConfigProfile { Address = "matching.example", Port = 443, Network = "websocket" };
        var working = new ConfigProfile
        {
            Address = "working.example", Port = 443, Network = "websocket",
            Health = ProfileHealth.Working, LastTested = DateTime.Now.AddMinutes(-5)
        };
        Assert.Equal(new[] { working, matching, unknown }, ConfigTestPlanner.OrderForHealthySearch(new[] { unknown, matching, working }));
        working.LastTested = DateTime.Now.AddDays(-3);
        Assert.Equal(new[] { working, unknown, matching }, ConfigTestPlanner.OrderForHealthySearch(new[] { unknown, matching, working }));
    }

    [Fact]
    public async Task EndpointCancellationIsNotRecordedAsServerFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EndpointPrecheckService()
            .TestAsync(new ConfigProfile { Address = "127.0.0.1", Port = 1 }, TimeSpan.FromSeconds(1), cts.Token));
    }

    [Fact]
    public async Task AndroidDirectDiscoveryUsesOneDownloadWithoutAProxyAttempt()
    {
        var proxyCalls = 0;
        var directCalls = 0;
        using var service = new GitHubConfigService(new Handler(_ =>
        {
            Interlocked.Increment(ref proxyCalls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        }), new Handler(_ =>
        {
            Interlocked.Increment(ref directCalls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("vless://user@example.com:443")
            });
        }), directOnly: true);
        var result = await service.FetchAsync("https://example.com/sub", null);
        Assert.True(result.UsedDirectConnection);
        Assert.Equal(0, proxyCalls);
        Assert.Equal(1, directCalls);
    }

    [Fact]
    public async Task DirectRouteDoesNotWaitForStaleProxy()
    {
        using var service = new GitHubConfigService(new Handler(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }), new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("vless://user@example.com:443")
        })), TimeSpan.FromSeconds(3));
        var result = await service.FetchAsync("https://example.com/sub", null).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.UsedDirectConnection);
    }

    [Fact]
    public async Task StalledBodyHasDeadlineAfterHeaders()
    {
        static Task<HttpResponseMessage> Stalled(CancellationToken _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent() });
        using var service = new GitHubConfigService(new Handler(Stalled), new Handler(Stalled), TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.FetchAsync("https://example.com/sub", null)
            .WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task HtmlResponseDoesNotHideUsableAlternateRoute()
    {
        using var service = new GitHubConfigService(
            new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("<html>Access denied</html>") })),
            new Handler(async ct =>
            {
                await Task.Delay(50, ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("vless://user@example.com:443?security=tls") };
            }));
        var result = await service.FetchAsync("https://example.com/sub", null);
        Assert.True(result.UsedDirectConnection);
        Assert.Single(result.Profiles);
    }

    [Fact]
    public async Task FetchPreservesCallerCancellation()
    {
        static async Task<HttpResponseMessage> Wait(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }
        using var service = new GitHubConfigService(new Handler(Wait), new Handler(Wait));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FetchAsync("https://example.com/sub", null, cts.Token));
    }

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(ct);
    }

    private sealed class StalledContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("A body cancellation token is required.");
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) =>
            Task.Delay(Timeout.Infinite, ct);
    }
}
