using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SaeParTunnel.Core.Services;

public sealed record TrafficCounters(long SentBytes, long ReceivedBytes);
public sealed record TrafficSnapshot(long SentBytes, long ReceivedBytes, double SendBytesPerSecond, double ReceiveBytesPerSecond);

/// <summary>Reads only the selected proxy's counters from Xray's loopback metrics listener.</summary>
public sealed class XrayTrafficClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _endpoint;

    public XrayTrafficClient(int port)
    {
        ValidatePort(port);
        _endpoint = new Uri($"http://127.0.0.1:{port}/debug/vars");
        // Use managed sockets for loopback HTTP, independently of Android's
        // platform HTTP handler. No proxy, redirects, or external host is used.
        _client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(2),
            MaxResponseContentBufferSize = 256 * 1024
        };
    }

    public async Task<TrafficCounters> ReadAsync(CancellationToken cancellationToken)
        => Parse(await _client.GetStringAsync(_endpoint, cancellationToken).ConfigureAwait(false));

    public static string EnableMetrics(string config, int port)
    {
        ValidatePort(port);
        var root = JsonNode.Parse(config) as JsonObject ?? throw new JsonException("Expected Xray configuration object.");
        root["stats"] = new JsonObject();
        var policy = root["policy"] as JsonObject;
        if (policy is null) root["policy"] = policy = new JsonObject();
        var system = policy["system"] as JsonObject;
        if (system is null) policy["system"] = system = new JsonObject();
        system["statsOutboundUplink"] = true;
        system["statsOutboundDownlink"] = true;
        root["metrics"] = new JsonObject { ["tag"] = "saepar-metrics", ["listen"] = $"127.0.0.1:{port}" };
        return root.ToJsonString();
    }

    public static TrafficCounters Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("stats", out var stats) ||
            stats.ValueKind != JsonValueKind.Object || !stats.TryGetProperty("outbound", out var outbound) ||
            outbound.ValueKind != JsonValueKind.Object)
            throw new JsonException("Missing Xray outbound statistics.");
        // Counters can be absent before the first connection uses the proxy.
        if (!outbound.TryGetProperty("proxy", out var proxy)) return new TrafficCounters(0, 0);
        if (proxy.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid proxy statistics.");
        return new TrafficCounters(ReadCounter(proxy, "uplink"), ReadCounter(proxy, "downlink"));
    }

    private static long ReadCounter(JsonElement proxy, string name)
    {
        if (!proxy.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var count) || count < 0)
            throw new JsonException("Invalid Xray traffic counter.");
        return count;
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
    }

    public static string FormatBytes(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes < 0) bytes = 0;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return bytes.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public void Dispose() => _client.Dispose();
}

public sealed class TrafficRateTracker
{
    private TrafficCounters? _previous;
    private TimeSpan _sampleTime;

    // elapsed is a monotonic stopwatch value, not wall-clock time.
    public TrafficSnapshot Update(TrafficCounters counters, TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(counters.SentBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(counters.ReceivedBytes);
        var seconds = (elapsed - _sampleTime).TotalSeconds;
        var send = _previous is not null && seconds > 0 && counters.SentBytes >= _previous.SentBytes
            ? (counters.SentBytes - _previous.SentBytes) / seconds : 0;
        var receive = _previous is not null && seconds > 0 && counters.ReceivedBytes >= _previous.ReceivedBytes
            ? (counters.ReceivedBytes - _previous.ReceivedBytes) / seconds : 0;
        if (_previous is null || elapsed > _sampleTime)
        {
            _previous = counters;
            _sampleTime = elapsed;
        }
        return new TrafficSnapshot(counters.SentBytes, counters.ReceivedBytes, send, receive);
    }
}
