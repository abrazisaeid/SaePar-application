using SaeParTunnel.Core.Models;

namespace SaeParTunnel.Core.Services;

public static class SubscriptionCatalog
{
    public static bool Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var original = settings.Subscriptions ?? new List<SubscriptionSource>();
        var candidates = original.Where(x => x is not null).ToList();

        if (!string.IsNullOrWhiteSpace(settings.GitHubSubscriptionUrl))
        {
            candidates.Add(new SubscriptionSource
            {
                Name = UrlsEqual(settings.GitHubSubscriptionUrl, SubscriptionSource.BuiltInUrl)
                    ? SubscriptionSource.BuiltInName
                    : "اشتراک قبلی",
                Url = settings.GitHubSubscriptionUrl,
                ETag = settings.GitHubETag ?? string.Empty,
                LastFetchedUtc = settings.LastGitHubFetchUtc,
                IsBuiltIn = UrlsEqual(settings.GitHubSubscriptionUrl, SubscriptionSource.BuiltInUrl)
            });
        }

        candidates.Add(SubscriptionSource.CreateBuiltIn());

        var normalized = new List<SubscriptionSource>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var url = (candidate.Url ?? string.Empty).Trim();
            if (url.Length == 0)
                continue;

            var existing = normalized.FirstOrDefault(x => UrlsEqual(x.Url, url));
            if (existing is not null)
            {
                if (string.IsNullOrWhiteSpace(existing.ETag) && !string.IsNullOrWhiteSpace(candidate.ETag))
                    existing.ETag = candidate.ETag.Trim();
                if (candidate.LastFetchedUtc > existing.LastFetchedUtc)
                    existing.LastFetchedUtc = candidate.LastFetchedUtc;
                continue;
            }

            var isBuiltIn = UrlsEqual(url, SubscriptionSource.BuiltInUrl);
            var id = isBuiltIn ? SubscriptionSource.BuiltInId : (candidate.Id ?? string.Empty).Trim();
            if (!isBuiltIn &&
                (id.Length == 0 || id.Equals(SubscriptionSource.BuiltInId, StringComparison.OrdinalIgnoreCase) || !ids.Add(id)))
            {
                do { id = Guid.NewGuid().ToString("N"); }
                while (!ids.Add(id));
            }

            if (isBuiltIn)
            {
                id = SubscriptionSource.BuiltInId;
                ids.Add(id);
            }

            normalized.Add(new SubscriptionSource
            {
                Id = id,
                Name = isBuiltIn ? SubscriptionSource.BuiltInName : NormalizeName(candidate.Name, url),
                Url = isBuiltIn ? SubscriptionSource.BuiltInUrl : url,
                ETag = (candidate.ETag ?? string.Empty).Trim(),
                LastFetchedUtc = candidate.LastFetchedUtc,
                IsEnabled = candidate.IsEnabled,
                IsBuiltIn = isBuiltIn
            });
        }

        var builtIn = normalized.First(x => x.IsBuiltIn);
        normalized.Remove(builtIn);
        normalized.Insert(0, builtIn);

        var changed = !AreEquivalent(original, normalized);
        settings.Subscriptions = normalized;

        if (!string.Equals(settings.GitHubSubscriptionUrl, builtIn.Url, StringComparison.Ordinal) ||
            !string.Equals(settings.GitHubETag, builtIn.ETag, StringComparison.Ordinal) ||
            settings.LastGitHubFetchUtc != builtIn.LastFetchedUtc)
        {
            changed = true;
            settings.GitHubSubscriptionUrl = builtIn.Url;
            settings.GitHubETag = builtIn.ETag;
            settings.LastGitHubFetchUtc = builtIn.LastFetchedUtc;
        }

        return changed;
    }

    public static bool TryNormalizeHttpsUrl(string? value, out string normalizedUrl)
    {
        normalizedUrl = (value ?? string.Empty).Trim();
        return Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) &&
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(uri.Host);
    }

    public static bool UrlsEqual(string? left, string? right)
    {
        var leftText = (left ?? string.Empty).Trim();
        var rightText = (right ?? string.Empty).Trim();
        if (!Uri.TryCreate(leftText, UriKind.Absolute, out var leftUri) ||
            !Uri.TryCreate(rightText, UriKind.Absolute, out var rightUri))
        {
            return leftText.Equals(rightText, StringComparison.Ordinal);
        }

        return leftUri.Scheme.Equals(rightUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
               leftUri.IdnHost.Equals(rightUri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
               leftUri.Port == rightUri.Port &&
               leftUri.UserInfo.Equals(rightUri.UserInfo, StringComparison.Ordinal) &&
               leftUri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped)
                   .Equals(rightUri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped), StringComparison.Ordinal);
    }

    private static string NormalizeName(string? name, string url)
    {
        var value = (name ?? string.Empty).Trim();
        if (value.Length > 0)
            return value;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host
            : "اشتراک شخصی";
    }

    private static bool AreEquivalent(IReadOnlyList<SubscriptionSource> left, IReadOnlyList<SubscriptionSource> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal) ||
                !string.Equals(a.Name, b.Name, StringComparison.Ordinal) ||
                !string.Equals(a.Url, b.Url, StringComparison.Ordinal) ||
                !string.Equals(a.ETag, b.ETag, StringComparison.Ordinal) ||
                a.LastFetchedUtc != b.LastFetchedUtc ||
                a.IsEnabled != b.IsEnabled ||
                a.IsBuiltIn != b.IsBuiltIn)
            {
                return false;
            }
        }

        return true;
    }
}
