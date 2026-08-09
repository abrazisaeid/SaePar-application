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
        settings.WhitelistApplications ??= new List<WhitelistApplication>();
        settings.WhitelistWebsites ??= new List<string>();
        if (string.IsNullOrWhiteSpace(settings.GitHubSubscriptionUrl))
            settings.GitHubSubscriptionUrl = GitHubConfigService.DefaultSubscriptionUrl;
#if WINDOWS
        if (string.IsNullOrWhiteSpace(settings.XrayPath))
            settings.XrayPath = Path.Combine(RuntimePath, "xray.exe");
#endif
        return settings;
    }

    public Task SaveSettingsAsync(AppSettings settings) => WriteAsync(SettingsPath, settings);
    public async Task<List<ConfigProfile>> LoadProfilesAsync() => await ReadAsync<List<ConfigProfile>>(ProfilesPath) ?? new();
    public Task SaveProfilesAsync(IEnumerable<ConfigProfile> profiles) => WriteAsync(ProfilesPath, profiles.ToList());

    private async Task<T?> ReadAsync<T>(string path)
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(path)) return default;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
        }
        catch { return default; }
        finally { _gate.Release(); }
    }

    private async Task WriteAsync<T>(string path, T value)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
            File.Move(temp, path, true);
        }
        finally { _gate.Release(); }
    }
}
