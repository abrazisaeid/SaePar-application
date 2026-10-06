using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class TrafficStatisticsTests
{
    [Fact]
    public void CountsOnlyProxyTrafficAndKeepsLargeCountersExact()
    {
        var sample = XrayTrafficClient.Parse("""
            {"stats":{"outbound":{"proxy":{"uplink":9007199254740993,"downlink":12345},
              "direct":{"uplink":900000,"downlink":800000},"saepar-metrics":{"uplink":70,"downlink":100}}}}
            """);
        Assert.Equal(9007199254740993, sample.SentBytes);
        Assert.Equal(12345, sample.ReceivedBytes);
        Assert.Equal(new TrafficCounters(0, 0), XrayTrafficClient.Parse("""{"stats":{"outbound":{}}}"""));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"stats\":{\"outbound\":[]}}")]
    [InlineData("{\"stats\":{\"outbound\":{\"proxy\":{\"uplink\":-1,\"downlink\":2}}}}")]
    [InlineData("{\"stats\":{\"outbound\":{\"proxy\":{\"uplink\":\"12\",\"downlink\":2}}}}")]
    [InlineData("{\"stats\":{\"outbound\":{\"proxy\":{\"uplink\":12}}}}")]
    public void InvalidStatsAreUnavailableInsteadOfFabricatedZero(string json)
        => Assert.Throws<JsonException>(() => XrayTrafficClient.Parse(json));

    [Fact]
    public void RatesUseActualIntervalsHandleIdleGapsAndCounterResets()
    {
        var tracker = new TrafficRateTracker();
        Assert.Equal(0, tracker.Update(new(1000, 2000), TimeSpan.FromSeconds(1)).SendBytesPerSecond);
        var sample = tracker.Update(new(2024, 6096), TimeSpan.FromSeconds(3));
        Assert.Equal(512, sample.SendBytesPerSecond);
        Assert.Equal(2048, sample.ReceiveBytesPerSecond);
        Assert.Equal(0, tracker.Update(new(2024, 6096), TimeSpan.FromSeconds(4)).ReceiveBytesPerSecond);
        // A failed metrics request doesn't reset the tracker: the next success
        // averages the actual interval rather than claiming a one-second spike.
        Assert.Equal(100, tracker.Update(new(2324, 6996), TimeSpan.FromSeconds(7)).SendBytesPerSecond);
        sample = tracker.Update(new(5, 10), TimeSpan.FromSeconds(8));
        Assert.Equal(0, sample.SendBytesPerSecond);
        Assert.Equal(0, sample.ReceiveBytesPerSecond);
        Assert.Equal(20, tracker.Update(new(25, 50), TimeSpan.FromSeconds(9)).SendBytesPerSecond);
        Assert.Equal(0, tracker.Update(new(25, 50), TimeSpan.FromSeconds(9)).SendBytesPerSecond);
    }

    [Fact]
    public void EnablingMetricsPreservesRoutesAndOtherPolicies()
    {
        using var doc = JsonDocument.Parse(XrayTrafficClient.EnableMetrics("""
            {"inbounds":[],"outbounds":[{"tag":"proxy"}],"routing":{"rules":[{"outboundTag":"direct"}]},
             "policy":{"levels":{"0":{"connIdle":60}},"system":{"other":true}},"env":{"xray.tun.fd":"42"}}
            """, 19383));
        var root = doc.RootElement;
        Assert.Equal("127.0.0.1:19383", root.GetProperty("metrics").GetProperty("listen").GetString());
        Assert.True(root.GetProperty("policy").GetProperty("system").GetProperty("statsOutboundUplink").GetBoolean());
        Assert.Equal(60, root.GetProperty("policy").GetProperty("levels").GetProperty("0").GetProperty("connIdle").GetInt32());
        Assert.Equal("direct", root.GetProperty("routing").GetProperty("rules")[0].GetProperty("outboundTag").GetString());
        Assert.Equal("42", root.GetProperty("env").GetProperty("xray.tun.fd").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(65536)]
    public void MetricsCannotUseInvalidOrPrivilegedPorts(int port)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new XrayTrafficClient(port));

    [Fact]
    public async Task ReadsRealLoopbackHttpMetricsAndCancelsAStalledResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new XrayTrafficClient(port);
        var responseTask = client.ReadAsync(CancellationToken.None);
        using (var peer = await listener.AcceptTcpClientAsync())
        {
            var stream = peer.GetStream();
            var buffer = new byte[1024];
            var length = await stream.ReadAsync(buffer);
            Assert.Contains("GET /debug/vars HTTP/1.1", Encoding.ASCII.GetString(buffer, 0, length));
            const string body = "{\"stats\":{\"outbound\":{\"proxy\":{\"uplink\":123,\"downlink\":456}}}}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
            Assert.Equal(new TrafficCounters(123, 456), await responseTask.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        using var cts = new CancellationTokenSource();
        var stalled = client.ReadAsync(cts.Token);
        using var stalledPeer = await listener.AcceptTcpClientAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stalled.WaitAsync(TimeSpan.FromSeconds(1)));
    }
}
