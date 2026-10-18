using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class SubscriptionCatalogTests
{
    [Fact]
    public void NormalizeMigratesLegacyCustomSourceAndKeepsBuiltInSource()
    {
        var fetchedAt = new DateTime(2026, 8, 10, 12, 30, 0, DateTimeKind.Utc);
        var settings = new AppSettings
        {
            Subscriptions = new List<SubscriptionSource>(),
            SubscriptionCatalogVersion = SubscriptionCatalog.CurrentVersion,
            GitHubSubscriptionUrl = "https://sub.example.com/client/abc",
            GitHubETag = "\"legacy-etag\"",
            LastGitHubFetchUtc = fetchedAt
        };

        var changed = SubscriptionCatalog.Normalize(settings);

        Assert.True(changed);
        Assert.Equal(2, settings.Subscriptions.Count);
        Assert.True(settings.Subscriptions[0].IsBuiltIn);
        var migrated = Assert.Single(settings.Subscriptions, x => !x.IsBuiltIn);
        Assert.Equal("اشتراک قبلی", migrated.Name);
        Assert.Equal("https://sub.example.com/client/abc", migrated.Url);
        Assert.Equal("\"legacy-etag\"", migrated.ETag);
        Assert.Equal(fetchedAt, migrated.LastFetchedUtc);
    }

    [Fact]
    public void NormalizeDeduplicatesEquivalentUrlsAndKeepsNewestMetadata()
    {
        var older = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = older.AddDays(2);
        var settings = new AppSettings
        {
            SubscriptionCatalogVersion = SubscriptionCatalog.CurrentVersion,
            Subscriptions = new List<SubscriptionSource>
            {
                new() { Name = "اول", Url = "https://SUB.example.com/path", LastFetchedUtc = older },
                new() { Name = "دوم", Url = "https://sub.example.com/path", ETag = "\"etag\"", LastFetchedUtc = newer }
            }
        };

        SubscriptionCatalog.Normalize(settings);

        var custom = Assert.Single(settings.Subscriptions, x => !x.IsBuiltIn);
        Assert.Equal("اول", custom.Name);
        Assert.Equal("\"etag\"", custom.ETag);
        Assert.Equal(newer, custom.LastFetchedUtc);
    }

    [Theory]
    [InlineData("http://sub.example.com/list")]
    [InlineData("sub.example.com/list")]
    [InlineData("")]
    public void TryNormalizeHttpsUrlRejectsUnsafeOrRelativeUrls(string value)
    {
        Assert.False(SubscriptionCatalog.TryNormalizeHttpsUrl(value, out _));
    }

    [Fact]
    public void NewSourcesAreSeededOnceAndCanBeDisabledOrRemoved()
    {
        var settings = new AppSettings();
        Assert.True(SubscriptionCatalog.Normalize(settings));
        Assert.Equal(3, settings.Subscriptions.Count);
        Assert.All(settings.Subscriptions, source => Assert.True(source.IsEnabled));
        var radikal = settings.Subscriptions.Single(source => source.Id == "public-radikal-verified");
        Assert.False(radikal.IsBuiltIn);
        radikal.IsEnabled = false;
        settings.Subscriptions.RemoveAll(source => source.Id == "public-nomorewalls");
        Assert.False(SubscriptionCatalog.Normalize(settings));
        Assert.Equal(2, settings.Subscriptions.Count);
        Assert.False(settings.Subscriptions.Single(source => source.Id == radikal.Id).IsEnabled);
        Assert.DoesNotContain(settings.Subscriptions, source => source.Id == "public-nomorewalls");
    }

    [Fact]
    public void SourceUpgradePreservesExistingUrlStateAndOnlyAddsMissingSources()
    {
        var existing = SubscriptionCatalog.CreateAdditionalDefaults()[0];
        existing.Id = "custom-radikal";
        existing.Name = "My source";
        existing.IsEnabled = false;
        existing.ETag = "existing-validator";
        var settings = new AppSettings { Subscriptions = [existing] };
        Assert.True(SubscriptionCatalog.Normalize(settings));
        Assert.Equal(3, settings.Subscriptions.Count);
        var migrated = Assert.Single(settings.Subscriptions, source => SubscriptionCatalog.UrlsEqual(source.Url, existing.Url));
        Assert.Equal(existing.Id, migrated.Id);
        Assert.Equal(existing.Name, migrated.Name);
        Assert.False(migrated.IsEnabled);
        Assert.Equal(existing.ETag, migrated.ETag);
        Assert.False(SubscriptionCatalog.Normalize(settings));
    }
}
