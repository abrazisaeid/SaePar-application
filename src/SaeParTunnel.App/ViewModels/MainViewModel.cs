using System.Diagnostics;
using System.Globalization;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;
using SaeParTunnel.App.Services;
using SaeParTunnel.Core.Abstractions;
using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;
#if ANDROID
using Android.Content;
using Android.Content.PM;
using SaeParTunnel.App.Platforms.Android;
#endif

namespace SaeParTunnel.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const string GitHubSource = "GitHub: Epodonios/v2ray-configs";
    private readonly MauiJsonStore _store;
    private readonly ConfigExtractor _extractor;
    private readonly GitHubConfigService _github;
    private readonly ITunnelService _tunnel;
    private AppSettings _settings = new();
    private ConfigProfile? _selectedProfile;
    private ConfigProfile? _selectedHealthyProfile;
    private bool _isBusy;
    private bool _initialized;
    private bool _isTesting;
    private bool _isConnected;
    private bool _showAdvancedConfigTools;
    private string _statusMessage = "در حال آماده‌سازی...";
    private string _searchText = "";
    private string _statusFilter = "همه";
    private string _protocolFilter = "همه";
    private string _sortOption = "جدیدترین اضافه‌شده";
    private CancellationTokenSource? _testCts;
    private int _progressDone, _progressTotal, _progressWorking, _progressFailed, _progressFullWorking;
    private List<ConfigProfile> _filteredSnapshot = new();
    private int _visibleLimit;
    private long _lastProgressUiTicks;
    private double _progressPercent;
    private string _progressSpeed = "-", _progressEta = "-", _testGoalMessage = "";
    private string _newWebsite = "", _newApplication = "";
    private string _connectionStatusMessage = "اتصال فعال نیست.";

    public MainViewModel(MauiJsonStore store, ConfigExtractor extractor, GitHubConfigService github, ITunnelService tunnel)
    {
        _store = store; _extractor = extractor; _github = github; _tunnel = tunnel;

        GetConfigCommand = new Command(async () => await RunSafeAsync(GetConfigAsync));
        ImportClipboardCommand = new Command(async () => await RunSafeAsync(ImportClipboardAsync));
        TestFilteredCommand = new Command(async () => await RunSafeAsync(TestFilteredAsync));
        TestSelectedCommand = new Command(async () => await RunSafeAsync(TestSelectedAsync));
        SelectVisibleCommand = new Command(SelectVisibleForTest);
        ClearSelectionCommand = new Command(ClearTestSelection);
        TestHealthySelectionCommand = new Command(async () => await RunSafeAsync(TestHealthySelectionAsync));
        CancelTestCommand = new Command(CancelTest);
        ConnectCommand = new Command(async () => await RunSafeAsync(ConnectSelectedAsync));
        ConnectBestCommand = new Command(async () => await RunSafeAsync(ConnectBestAsync));
        DisconnectCommand = new Command(async () => await RunSafeAsync(DisconnectAsync));
        SaveSettingsCommand = new Command(async () => await RunSafeAsync(SaveSettingsAsync));
        BrowseXrayCommand = new Command(async () => await RunSafeAsync(BrowseXrayAsync));
        AddWebsiteCommand = new Command(AddWebsite);
        RemoveWebsiteCommand = new Command<string>(RemoveWebsite);
        AddApplicationCommand = new Command(AddApplication);
        BrowseApplicationCommand = new Command(async () => await RunSafeAsync(BrowseApplicationAsync));
        OpenAndroidVpnSettingsCommand = new Command(async () => await RunSafeAsync(OpenAndroidVpnSettingsAsync));
        RemoveApplicationCommand = new Command<WhitelistApplication>(RemoveApplication);
#if ANDROID
        AndroidVpnRuntime.StatusChanged += OnAndroidVpnStatusChanged;
        _connectionStatusMessage = AndroidVpnRuntime.StatusMessage;
#endif
        ResetFiltersCommand = new Command(() => { _visibleLimit = InitialVisibleLimit(); SearchText = ""; StatusFilter = "همه"; ProtocolFilter = "همه"; RefreshFilters(); });
        ToggleAdvancedConfigToolsCommand = new Command(ToggleAdvancedConfigTools);
        LoadMoreCommand = new Command(LoadMore);
    }

    public ObservableRangeCollection<ConfigProfile> Profiles { get; } = new();
    public ObservableRangeCollection<ConfigProfile> FilteredProfiles { get; } = new();
    public ObservableRangeCollection<ConfigProfile> HealthyProfiles { get; } = new();
    public ObservableRangeCollection<string> WhitelistWebsites { get; } = new();
    public ObservableRangeCollection<WhitelistApplication> WhitelistApplications { get; } = new();
    public IReadOnlyList<string> StatusFilters { get; } = new[] { "همه", "سالم", "TCP قابل دسترس", "ناموفق", "تست نشده", "پشتیبانی‌نشده" };
    public IReadOnlyList<string> ProtocolFilters { get; } = new[] { "همه", "VLESS", "VMess", "Trojan", "Shadowsocks" };
    public IReadOnlyList<string> SortOptions { get; } = new[] { "جدیدترین اضافه‌شده", "قدیمی‌ترین اضافه‌شده", "کمترین Ping", "بیشترین Ping", "جدیدترین تست", "نام" };

    public AppSettings Settings { get => _settings; private set => SetProperty(ref _settings, value); }
    public ConfigProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value)) return;
            OnPropertyChanged(nameof(SelectedProfileSummary));
            if (value?.Health == ProfileHealth.Working && !ReferenceEquals(_selectedHealthyProfile, value))
            {
                _selectedHealthyProfile = value;
                OnPropertyChanged(nameof(SelectedHealthyProfile));
                OnPropertyChanged(nameof(SelectedHealthyPingText));
                OnPropertyChanged(nameof(HealthySelectionSummary));
            }
        }
    }
    public ConfigProfile? SelectedHealthyProfile
    {
        get => _selectedHealthyProfile;
        set
        {
            if (!SetProperty(ref _selectedHealthyProfile, value)) return;
            if (value is not null) SelectedProfile = value;
            OnPropertyChanged(nameof(SelectedHealthyPingText));
            OnPropertyChanged(nameof(HealthySelectionSummary));
        }
    }
    public string SelectedProfileSummary => SelectedProfile is null ? "کانفیگی انتخاب نشده" : $"{SelectedProfile.ProtocolText} • {SelectedProfile.Endpoint} • {SelectedProfile.HealthText} • {SelectedProfile.LatencyText}";
    public string SelectedHealthyPingText => SelectedHealthyProfile?.LatencyMs is int ms ? $"{ms} ms" : "-";
    public string HealthySelectionSummary => SelectedHealthyProfile is null
        ? "هنوز سرور سالمی انتخاب نشده است."
        : $"{SelectedHealthyProfile.ProtocolText} • {SelectedHealthyProfile.Endpoint} • آخرین Ping: {SelectedHealthyProfile.LatencyText}";
    private ConfigProfile? RecommendedHealthyProfile => Profiles
        .Where(x => x.Health == ProfileHealth.Working)
        .OrderBy(x => x.LatencyMs ?? int.MaxValue)
        .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .FirstOrDefault();

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsNotBusy));
        }
    }
    public bool IsNotBusy => !IsBusy;
    public bool IsTesting { get => _isTesting; private set => SetProperty(ref _isTesting, value); }
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetProperty(ref _isConnected, value)) return;
        }
    }
    public bool ShowAdvancedConfigTools
    {
        get => _showAdvancedConfigTools;
        private set
        {
            if (!SetProperty(ref _showAdvancedConfigTools, value)) return;
            OnPropertyChanged(nameof(AdvancedConfigToolsText));
        }
    }
    public string AdvancedConfigToolsText => ShowAdvancedConfigTools ? "بستن فیلترها و انتخاب‌ها" : "فیلترها و انتخاب‌ها";
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string BackendTitle => $"{_tunnel.Capabilities.PlatformName} • {_tunnel.Capabilities.BackendName}";
    public string BackendNote => _tunnel.Capabilities.Note;
    public bool CanTunnel => _tunnel.Capabilities.SupportsTunnel;
    public bool CannotTunnel => !CanTunnel;
    public bool CanAppWhitelist => _tunnel.Capabilities.SupportsApplicationWhitelist;
    public bool IsWindows => DeviceInfo.Platform == DevicePlatform.WinUI;
    public bool IsAndroid => DeviceInfo.Platform == DevicePlatform.Android;
    public string ConnectionStatusMessage { get => _connectionStatusMessage; private set => SetProperty(ref _connectionStatusMessage, value); }
    public string AppWhitelistHint => DeviceInfo.Platform == DevicePlatform.Android
        ? "برنامه را انتخاب کن یا Package ID مثل org.telegram.messenger وارد کن"
        : DeviceInfo.Platform == DevicePlatform.WinUI
            ? "Browse را بزن و فایل .exe برنامه را انتخاب کن"
            : "Per-App VPN روی iPhone عمومی محدود است";
    public string BrowseApplicationText => DeviceInfo.Platform == DevicePlatform.Android ? "انتخاب برنامه" : "Browse...";

    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) { _visibleLimit = InitialVisibleLimit(); RefreshFilters(); } } }
    public string StatusFilter { get => _statusFilter; set { if (SetProperty(ref _statusFilter, value)) { _visibleLimit = InitialVisibleLimit(); RefreshFilters(); } } }
    public string ProtocolFilter { get => _protocolFilter; set { if (SetProperty(ref _protocolFilter, value)) { _visibleLimit = InitialVisibleLimit(); RefreshFilters(); } } }
    public string SortOption { get => _sortOption; set { if (SetProperty(ref _sortOption, value)) { _visibleLimit = InitialVisibleLimit(); RefreshFilters(); } } }

    public int TotalProfiles => Profiles.Count;
    public int WorkingProfiles => Profiles.Count(x => x.Health == ProfileHealth.Working);
    public int ReachableProfiles => Profiles.Count(x => x.Health == ProfileHealth.Reachable);
    public int FailedProfiles => Profiles.Count(x => x.Health == ProfileHealth.Failed);
    public int UntestedProfiles => Profiles.Count(x => x.Health == ProfileHealth.Untested);
    public int HealthyProfilesCount => HealthyProfiles.Count;
    public string HealthyProfilesCountText => $"{HealthyProfilesCount:N0} سالم";
    public string PrimaryConfigFlowHint => TotalProfiles == 0
        ? "بدون کانفیگ"
        : WorkingProfiles == 0
            ? "بدون کانفیگ Full-Test سالم"
            : $"{WorkingProfiles:N0} کانفیگ Full-Test سالم آماده اتصال است.";
    public int FilteredCount => _filteredSnapshot.Count;
    public int VisibleCount => FilteredProfiles.Count;
    public bool HasMoreProfiles => VisibleCount < FilteredCount;
    public string VisibleProfilesLabel => HasMoreProfiles ? $"نمایش {VisibleCount:N0} از {FilteredCount:N0}" : $"نمایش {FilteredCount:N0}";

    public int ProgressDone { get => _progressDone; private set => SetProperty(ref _progressDone, value); }
    public int ProgressTotal { get => _progressTotal; private set => SetProperty(ref _progressTotal, value); }
    public int ProgressWorking { get => _progressWorking; private set => SetProperty(ref _progressWorking, value); }
    public int ProgressFailed { get => _progressFailed; private set => SetProperty(ref _progressFailed, value); }
    public int ProgressFullWorking
    {
        get => _progressFullWorking;
        private set
        {
            if (!SetProperty(ref _progressFullWorking, value)) return;
            OnPropertyChanged(nameof(ProgressHealthyLabel));
        }
    }
    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }
    public string ProgressLabel => $"{ProgressDone:N0} / {ProgressTotal:N0} — {ProgressPercent:0}%";
    public double ProgressFraction => Math.Clamp(ProgressPercent / 100d, 0d, 1d);
    public string ProgressSpeed { get => _progressSpeed; private set => SetProperty(ref _progressSpeed, value); }
    public string ProgressEta { get => _progressEta; private set => SetProperty(ref _progressEta, value); }
    public string ProgressHealthyLabel => $"{ProgressFullWorking:N0} سالم کامل";
    public string TestGoalMessage { get => _testGoalMessage; private set => SetProperty(ref _testGoalMessage, value); }

    public string NewWebsite { get => _newWebsite; set => SetProperty(ref _newWebsite, value); }
    public string NewApplication { get => _newApplication; set => SetProperty(ref _newApplication, value); }

    public Command GetConfigCommand { get; }
    public Command ImportClipboardCommand { get; }
    public Command TestFilteredCommand { get; }
    public Command TestSelectedCommand { get; }
    public Command SelectVisibleCommand { get; }
    public Command ClearSelectionCommand { get; }
    public Command TestHealthySelectionCommand { get; }
    public Command CancelTestCommand { get; }
    public Command ConnectCommand { get; }
    public Command ConnectBestCommand { get; }
    public Command DisconnectCommand { get; }
    public Command SaveSettingsCommand { get; }
    public Command BrowseXrayCommand { get; }
    public Command AddWebsiteCommand { get; }
    public Command<string> RemoveWebsiteCommand { get; }
    public Command AddApplicationCommand { get; }
    public Command BrowseApplicationCommand { get; }
    public Command OpenAndroidVpnSettingsCommand { get; }
    public Command<WhitelistApplication> RemoveApplicationCommand { get; }
    public Command ResetFiltersCommand { get; }
    public Command ToggleAdvancedConfigToolsCommand { get; }
    public Command LoadMoreCommand { get; }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        IsBusy = true;
        StatusMessage = "در حال بارگذاری تنظیمات و کانفیگ‌ها...";
        try
        {
            _store.EnsureCreated();
            Settings = await _store.LoadSettingsAsync();
            if (Settings.TestConcurrency <= 0)
                Settings.TestConcurrency = DeviceInfo.Platform == DevicePlatform.WinUI ? 24 : DeviceInfo.Platform == DevicePlatform.Android ? 6 : 4;
            Settings.TestConcurrency = Math.Clamp(Settings.TestConcurrency, 1, DeviceInfo.Platform == DevicePlatform.WinUI ? 64 : 12);
            if (Settings.ProbePort <= 1024 || Settings.ProbePort > 65535) Settings.ProbePort = 10810;

            _visibleLimit = InitialVisibleLimit();
            var loadedProfiles = await _store.LoadProfilesAsync();
            Profiles.ReplaceRange(loadedProfiles);
            WhitelistWebsites.ReplaceRange(Settings.WhitelistWebsites.Distinct(StringComparer.OrdinalIgnoreCase));
            WhitelistApplications.ReplaceRange(Settings.WhitelistApplications);
            RefreshFilters();
            RefreshStats();
            IsConnected = _tunnel.IsConnected;
            StatusMessage = IsConnected ? $"VPN فعال • {BackendTitle}" : $"آماده • {BackendTitle}";
            if (DeviceInfo.Platform == DevicePlatform.Android)
                ConnectionStatusMessage = IsConnected ? "VPN Android فعال است." : "آماده برای اتصال؛ اگر مجوز قبلاً داده شده باشد Android پنجره مجوز را دوباره نشان نمی‌دهد.";
        }
        finally
        {
            _initialized = true;
            IsBusy = false;
        }
    }

    private async Task GetConfigAsync()
    {
        IsBusy = true;
        StatusMessage = "در حال دریافت کانفیگ‌ها...";
        try
        {
            var result = await _github.FetchAsync(Settings.GitHubSubscriptionUrl, Settings.GitHubETag);
            Settings.LastGitHubFetchUtc = DateTime.UtcNow;
            var host = Uri.TryCreate(result.SourceUrl, UriKind.Absolute, out var uri) ? uri.Host : "source";
            var route = result.UsedDirectConnection ? "direct fallback" : "system route";

            if (!result.NotModified)
            {
                StatusMessage = $"دریافت شد از {host}؛ در حال پردازش...";
                var extracted = await Task.Run(() => _extractor.Extract(result.Content, GitHubSource).ToList());
                var existing = Profiles.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
                var additions = new List<ConfigProfile>();
                foreach (var profile in extracted)
                {
                    if (existing.TryGetValue(profile.Id, out var old)) { old.LastSeen = DateTime.Now; continue; }
                    additions.Add(profile); existing[profile.Id] = profile;
                }
                Profiles.AddRange(additions);
                Settings.GitHubETag = result.ETag;
                await _store.SaveProfilesAsync(Profiles);
                StatusMessage = $"Get Config: {additions.Count:N0} جدید • {host} • {route}";
            }
            else
            {
                StatusMessage = $"Get Config: تغییری نیست • {host} • {route}";
            }

            await _store.SaveSettingsAsync(Settings);
            RefreshFilters();
            RefreshStats();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportClipboardAsync()
    {
        var text = await Clipboard.Default.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text)) { StatusMessage = "Clipboard خالی است."; return; }
        var ids = Profiles.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extracted = await Task.Run(() => _extractor.Extract(text, "Clipboard").ToList());
        var additions = extracted.Where(x => ids.Add(x.Id)).ToList();
        Profiles.AddRange(additions);
        await _store.SaveProfilesAsync(Profiles);
        RefreshFilters(); RefreshStats();
        StatusMessage = $"{additions.Count} کانفیگ از Clipboard اضافه شد.";
    }

    private async Task TestSelectedAsync()
    {
        var selected = Profiles.Where(x => x.IsSelectedForTest).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "حداقل یک کانفیگ را با تیک انتخاب کن.";
            return;
        }

        StatusMessage = $"{selected.Count:N0} کانفیگ انتخابی در صف تست...";
        await TestProfilesAsync(selected, guidedHealthySearch: selected.Count > 5);
    }

    private void SelectVisibleForTest()
    {
        foreach (var profile in FilteredProfiles) profile.IsSelectedForTest = true;
        StatusMessage = $"{FilteredProfiles.Count:N0} کانفیگِ نمایش‌داده‌شده برای تست انتخاب شد.";
    }

    private void ClearTestSelection()
    {
        foreach (var profile in Profiles) profile.IsSelectedForTest = false;
        StatusMessage = "انتخاب تست پاک شد.";
    }

    private void ToggleAdvancedConfigTools() => ShowAdvancedConfigTools = !ShowAdvancedConfigTools;

    private async Task TestHealthySelectionAsync()
    {
        var profile = SelectedHealthyProfile;
        if (profile is null)
        {
            StatusMessage = "از لیست سالم‌ها یک کانفیگ انتخاب کن.";
            return;
        }

        StatusMessage = $"در حال تست مجدد {profile.DisplayName}...";
        await TestProfilesAsync(new[] { profile });

        // TestProfilesAsync refreshes the healthy list. If this profile failed,
        // it is automatically removed from the home picker.
        if (profile.Health == ProfileHealth.Working)
        {
            SelectedHealthyProfile = HealthyProfiles.FirstOrDefault(x => x.Id == profile.Id) ?? profile;
            StatusMessage = $"تست مجدد موفق • {profile.LatencyText}";
        }
        else
        {
            StatusMessage = $"این کانفیگ دیگر Full-Test سالم نیست: {profile.TestMessage}";
        }
    }

    private Task TestFilteredAsync() => TestProfilesAsync(_filteredSnapshot.ToList(), guidedHealthySearch: true);

    private async Task TestProfilesAsync(IReadOnlyList<ConfigProfile> candidates, bool guidedHealthySearch = false)
    {
        if (!guidedHealthySearch || candidates.Count <= 5)
        {
            TestGoalMessage = "تست این لیست تا پایان اجرا می‌شود.";
            ProgressFullWorking = 0;
            await TestProfilesLegacyAsync(candidates);
            return;
        }

        if (candidates.Count == 0) { StatusMessage = "کانفیگی برای تست وجود ندارد."; return; }
#if ANDROID
        // libXray keeps several networking managers process-wide. Avoid starting a
        // temporary test core on top of the active Android VPN core.
        if (_tunnel.IsConnected)
        {
            StatusMessage = "برای تست کانفیگ‌ها در Android ابتدا VPN را قطع کن؛ اتصال فعال دست‌نخورده باقی ماند.";
            if (Shell.Current is not null)
                await Shell.Current.DisplayAlert("VPN فعال است", StatusMessage, "باشه");
            return;
        }
#endif
        _testCts?.Cancel(); _testCts?.Dispose(); _testCts = new CancellationTokenSource(); var ct = _testCts.Token;
        IsBusy = true; IsTesting = true; ProgressTotal = candidates.Count; ProgressDone = 0; ProgressWorking = 0; ProgressFailed = 0; ProgressFullWorking = 0;
        TestGoalMessage = "هدف فعلی: پیدا کردن ۵ کانفیگ سالم؛ بعد از آن از شما می‌پرسم ادامه بدهم یا نه.";
        var sw = Stopwatch.StartNew();
        UpdateProgress(sw, 0);
        var tested = 0; var concurrency = Math.Min(Settings.TestConcurrency, candidates.Count);
        var index = 0; int? healthyTarget = 5; var stoppedAfterEnough = false;
        StatusMessage = $"در حال پیدا کردن ۵ کانفیگ سالم از بین {candidates.Count:N0} مورد...";

        async Task TestSkippedAsync()
        {
            var skipped = Interlocked.Increment(ref tested);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                ProgressDone++;
                MaybeUpdateProgress(sw, skipped, false);
            });
        }

        async Task TestOneAsync(ConfigProfile profile)
        {
            if (profile.Health == ProfileHealth.Unsupported)
            {
                await TestSkippedAsync();
                return;
            }

            var old = profile.Health;
            await MainThread.InvokeOnMainThreadAsync(() => profile.Health = ProfileHealth.Testing);
            try
            {
                var result = await _tunnel.TestAsync(profile, Settings, ct).ConfigureAwait(false);
                var newHealth = result.Success ? (result.Level == ValidationLevel.FullProxy ? ProfileHealth.Working : ProfileHealth.Reachable) : ProfileHealth.Failed;
                var done = Interlocked.Increment(ref tested);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    profile.LatencyMs = result.LatencyMs;
                    profile.LastTested = DateTime.Now;
                    profile.TestMessage = result.Message;
                    profile.Health = newHealth;
                    if (!result.Success) profile.FailureCount++; else profile.FailureCount = 0;
                    ProgressDone++;
                    if (newHealth is ProfileHealth.Working or ProfileHealth.Reachable) ProgressWorking++;
                    if (newHealth == ProfileHealth.Working) ProgressFullWorking++;
                    else if (newHealth == ProfileHealth.Failed) ProgressFailed++;
                    MaybeUpdateProgress(sw, done, done == ProgressTotal);
                });
            }
            catch (OperationCanceledException)
            {
                await MainThread.InvokeOnMainThreadAsync(() => profile.Health = old);
            }
        }

        try
        {
            while (!ct.IsCancellationRequested && index < candidates.Count)
            {
                var batchLimit = healthyTarget is int target
                    ? Math.Max(1, Math.Min(concurrency, target - ProgressFullWorking))
                    : concurrency;
                var batch = new List<ConfigProfile>(batchLimit);

                while (batch.Count < batchLimit && index < candidates.Count)
                {
                    var profile = candidates[index++];
                    if (profile.Health == ProfileHealth.Unsupported)
                    {
                        await TestSkippedAsync();
                        continue;
                    }

                    batch.Add(profile);
                }

                if (batch.Count == 0) continue;

                StatusMessage = healthyTarget is int activeTarget
                    ? $"در حال پیدا کردن {activeTarget:N0} کانفیگ سالم؛ {ProgressFullWorking:N0} سالم تا اینجا..."
                    : $"در حال تست همه موارد باقی‌مانده؛ {ProgressFullWorking:N0} سالم تا اینجا...";
                await Task.WhenAll(batch.Select(TestOneAsync));

                if (healthyTarget is null || ProgressFullWorking < healthyTarget.Value) continue;

                if (index >= candidates.Count)
                    continue;

                if (healthyTarget.Value == 5)
                {
                    if (!await AskContinueAfterHealthyMilestoneAsync(5))
                    {
                        stoppedAfterEnough = true;
                        break;
                    }

                    healthyTarget = 10;
                    TestGoalMessage = "هدف فعلی: پیدا کردن ۱۰ کانفیگ سالم؛ بعد دوباره از شما می‌پرسم.";
                    continue;
                }

                if (healthyTarget.Value == 10)
                {
                    if (!await AskContinueAfterHealthyMilestoneAsync(10))
                    {
                        stoppedAfterEnough = true;
                        break;
                    }

                    var plan = await AskHealthySearchPlanAfterTenAsync(ProgressFullWorking, ProgressTotal - ProgressDone);
                    if (plan.Stop)
                    {
                        stoppedAfterEnough = true;
                        break;
                    }

                    healthyTarget = plan.TestAll ? null : plan.Target;
                    TestGoalMessage = plan.TestAll
                        ? "هدف فعلی: تست همه کانفیگ‌های باقی‌مانده."
                        : $"هدف فعلی: پیدا کردن {healthyTarget:N0} کانفیگ سالم.";
                    continue;
                }

                await ShowReachedHealthyTargetAsync(healthyTarget.Value);
                stoppedAfterEnough = true;
                break;
            }
        }
        finally
        {
            MaybeUpdateProgress(sw, tested, true);
            await _store.SaveProfilesAsync(Profiles);
            RefreshFilters(); RefreshStats();
            IsTesting = false; IsBusy = false;
            StatusMessage = ct.IsCancellationRequested
                ? $"تست متوقف شد؛ {ProgressDone}/{ProgressTotal} بررسی شد."
                : stoppedAfterEnough
                    ? $"تست با انتخاب شما متوقف شد؛ {ProgressFullWorking:N0} کانفیگ سالم پیدا شد و {ProgressDone:N0}/{ProgressTotal:N0} مورد بررسی شد."
                    : $"تست تمام شد؛ {ProgressFullWorking:N0} سالم کامل، {ProgressWorking:N0} موفق/قابل‌دسترس و {ProgressFailed:N0} ناموفق.";
        }
    }

    private async Task<bool> AskContinueAfterHealthyMilestoneAsync(int count)
    {
        if (Shell.Current is null) return true;

        return await MainThread.InvokeOnMainThreadAsync(() =>
            Shell.Current.DisplayAlert(
                $"{count:N0} کانفیگ سالم پیدا شد",
                $"تا اینجا {count:N0} کانفیگ Full-Test سالم داریم. همین تعداد کافی است یا ادامه بدهم؟",
                "ادامه بده",
                "کافیه"));
    }

    private async Task<(bool Stop, bool TestAll, int? Target)> AskHealthySearchPlanAfterTenAsync(int currentHealthy, int remaining)
    {
        if (Shell.Current is null) return (false, true, null);

        var exactCountText = "تعداد مشخص";
        var testAllText = "همه را تست کن";
        var choice = await MainThread.InvokeOnMainThreadAsync(() =>
            Shell.Current.DisplayActionSheet(
                "ادامه تست",
                "کافیه",
                null,
                exactCountText,
                testAllText));

        if (choice == testAllText) return (false, true, null);
        if (choice != exactCountText) return (true, false, null);

        var maxTarget = Math.Max(currentHealthy + 1, currentHealthy + remaining);
        var suggested = Math.Min(currentHealthy + 10, maxTarget);
        while (true)
        {
            var answer = await MainThread.InvokeOnMainThreadAsync(() =>
                Shell.Current.DisplayPromptAsync(
                    "چندتا سالم پیدا کنم؟",
                    $"الان {currentHealthy:N0} سالم داریم. عدد هدف را وارد کن یا «کافیه» را بزن.",
                    "ادامه",
                    "کافیه",
                    "مثلا 20",
                    6,
                    Keyboard.Numeric,
                    suggested.ToString(CultureInfo.CurrentCulture)));

            if (string.IsNullOrWhiteSpace(answer)) return (true, false, null);
            if (int.TryParse(answer.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var target) ||
                int.TryParse(answer.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out target))
            {
                if (target > currentHealthy)
                    return (false, false, Math.Min(target, maxTarget));
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
                Shell.Current.DisplayAlert(
                    "عدد درست نیست",
                    $"یک عدد بزرگ‌تر از {currentHealthy:N0} وارد کن.",
                    "باشه"));
        }
    }

    private async Task ShowReachedHealthyTargetAsync(int target)
    {
        if (Shell.Current is null) return;
        await MainThread.InvokeOnMainThreadAsync(() =>
            Shell.Current.DisplayAlert(
                "به هدف رسیدم",
                $"{target:N0} کانفیگ Full-Test سالم پیدا شد.",
                "باشه"));
    }

    private async Task TestProfilesLegacyAsync(IReadOnlyList<ConfigProfile> candidates)
    {
        if (candidates.Count == 0) { StatusMessage = "کانفیگی برای تست وجود ندارد."; return; }
#if ANDROID
        // libXray keeps several networking managers process-wide. Avoid starting a
        // temporary test core on top of the active Android VPN core.
        if (_tunnel.IsConnected)
        {
            StatusMessage = "برای تست کانفیگ‌ها در Android ابتدا VPN را قطع کن؛ اتصال فعال دست‌نخورده باقی ماند.";
            if (Shell.Current is not null)
                await Shell.Current.DisplayAlert("VPN فعال است", StatusMessage, "باشه");
            return;
        }
#endif
        _testCts?.Cancel(); _testCts?.Dispose(); _testCts = new CancellationTokenSource(); var ct = _testCts.Token;
        IsBusy = true; IsTesting = true; ProgressTotal = candidates.Count; ProgressDone = 0; ProgressWorking = 0; ProgressFailed = 0; ProgressFullWorking = 0;
        var sw = Stopwatch.StartNew();
        UpdateProgress(sw, 0);
        var next = -1; var tested = 0; var concurrency = Math.Min(Settings.TestConcurrency, candidates.Count);
        StatusMessage = $"تست با {concurrency} Worker هم‌زمان...";

        async Task Worker()
        {
            while (!ct.IsCancellationRequested)
            {
                var i = Interlocked.Increment(ref next); if (i >= candidates.Count) return;
                var p = candidates[i];
                if (p.Health == ProfileHealth.Unsupported)
                {
                    var skipped = Interlocked.Increment(ref tested);
                    MainThread.BeginInvokeOnMainThread(() => { ProgressDone++; MaybeUpdateProgress(sw, skipped, false); });
                    continue;
                }
                var old = p.Health;
                try
                {
                    var result = await _tunnel.TestAsync(p, Settings, ct).ConfigureAwait(false);
                    var newHealth = result.Success ? (result.Level == ValidationLevel.FullProxy ? ProfileHealth.Working : ProfileHealth.Reachable) : ProfileHealth.Failed;
                    var done = Interlocked.Increment(ref tested);
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        p.LatencyMs = result.LatencyMs;
                        p.LastTested = DateTime.Now;
                        p.TestMessage = result.Message;
                        p.Health = newHealth;
                        if (!result.Success) p.FailureCount++; else p.FailureCount = 0;
                        ProgressDone++;
                        if (newHealth is ProfileHealth.Working or ProfileHealth.Reachable) ProgressWorking++;
                        if (newHealth == ProfileHealth.Working) ProgressFullWorking++;
                        else if (newHealth == ProfileHealth.Failed) ProgressFailed++;
                        MaybeUpdateProgress(sw, done, done == ProgressTotal);
                    });
                }
                catch (OperationCanceledException)
                {
                    await MainThread.InvokeOnMainThreadAsync(() => p.Health = old);
                    return;
                }
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Worker()));
        }
        finally
        {
            MaybeUpdateProgress(sw, tested, true);
            await _store.SaveProfilesAsync(Profiles);
            RefreshFilters(); RefreshStats();
            IsTesting = false; IsBusy = false;
            StatusMessage = ct.IsCancellationRequested
                ? $"تست متوقف شد؛ {ProgressDone}/{ProgressTotal} بررسی شد."
                : $"تست تمام شد؛ {ProgressFullWorking:N0} سالم کامل، {ProgressWorking:N0} موفق/قابل‌دسترس و {ProgressFailed:N0} ناموفق.";
        }
    }

    private void CancelTest()
    {
        if (_testCts is { IsCancellationRequested: false })
        {
            _testCts.Cancel();
            StatusMessage = "در حال توقف تست‌ها...";
        }
    }

    private void MaybeUpdateProgress(Stopwatch sw, int tested, bool force)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastProgressUiTicks);
        var elapsedMs = last == 0 ? double.MaxValue : (now - last) * 1000d / Stopwatch.Frequency;
        if (!force && elapsedMs < 200) return;
        Interlocked.Exchange(ref _lastProgressUiTicks, now);
        UpdateProgress(sw, tested);
    }

    private void UpdateProgress(Stopwatch sw, int tested)
    {
        ProgressPercent = ProgressTotal == 0 ? 0 : ProgressDone * 100d / ProgressTotal;
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(ProgressFraction));
        var rate = sw.Elapsed.TotalSeconds > .2 && tested > 0 ? tested / sw.Elapsed.TotalSeconds : 0;
        ProgressSpeed = rate > 0 ? $"{rate:0.0} config/s" : "در حال محاسبه...";
        var remain = Math.Max(0, ProgressTotal - ProgressDone);
        ProgressEta = rate > 0 ? $"ETA: {TimeSpan.FromSeconds(remain / rate):hh\\:mm\\:ss}" : "ETA: -";
    }

    private async Task ConnectSelectedAsync()
    {
        var candidate = SelectedHealthyProfile
            ?? (SelectedProfile?.Health == ProfileHealth.Working ? SelectedProfile : null)
            ?? HealthyProfiles.FirstOrDefault();
        if (candidate is null)
        {
            StatusMessage = "کانفیگ Full-Test سالمی برای اتصال نداریم؛ ابتدا کانفیگ‌ها را تست کن.";
            if (Shell.Current is not null) await Shell.Current.DisplayAlert("سرور سالم پیدا نشد", StatusMessage, "باشه");
            return;
        }
        SelectedHealthyProfile = candidate;
        SelectedProfile = candidate;
        if (!_tunnel.Capabilities.SupportsTunnel)
        {
            StatusMessage = _tunnel.Capabilities.Note;
            if (Shell.Current is not null) await Shell.Current.DisplayAlert("تونل این پلتفرم هنوز فعال نیست", StatusMessage, "باشه");
            return;
        }

        IsBusy = true;
        try
        {
            IsConnected = false;
            StatusMessage = "در حال اتصال...";
            ConnectionStatusMessage = DeviceInfo.Platform == DevicePlatform.Android
                ? "مرحله 1: بررسی مجوز VPN Android..."
                : "در حال اتصال...";
            await _tunnel.EnsureReadyAsync(Settings, cancellationToken: default);
            await _tunnel.ConnectAsync(SelectedProfile, Settings);
            StatusMessage = "تونل آماده شد؛ در حال تست اینترنت...";
            ConnectionStatusMessage = "در حال تست عبور واقعی اینترنت از تونل...";

            var validation = await _tunnel.TestCurrentConnectionAsync(Settings);
            if (!validation.Success || validation.Level != ValidationLevel.FullProxy)
            {
                IsConnected = false;
                StatusMessage = "تست اینترنت ناموفق بود؛ اتصال تأیید نشد.";
                ConnectionStatusMessage = "تا تأیید کامل اینترنت، وضعیت متصل نمایش داده نمی‌شود: " + validation.Message;
                try { await _tunnel.DisconnectAsync(Settings); } catch { }
                if (Shell.Current is not null)
                    await Shell.Current.DisplayAlert("تست اینترنت ناموفق بود", validation.Message, "باشه");
                return;
            }

            if (SelectedProfile.Health == ProfileHealth.Working) SelectedHealthyProfile = SelectedProfile;
            IsConnected = true;
            StatusMessage = DeviceInfo.Platform == DevicePlatform.Android
                ? $"VPN Android متصل و تست اینترنت تأیید شد: {SelectedProfile.DisplayName}"
                : $"متصل و تست اینترنت تأیید شد: {SelectedProfile.DisplayName} • HTTP محلی: 127.0.0.1:{Settings.HttpPort}";
            ConnectionStatusMessage = DeviceInfo.Platform == DevicePlatform.Android
                ? $"✓ اینترنت از VPN تأیید شد • {SelectedProfile.DisplayName} • {validation.Message}"
                : $"✓ اینترنت از Proxy تأیید شد • {validation.Message}";
            if (DeviceInfo.Platform == DevicePlatform.Android && Shell.Current is not null)
                await Shell.Current.DisplayAlert("VPN متصل شد", $"اینترنت از SaePar Tunnel تأیید شد.\n{validation.Message}", "باشه");
        }
        finally { IsBusy = false; }
    }

    private async Task ConnectBestAsync()
    {
        var best = RecommendedHealthyProfile;
        if (best is null) { StatusMessage = "هنوز کانفیگ Full-Test سالم نداریم."; return; }
        SelectedHealthyProfile = best;
        SelectedProfile = best;
        await ConnectSelectedAsync();
    }

    private async Task DisconnectAsync()
    {
        await _tunnel.DisconnectAsync(Settings);
        IsConnected = false;
        StatusMessage = "اتصال قطع شد.";
        ConnectionStatusMessage = "VPN قطع است.";
    }

#if ANDROID
    private void OnAndroidVpnStatusChanged(object? sender, AndroidVpnStatusEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ConnectionStatusMessage = e.Message;
            StatusMessage = e.Message;
            if (e.Connected.HasValue) IsConnected = e.Connected.Value;
        });
    }
#endif

    private Task OpenAndroidVpnSettingsAsync()
    {
#if ANDROID
        var activity = Platform.CurrentActivity;
        if (activity is null)
            throw new InvalidOperationException("Android Activity در دسترس نیست.");
        var intent = new Intent(global::Android.Provider.Settings.ActionVpnSettings);
        activity.StartActivity(intent);
        ConnectionStatusMessage = "صفحه تنظیمات VPN Android باز شد. وضعیت SaePar Tunnel را از اینجا می‌توانی ببینی.";
#endif
        return Task.CompletedTask;
    }

    private async Task BrowseXrayAsync()
    {
        if (!IsWindows) return;
        var executableType = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.WinUI] = new[] { ".exe" }
        });
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "فایل xray.exe را انتخاب کن",
            FileTypes = executableType
        });
        if (result is null) return;
        if (!string.Equals(result.FileName, "xray.exe", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "فایل انتخابی باید xray.exe باشد.";
            return;
        }
        Settings.XrayPath = result.FullPath;
        await _store.SaveSettingsAsync(Settings);
        OnPropertyChanged(nameof(Settings));
        StatusMessage = $"Xray انتخاب شد: {result.FullPath}";
    }

    private async Task SaveSettingsAsync()
    {
        Settings.TestConcurrency = Math.Clamp(Settings.TestConcurrency, 1, DeviceInfo.Platform == DevicePlatform.WinUI ? 64 : 12);
        Settings.WhitelistWebsites = WhitelistWebsites.ToList();
        Settings.WhitelistApplications = WhitelistApplications.ToList();
        await _store.SaveSettingsAsync(Settings);
        StatusMessage = "تنظیمات ذخیره شد.";
    }

    private void AddWebsite()
    {
        var value = NormalizeDomain(NewWebsite); if (string.IsNullOrWhiteSpace(value)) return;
        if (!WhitelistWebsites.Contains(value, StringComparer.OrdinalIgnoreCase)) WhitelistWebsites.Add(value);
        NewWebsite = "";
        _ = SaveSettingsAsync();
    }

    private void RemoveWebsite(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) WhitelistWebsites.Remove(value);
        _ = SaveSettingsAsync();
    }

    private static string NormalizeDomain(string text)
    {
        text = (text ?? "").Trim(); if (text.Length == 0) return "";
        if (Uri.TryCreate(text.Contains("://") ? text : "https://" + text, UriKind.Absolute, out var uri))
            return uri.Host.Trim('.').ToLowerInvariant();
        return text.Trim('.').ToLowerInvariant();
    }

    private async Task BrowseApplicationAsync()
    {
        if (!CanAppWhitelist) return;

        if (DeviceInfo.Platform == DevicePlatform.WinUI)
        {
            var executableType = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.WinUI] = new[] { ".exe" }
            });
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "فایل اجرایی برنامه را انتخاب کن",
                FileTypes = executableType
            });
            if (result is null) return;
            NewApplication = result.FullPath;
            AddApplication();
            StatusMessage = $"برنامه اضافه شد: {result.FileName}";
            return;
        }

#if ANDROID
        if (DeviceInfo.Platform == DevicePlatform.Android)
        {
            var context = Android.App.Application.Context;
            var pm = context.PackageManager ?? throw new InvalidOperationException("PackageManager در دسترس نیست.");
            using var launcherIntent = new Intent(Intent.ActionMain);
            launcherIntent.AddCategory(Intent.CategoryLauncher);
#pragma warning disable CS0618
            var resolved = pm.QueryIntentActivities(launcherIntent, PackageInfoFlags.MatchAll);
#pragma warning restore CS0618
            var apps = resolved
                .Where(x => x.ActivityInfo?.PackageName is not null)
                .Select(x => new
                {
                    Label = x.LoadLabel(pm)?.ToString() ?? x.ActivityInfo!.PackageName!,
                    Package = x.ActivityInfo!.PackageName!
                })
                .GroupBy(x => x.Package, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase)
                .Take(120)
                .ToList();

            if (apps.Count == 0)
            {
                StatusMessage = "برنامه قابل انتخابی پیدا نشد؛ Package ID را دستی وارد کن.";
                return;
            }

            var labels = apps.Select(x => $"{x.Label}  —  {x.Package}").ToArray();
            var choice = Shell.Current is null
                ? null
                : await Shell.Current.DisplayActionSheet("انتخاب برنامه برای Whitelist", "لغو", null, labels);
            if (string.IsNullOrWhiteSpace(choice) || choice == "لغو") return;
            var index = Array.IndexOf(labels, choice);
            if (index < 0) return;
            NewApplication = apps[index].Package;
            AddApplication(apps[index].Label);
            StatusMessage = $"برنامه اضافه شد: {apps[index].Label}";
            return;
        }
#endif

        StatusMessage = "انتخاب خودکار برنامه روی این پلتفرم در دسترس نیست.";
    }

    private void AddApplication(string? friendlyName)
    {
        var id = (NewApplication ?? "").Trim(); if (id.Length == 0 || !CanAppWhitelist) return;
        WhitelistApplication app;
        if (DeviceInfo.Platform == DevicePlatform.Android)
        {
            app = new WhitelistApplication
            {
                PackageName = id,
                Platform = "Android",
                FriendlyName = string.IsNullOrWhiteSpace(friendlyName) ? id : friendlyName
            };
        }
        else
        {
            app = new WhitelistApplication
            {
                ExecutablePath = id,
                Platform = "Windows",
                FriendlyName = string.IsNullOrWhiteSpace(friendlyName) ? Path.GetFileNameWithoutExtension(id) : friendlyName
            };
        }

        if (!WhitelistApplications.Any(x => string.Equals(x.Identifier, app.Identifier, StringComparison.OrdinalIgnoreCase)))
            WhitelistApplications.Add(app);
        NewApplication = "";
        _ = SaveSettingsAsync();
    }

    private void AddApplication() => AddApplication(null);

    private void RemoveApplication(WhitelistApplication? app)
    {
        if (app is not null) WhitelistApplications.Remove(app);
        _ = SaveSettingsAsync();
    }

    private void RefreshFilters()
    {
        var query = Profiles.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(SearchText))
            query = query.Where(x => (x.DisplayName + " " + x.Endpoint + " " + x.Source).Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        if (ProtocolFilter != "همه") query = query.Where(x => x.ProtocolText == ProtocolFilter);
        query = StatusFilter switch
        {
            "سالم" => query.Where(x => x.Health == ProfileHealth.Working),
            "TCP قابل دسترس" => query.Where(x => x.Health == ProfileHealth.Reachable),
            "ناموفق" => query.Where(x => x.Health == ProfileHealth.Failed),
            "تست نشده" => query.Where(x => x.Health == ProfileHealth.Untested),
            "پشتیبانی‌نشده" => query.Where(x => x.Health == ProfileHealth.Unsupported),
            _ => query
        };

        query = SortOption switch
        {
            "قدیمی‌ترین اضافه‌شده" => query.OrderBy(x => x.FirstSeen).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            "کمترین Ping" => query.OrderBy(x => x.LatencyMs ?? int.MaxValue).ThenByDescending(x => x.FirstSeen),
            "بیشترین Ping" => query.OrderByDescending(x => x.LatencyMs ?? int.MinValue).ThenByDescending(x => x.FirstSeen),
            "جدیدترین تست" => query.OrderByDescending(x => x.LastTested ?? DateTime.MinValue).ThenByDescending(x => x.FirstSeen),
            "نام" => query.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderByDescending(x => x.FirstSeen).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        };

        _filteredSnapshot = query.ToList();
        if (_visibleLimit <= 0) _visibleLimit = InitialVisibleLimit();
        FilteredProfiles.ReplaceRange(_filteredSnapshot.Take(_visibleLimit));
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasMoreProfiles));
        OnPropertyChanged(nameof(VisibleProfilesLabel));
        NotifyConfigFlowChanged();
    }

    private int InitialVisibleLimit() => DeviceInfo.Platform == DevicePlatform.WinUI ? 1000 : 120;

    private void LoadMore()
    {
        if (!HasMoreProfiles) return;
        _visibleLimit = Math.Min(_visibleLimit + (DeviceInfo.Platform == DevicePlatform.WinUI ? 1000 : 120), FilteredCount);
        FilteredProfiles.ReplaceRange(_filteredSnapshot.Take(_visibleLimit));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasMoreProfiles));
        OnPropertyChanged(nameof(VisibleProfilesLabel));
    }

    private void RefreshHealthyProfiles()
    {
        var selectedId = SelectedHealthyProfile?.Id;
        var healthy = Profiles
            .Where(x => x.Health == ProfileHealth.Working)
            .OrderBy(x => x.LatencyMs ?? int.MaxValue)
            .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        HealthyProfiles.ReplaceRange(healthy);
        var next = selectedId is null ? null : healthy.FirstOrDefault(x => x.Id == selectedId);
        next ??= healthy.FirstOrDefault();
        SelectedHealthyProfile = next;
        OnPropertyChanged(nameof(HealthyProfilesCount));
        OnPropertyChanged(nameof(HealthyProfilesCountText));
        OnPropertyChanged(nameof(SelectedHealthyPingText));
        OnPropertyChanged(nameof(HealthySelectionSummary));
    }

    private void RefreshStats()
    {
        RefreshHealthyProfiles();
        OnPropertyChanged(nameof(TotalProfiles));
        OnPropertyChanged(nameof(WorkingProfiles));
        OnPropertyChanged(nameof(ReachableProfiles));
        OnPropertyChanged(nameof(FailedProfiles));
        OnPropertyChanged(nameof(UntestedProfiles));
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasMoreProfiles));
        OnPropertyChanged(nameof(VisibleProfilesLabel));
        NotifyConfigFlowChanged();
    }

    private void NotifyConfigFlowChanged()
    {
        OnPropertyChanged(nameof(PrimaryConfigFlowHint));
    }

    private async Task RunSafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException)
        {
            StatusMessage = "عملیات متوقف شد.";
            IsTesting = false;
            IsBusy = false;
        }
        catch (Exception ex)
        {
            var friendly = FlattenException(ex);
            StatusMessage = "خطا: " + friendly;
            if (DeviceInfo.Platform == DevicePlatform.Android) ConnectionStatusMessage = "✕ " + friendly;
            IsTesting = false;
            IsBusy = false;
            if (Shell.Current is not null)
                await Shell.Current.DisplayAlert("خطا", friendly, "باشه");
        }
    }

    private static string FlattenException(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? current = ex; current is not null && parts.Count < 5; current = current.InnerException)
        {
            var message = (current.Message ?? string.Empty)
                .Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            if (message.Length > 0 && !parts.Contains(message, StringComparer.OrdinalIgnoreCase)) parts.Add(message);
        }
        return parts.Count == 0 ? "خطای ناشناخته" : string.Join(" → ", parts);
    }
}
