using System.Text.Json;
using Microsoft.Maui.Storage;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.App.Services;

public sealed class MauiJsonStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string RootPath
    {
        get
        {
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
        return await Task.Run(() =>
        {
            var parser = new ConfigParser();
            foreach (var profile in profiles)
            {
                profile.Id = ConfigParser.ComputeId(profile);
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
            }
            return profiles.DistinctBy(p => p.Id).ToList();
        });
    }
    public Task SaveProfilesAsync(IEnumerable<ConfigProfile> profiles) => WriteAsync(ProfilesPath, profiles.ToList());

    private async Task<T?> ReadAsync<T>(string path)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return default;
            await using var stream = File.OpenRead(path);
            return await Task.Run(async () => await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions)).ConfigureAwait(false);
        }
        catch { return default; }
        finally { _gate.Release(); }
    }

    private async Task WriteAsync<T>(string path, T value)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
                await Task.Run(async () => await JsonSerializer.SerializeAsync(stream, value, JsonOptions)).ConfigureAwait(false);
            File.Move(temp, path, true);
        }
        finally { _gate.Release(); }
    }
}
