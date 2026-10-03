using System.Net;
using System.Net.Http.Headers;
using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

public sealed class GitHubConfigFetchResult
{
    public bool NotModified { get; set; }
    public string Content { get; set; } = string.Empty;
    public string ETag { get; set; } = string.Empty;
    public DateTimeOffset? LastModified { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public bool UsedDirectConnection { get; set; }
    public IReadOnlyList<ConfigProfile> Profiles { get; set; } = Array.Empty<ConfigProfile>();
}

public sealed class GitHubConfigService : IDisposable
{
    public const string RepositoryUrl = "https://github.com/Epodonios/v2ray-configs";
    public const string DefaultSubscriptionUrl = SubscriptionSource.BuiltInUrl;

    private static readonly string[] DefaultFallbackUrls =
    {
        DefaultSubscriptionUrl,
        "https://github.com/Epodonios/v2ray-configs/raw/refs/heads/main/All_Configs_Sub.txt",
        "https://cdn.jsdelivr.net/gh/Epodonios/v2ray-configs@main/All_Configs_Sub.txt"
    };

    private readonly HttpClient _systemProxyClient;
    private readonly HttpClient _directClient;
    private readonly TimeSpan _attemptTimeout;
    private const int MaxContentBytes = 32 * 1024 * 1024;

    public GitHubConfigService() : this(
        new HttpClientHandler { UseProxy = true },
        new HttpClientHandler { UseProxy = false })
    {
    }

    public GitHubConfigService(HttpMessageHandler systemHandler, HttpMessageHandler directHandler,
        TimeSpan? attemptTimeout = null)
    {
        _systemProxyClient = CreateClient(systemHandler);
        _directClient = CreateClient(directHandler);
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(15);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SaeParTunnel/2.0-preview12");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        return client;
    }

    public async Task<GitHubConfigFetchResult> FetchAsync(
        string subscriptionUrl,
        string? previousETag,
        CancellationToken cancellationToken = default)
    {
        var requestedUrl = string.IsNullOrWhiteSpace(subscriptionUrl)
            ? DefaultSubscriptionUrl
            : subscriptionUrl.Trim();

        ValidateHttps(requestedUrl);

        var candidates = BuildCandidates(requestedUrl);
        var errors = new List<string>();

        foreach (var candidate in candidates)
        {
            // A stale system proxy must not hold up a working direct route.
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(_attemptTimeout);
            var pending = new[] { (Client: _systemProxyClient, Direct: false), (Client: _directClient, Direct: true) }
                .Select(mode => FetchOneAsync(mode.Client, candidate,
                    string.Equals(candidate, requestedUrl, StringComparison.OrdinalIgnoreCase) ? previousETag : null,
                    mode.Direct, attemptCts.Token)).ToList();
            try
            {
                while (pending.Count > 0)
                {
                    var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(completed);
                    try { return await completed.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
                    {
                        errors.Add($"{new Uri(candidate).Host}: {FlattenException(ex)}");
                    }
                }
            }
            finally
            {
                attemptCts.Cancel();
                try { await Task.WhenAll(pending).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) { }
            }
        }

        var detail = string.Join("\n", errors.TakeLast(6));
        throw new HttpRequestException(
            "دریافت کانفیگ از منبع اصلی و مسیرهای جایگزین ناموفق بود.\n" + detail);
    }

    private static async Task<GitHubConfigFetchResult> FetchOneAsync(
        HttpClient client,
        string url,
        string? previousETag,
        bool direct,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(previousETag) &&
            EntityTagHeaderValue.TryParse(previousETag, out var etag))
        {
            request.Headers.IfNoneMatch.Add(etag);
        }

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new GitHubConfigFetchResult
            {
                NotModified = true,
                ETag = previousETag ?? string.Empty,
                LastModified = response.Content.Headers.LastModified,
                SourceUrl = url,
                UsedDirectConnection = direct
            };
        }

        response.EnsureSuccessStatusCode();
        // ResponseHeadersRead does not put a deadline on reading the body.
        // The attempt token covers both stages and the size limit bounds memory use.
        await response.Content.LoadIntoBufferAsync(MaxContentBytes, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
            throw new HttpRequestException("منبع پاسخ خالی برگرداند.");

        var profiles = await Task.Run(() => new ConfigExtractor(new ConfigParser()).Extract(content, string.Empty),
            cancellationToken).ConfigureAwait(false);
        if (profiles.Count == 0)
            throw new HttpRequestException("پاسخ منبع هیچ لینک کانفیگ معتبری ندارد؛ مسیر جایگزین بررسی می‌شود.");

        return new GitHubConfigFetchResult
        {
            NotModified = false,
            Content = content,
            ETag = response.Headers.ETag?.ToString() ?? string.Empty,
            LastModified = response.Content.Headers.LastModified,
            SourceUrl = url,
            UsedDirectConnection = direct,
            Profiles = profiles
        };
    }

    private static IReadOnlyList<string> BuildCandidates(string requestedUrl)
    {
        if (!string.Equals(requestedUrl, DefaultSubscriptionUrl, StringComparison.OrdinalIgnoreCase))
            return new[] { requestedUrl };

        return DefaultFallbackUrls.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ValidateHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("آدرس اشتراک معتبر نیست؛ فقط HTTPS مجاز است.");
        }
    }

    private static string FlattenException(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? current = ex; current is not null && parts.Count < 4; current = current.InnerException)
        {
            var message = (current.Message ?? string.Empty)
                .Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            if (message.Length > 0 && !parts.Contains(message, StringComparer.OrdinalIgnoreCase))
                parts.Add(message);
        }
        return string.Join(" → ", parts);
    }

    public void Dispose()
    {
        _systemProxyClient.Dispose();
        _directClient.Dispose();
    }
}
