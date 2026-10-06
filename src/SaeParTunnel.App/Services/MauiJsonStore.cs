using Microsoft.Maui.Storage;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.App.Services;

public sealed class MauiJsonStore
{
    private readonly JsonCacheStore _files = new();
    private readonly string? _rootPath;
    public MauiJsonStore() { }
    internal MauiJsonStore(string rootPath) { _rootPath = rootPath; }
    public string LastStorageError { get; private set; } = string.Empty;

    public string RootPath
    {
        get
        {
            if (_rootPath is not null) return _rootPath;
#if WINDOWS
            // Reuse v1.x data automatically on Windows.
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SaeParTunnel");
#else
            return Path.Combine(FileSystem.Current.AppDataDirectory, "SaeParTunnel");
#endif
        }
    }

    public string RuntimePath => Path.Combine(RootPath, "Runtime");
    private string SettingsPath => Path.Combine(RootPath, "settings.json");
    private string ProfilesPath => Path.Combine(RootPath, "profiles.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(RuntimePath);
    }

    public async Task<AppSettings> LoadSettingsAsync()
    {
        EnsureCreated();
        var settings = await ReadAsync<AppSettings>(SettingsPath) ?? new AppSettings();
        var refreshValidators = settings.DataSchemaVersion < 27;
        var shouldSave = settings.DataSchemaVersion < new AppSettings().DataSchemaVersion;
        // Preserve an explicitly selected legacy whitelist mode on upgrades.
        if (settings.DataSchemaVersion < 28 && settings.EnableWhitelistRouting) settings.EnableIranBypass = false;
        settings.DataSchemaVersion = Math.Max(settings.DataSchemaVersion, new AppSettings().DataSchemaVersion);
        settings.WhitelistApplications ??= new List<WhitelistApplication>();
        settings.WhitelistWebsites ??= new List<string>();
        settings.DirectRoutingEntries ??= new List<string>();
        settings.SuppressedProfileIds ??= new Dictionary<string, DateTime>();
        shouldSave |= SubscriptionCatalog.Normalize(settings);
        if (refreshValidators)
        {
            // Re-fetch entries previously lost to incomplete profile identity,
            // and discard validators saved before successful body validation.
            foreach (var source in settings.Subscriptions) source.ETag = string.Empty;
            settings.GitHubETag = string.Empty;
            settings.CommunityHealthETag = string.Empty;
        }
        settings.CommunityHealthIndexUrl ??= string.Empty;
        settings.CommunityHealthETag ??= string.Empty;
#if WINDOWS
        if (string.IsNullOrWhiteSpace(settings.XrayPath))
        {
            settings.XrayPath = Path.Combine(RuntimePath, "xray.exe");
            shouldSave = true;
        }
#endif
        if (shouldSave)
            await SaveSettingsAsync(settings);
        return settings;
    }

    public Task SaveSettingsAsync(AppSettings settings) => WriteAsync(SettingsPath, settings);
    public async Task<List<ConfigProfile>> LoadProfilesAsync()
    {
        var profiles = await ReadAsync<List<ConfigProfile>>(ProfilesPath) ?? new();
        var previous = new List<ConfigProfile>();
        if (profiles.Any(p => p.Health == ProfileHealth.Failed && !p.LastSuccessfulTest.HasValue) &&
            File.Exists(ProfilesPath + ".bak"))
        {
            // Upgrade recovery only: recover success history for existing IDs,
            // never bring back entries removed by the cleanup policy.
            try { previous = await Task.Run(() => _files.ReadAsync<List<ConfigProfile>>(ProfilesPath + ".bak")) ?? new(); }
            catch (IOException) { /* An optional old backup must not block a valid primary. */ }
        }
        return await Task.Run(() =>
        {
            var parser = new ConfigParser();
            var history = previous.GroupBy(ConfigParser.ComputeId).ToDictionary(g => g.Key,
                g => g.OrderByDescending(p => p.Health == ProfileHealth.Working)
                    .ThenByDescending(p => p.LastSuccessfulTest ?? p.LastTested).First());
            foreach (var profile in profiles)
            {
                profile.Id = ConfigParser.ComputeId(profile);
                if (!profile.LastSuccessfulTest.HasValue) SavedServerPolicy.RememberSuccess(profile);
                // Older caches predate the engine's removed-feature checks.
                var parsed = string.IsNullOrWhiteSpace(profile.OriginalUri)
                    ? null : parser.Parse(profile.OriginalUri, profile.Source, out _);
                if (parsed?.Health == ProfileHealth.Unsupported)
                {
                    profile.Health = ProfileHealth.Unsupported;
                    profile.TestMessage = parsed.TestMessage;
                }
                if (profile.Health == ProfileHealth.Testing)
                    profile.Health = ProfileHealth.Untested;
                if (history.TryGetValue(profile.Id, out var prior)) SavedServerPolicy.RecoverHistory(profile, prior);
            }
            return profiles.OrderByDescending(p => p.Health == ProfileHealth.Working)
                .ThenByDescending(p => p.LastSuccessfulTest).DistinctBy(p => p.Id).ToList();
        });
    }
    public Task SaveProfilesAsync(IEnumerable<ConfigProfile> profiles) => WriteAsync(ProfilesPath, profiles.ToList());

    private async Task<T?> ReadAsync<T>(string path)
    {
        try
        {
            return await Task.Run(() => _files.ReadAsync<T>(path)).ConfigureAwait(false);
        }
        catch
        {
            LastStorageError = $"خواندن {Path.GetFileName(path)} انجام نشد؛ فایل ذخیره‌شده حفظ شد.";
            throw;
        }
    }

    private async Task WriteAsync<T>(string path, T value)
    {
        try
        {
            await Task.Run(() => _files.WriteAsync(path, value)).ConfigureAwait(false);
        }
        catch
        {
            LastStorageError = $"ذخیرهٔ {Path.GetFileName(path)} انجام نشد؛ نتیجهٔ قبلی حفظ شد.";
            throw;
        }
    }
}
