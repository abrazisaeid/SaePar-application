using Microsoft.Maui.Storage;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.App.Services;

public sealed class MauiJsonStore
{
    private readonly JsonCacheStore _files = new();
    private readonly JsonCacheStore _homeFiles = new();
    private readonly JsonCacheStore _settingsFiles = new();
    private string? _selectedServerId;
    private readonly string? _rootPath;
    private SqliteProfileStore? _profiles;
    private bool _archiveReadFailed;
    private Dictionary<string, DateTime> _suppressedIds = new();
    private AppSettings? _loadedSettings;
    private SqliteProfileStore ProfileDatabase => _profiles ??= new SqliteProfileStore(Path.Combine(RootPath, "profiles.db"));
    public MauiJsonStore() { }
    internal MauiJsonStore(string rootPath) { _rootPath = rootPath; }
    public string LastStorageError { get; private set; } = string.Empty;
    public string RecoveryNotice { get; private set; } = string.Empty;

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
    private string HomeServersPath => Path.Combine(RootPath, "home-servers.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(RuntimePath);
    }

    public async Task<AppSettings> LoadSettingsAsync()
    {
        EnsureCreated();
        var settings = await ReadAsync<AppSettings>(SettingsPath) ?? new AppSettings();
        _selectedServerId = settings.SelectedServerId;
        var refreshValidators = settings.DataSchemaVersion < 27;
        var shouldSave = settings.DataSchemaVersion < new AppSettings().DataSchemaVersion;
        // Preserve an explicitly selected legacy whitelist mode on upgrades.
        if (settings.DataSchemaVersion < 28 && settings.EnableWhitelistRouting) settings.EnableIranBypass = false;
        settings.DataSchemaVersion = Math.Max(settings.DataSchemaVersion, new AppSettings().DataSchemaVersion);
        settings.WhitelistApplications ??= new List<WhitelistApplication>();
        settings.WhitelistWebsites ??= new List<string>();
        settings.DirectRoutingEntries ??= new List<string>();
        settings.SuppressedProfileIds ??= new Dictionary<string, DateTime>();
        _suppressedIds = settings.SuppressedProfileIds;
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
        _loadedSettings = settings;
        return settings;
    }

    public Task SaveSettingsAsync(AppSettings settings)
    {
        _selectedServerId = settings.SelectedServerId;
        _loadedSettings = settings;
        _suppressedIds = settings.SuppressedProfileIds;
        return WriteAsync(SettingsPath, settings);
    }

    public async Task<List<ConfigProfile>> LoadHomeServersAsync()
    {
        var snapshot = await ReadAsync<HomeServerSnapshot>(HomeServersPath);
        var profiles = snapshot?.Profiles ?? new();
        foreach (var profile in profiles)
        {
            ConfigParser.ValidateCachedProfile(profile);
            if (profile.Health == ProfileHealth.Testing) profile.Health = ProfileHealth.Untested;
            if (!profile.LastSuccessfulTest.HasValue) SavedServerPolicy.RememberSuccess(profile);
        }
        return SavedServerPolicy.ForHome(profiles, _selectedServerId, ConnectedDiscoveryPolicy.HealthyTarget).ToList();
    }

    public Task SaveHomeServersAsync(IEnumerable<ConfigProfile> profiles) => WriteAsync(HomeServersPath,
        new HomeServerSnapshot { Profiles = SavedServerPolicy.ForHome(profiles, _selectedServerId, ConnectedDiscoveryPolicy.HealthyTarget).ToList() });
    public async Task<List<ConfigProfile>> LoadProfilesAsync()
    {
        try
        {
            var profiles = await LoadProfilesCoreAsync();
            _archiveReadFailed = false;
            return profiles;
        }
        catch
        {
            _archiveReadFailed = true;
            LastStorageError = "خواندن آرشیو سرورها انجام نشد؛ داده‌های قبلی برای بازیابی حفظ شدند.";
            throw;
        }
    }

    private async Task<List<ConfigProfile>> LoadProfilesCoreAsync()
    {
        List<ConfigProfile>? stored;
        try { stored = await ProfileDatabase.LoadAsync(); }
        catch (Microsoft.Data.Sqlite.SqliteException) when (ProfileDatabase.IsCorrupt)
        {
            if (!await RecoverCorruptArchiveAsync()) throw;
            stored = await ProfileDatabase.LoadAsync();
        }
        var profiles = stored ?? await ReadAsync<List<ConfigProfile>>(ProfilesPath) ?? new();
        var previous = new List<ConfigProfile>();
        try { previous = await LoadHomeServersAsync(); }
        catch (IOException) { /* A damaged optional cache cannot block the archive. */ }
        if (stored is null && !File.Exists(HomeServersPath) && profiles.Any(p => p.Health == ProfileHealth.Failed && !p.LastSuccessfulTest.HasValue) &&
            File.Exists(ProfilesPath + ".bak"))
        {
            // Upgrade recovery only: recover success history for existing IDs,
            // never bring back entries removed by the cleanup policy.
            try { previous = await Task.Run(() => _files.ReadAsync<List<ConfigProfile>>(ProfilesPath + ".bak")) ?? new(); }
            catch (IOException) { /* An optional old backup must not block a valid primary. */ }
        }
        var migratedIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var restored = await Task.Run(() =>
        {
            var history = previous.GroupBy(ConfigParser.ComputeId).ToDictionary(g => g.Key,
                g => g.OrderByDescending(p => p.Health == ProfileHealth.Working)
                    .ThenByDescending(p => p.LastSuccessfulTest ?? p.LastTested).First());
            foreach (var profile in profiles)
            {
                if (!profile.LastSuccessfulTest.HasValue) SavedServerPolicy.RememberSuccess(profile);
                // Check stored fields without reparsing and rehashing every URI.
                ConfigParser.ValidateCachedProfile(profile);
                var normalizedId = ConfigParser.ComputeId(profile);
                if (profile.Id != normalizedId) migratedIds[profile.Id] = normalizedId;
                profile.Id = normalizedId;
                if (profile.Health == ProfileHealth.Testing)
                    profile.Health = ProfileHealth.Untested;
                if (history.TryGetValue(profile.Id, out var prior)) SavedServerPolicy.RecoverHistory(profile, prior);
            }
            return profiles.OrderByDescending(p => p.Health == ProfileHealth.Working)
                .ThenByDescending(p => p.LastSuccessfulTest).DistinctBy(p => p.Id).ToList();
        });
        // Commit every migrated row and the migration marker together. Legacy
        // JSON and its backups remain untouched for manual recovery.
        if (stored is null || migratedIds.Count > 0) await ProfileDatabase.SaveSnapshotAsync(restored);
        var selectionChanged = false;
        if (_selectedServerId is not null && migratedIds.TryGetValue(_selectedServerId, out var selectedId))
        {
            _selectedServerId = selectedId;
            selectionChanged = true;
        }
        if (_loadedSettings is { } settings && migratedIds.Count > 0)
        {
            if (selectionChanged) settings.SelectedServerId = _selectedServerId!;
            foreach (var (oldId, newId) in migratedIds)
                if (settings.SuppressedProfileIds.TryGetValue(oldId, out var until))
                    settings.SuppressedProfileIds[newId] = until;
            await SaveSettingsAsync(settings);
        }
        try { await SaveHomeServersAsync(restored); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return restored;
    }

    private async Task<bool> RecoverCorruptArchiveAsync()
    {
        var backup = await ProfileDatabase.ReadRecoveryBackupAsync();
        if (backup is not { Count: > 0 })
        {
            try { backup = await ReadAsync<List<ConfigProfile>>(ProfilesPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var home = new List<ConfigProfile>();
        try { home = await LoadHomeServersAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var recovered = new Dictionary<string, ConfigProfile>(StringComparer.Ordinal);
        // Older archive copies do not prove present connectivity. Keep their
        // history, and let current physical-network tests establish health again.
        foreach (var profile in backup ?? new())
        {
            SavedServerPolicy.RememberSuccess(profile);
            if (profile.Health is ProfileHealth.Working or ProfileHealth.Testing) profile.Health = ProfileHealth.Untested;
            AddRecovered(profile);
        }
        foreach (var profile in home) AddRecovered(profile); // newest home state wins
        if (recovered.Count == 0) return false;
        await ProfileDatabase.RecoverAsync(recovered.Values.ToList());
        if (_loadedSettings is { } settings)
        {
            // Validators may describe imports newer than this recovery copy.
            // Force a fresh body instead of accepting 304 for missing profiles.
            settings.GitHubETag = string.Empty;
            settings.CommunityHealthETag = string.Empty;
            foreach (var subscription in settings.Subscriptions) subscription.ETag = string.Empty;
            try { await SaveSettingsAsync(settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        RecoveryNotice = $"آرشیو از نسخهٔ پشتیبان بازیابی شد؛ {recovered.Count:N0} سرور برگشت. برای بررسی اتصال، جست‌وجو را بزن.";
        LastStorageError = string.Empty;
        return true;

        void AddRecovered(ConfigProfile profile)
        {
            try
            {
                ConfigParser.ValidateCachedProfile(profile);
                profile.Id = ConfigParser.ComputeId(profile);
                if (_suppressedIds.TryGetValue(profile.Id, out var until) && until > DateTime.Now) return;
                if (profile.EchConfigList.Length > 0 &&
                    _suppressedIds.TryGetValue(ConfigParser.ComputeIdWithoutEch(profile), out var oldUntil) &&
                    oldUntil > DateTime.Now) return;
                recovered[profile.Id] = profile;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or NullReferenceException)
            { /* The untouched source and quarantined DB retain unreadable entries. */ }
        }
    }
    public async Task SaveProfilesAsync(IEnumerable<ConfigProfile> profiles)
    {
        if (_archiveReadFailed) throw new IOException("آرشیو قبلی خوانده نشد؛ برای حفظ داده‌ها ذخیره متوقف شد.");
        var snapshot = profiles.ToList();
        foreach (var profile in snapshot)
            if (!profile.LastSuccessfulTest.HasValue) SavedServerPolicy.RememberSuccess(profile);
        await ProfileDatabase.SaveSnapshotAsync(snapshot);
        try { await SaveHomeServersAsync(snapshot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { /* Optional fast-cache errors must not prevent authoritative archive saves. */ }
    }

    public async Task SaveProfileAsync(ConfigProfile profile, IEnumerable<ConfigProfile> allProfiles)
    {
        if (_archiveReadFailed) throw new IOException("آرشیو قبلی خوانده نشد؛ برای حفظ داده‌ها ذخیره متوقف شد.");
        if (!profile.LastSuccessfulTest.HasValue) SavedServerPolicy.RememberSuccess(profile);
        await ProfileDatabase.SaveProfileAsync(profile);
        try { await SaveHomeServersAsync(allProfiles); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private JsonCacheStore FilesFor(string path) => path == HomeServersPath ? _homeFiles
        : path == SettingsPath ? _settingsFiles : _files;

    private async Task<T?> ReadAsync<T>(string path)
    {
        try
        {
            return await Task.Run(() => FilesFor(path).ReadAsync<T>(path)).ConfigureAwait(false);
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
            await Task.Run(() => FilesFor(path).WriteAsync(path, value)).ConfigureAwait(false);
        }
        catch
        {
            LastStorageError = $"ذخیرهٔ {Path.GetFileName(path)} انجام نشد؛ نتیجهٔ قبلی حفظ شد.";
            throw;
        }
    }
}
