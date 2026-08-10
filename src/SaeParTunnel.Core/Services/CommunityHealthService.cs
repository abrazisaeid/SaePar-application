using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

public sealed class CommunityHealthService : IDisposable
{
    private const int MaxIndexBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _systemProxyClient;
    private readonly HttpClient _directClient;

    public CommunityHealthService()
    {
        _systemProxyClient = CreateClient(new HttpClientHandler { UseProxy = true });
        _directClient = CreateClient(new HttpClientHandler { UseProxy = false });
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SaeParTunnel/2.0-community-health");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public async Task<CommunityHealthFetchResult> FetchAsync(
        string indexUrl,
        string? previousETag,
        CancellationToken cancellationToken = default)
    {
        var requestedUrl = (indexUrl ?? string.Empty).Trim();
        ValidateHttps(requestedUrl);

        var errors = new List<string>();
        foreach (var mode in new[] { (Client: _systemProxyClient, Direct: false), (Client: _directClient, Direct: true) })
        {
            try
            {
                return await FetchOneAsync(mode.Client, requestedUrl, previousETag, mode.Direct, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                errors.Add($"{(mode.Direct ? "direct" : "system proxy")}: {FlattenException(ex)}");
            }
        }

        var detail = string.Join("\n", errors.TakeLast(4));
        throw new HttpRequestException("Community health index could not be downloaded or parsed.\n" + detail);
    }

    private static async Task<CommunityHealthFetchResult> FetchOneAsync(
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
            return new CommunityHealthFetchResult
            {
                NotModified = true,
                ETag = previousETag ?? string.Empty,
                LastModified = response.Content.Headers.LastModified,
                SourceUrl = url,
                UsedDirectConnection = direct
            };
        }

        response.EnsureSuccessStatusCode();
        var content = await ReadLimitedStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
            throw new HttpRequestException("Community health index returned an empty response.");

        var index = JsonSerializer.Deserialize<CommunityHealthIndex>(content, JsonOptions)
            ?? throw new JsonException("Community health index JSON is empty.");
        index.Profiles ??= new List<CommunityServerHealth>();

        return new CommunityHealthFetchResult
        {
            NotModified = false,
            Index = index,
            ETag = response.Headers.ETag?.ToString() ?? string.Empty,
            LastModified = response.Content.Headers.LastModified,
            SourceUrl = url,
            UsedDirectConnection = direct
        };
    }

    private static async Task<string> ReadLimitedStringAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            total += read;
            if (total > MaxIndexBytes)
                throw new HttpRequestException("Community health index is too large.");

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void ValidateHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Community health index URL must be a valid HTTPS address.");
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
        return string.Join(" -> ", parts);
    }

    public void Dispose()
    {
        _systemProxyClient.Dispose();
        _directClient.Dispose();
    }
}
