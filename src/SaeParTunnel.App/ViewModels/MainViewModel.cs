using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;
using SaeParTunnel.App.Pages;
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
    private const int SimpleSearchHealthyTarget = 5;
    private enum ConnectionUiPhase
    {
        Idle,
        Connecting,
        Validating,
        Disconnecting
    }

    private sealed record SubscriptionFetchOutcome(int Added, bool NotModified, string Host, string Route);

    private readonly MauiJsonStore _store;
    private readonly ConfigExtractor _extractor;
    private readonly GitHubConfigService _github;
    private readonly CommunityHealthService _communityHealth;
    private readonly QrCodeService _qrCode;
    private readonly ITunnelService _tunnel;
    private AppSettings _settings = new();
    private ConfigProfile? _selectedProfile;
    private ConfigProfile? _selectedHealthyProfile;
    private bool _isBusy;
    private bool _initialized;
    private Task? _initializationTask;
    private bool _isInitializing;
    private bool _isLoadingHome;
    private bool _saveInitialSelection;
    private bool _waitingForTests;
    private TaskCompletionSource? _testCompletion;
    private TaskCompletionSource? _discoveryCompletion;
    private bool _isTesting;
    private bool _findingServers;
    private bool _connectedSearch;
    private long _connectionGeneration;
    private bool _homeSearchCompleted;
    private CancellationTokenSource? _discoveryCts;
    private string _homeNotice = "";
    private bool _isPinging;
    private CancellationTokenSource? _pingCts;
    private string? _activeProfileId;
    private int? _healthySearchTarget;
    private string _pingFeedback = "";
    private string _cleanupSummary = "سرور قدیمی پس از ۳ تست ناموفق پیاپی پاک می‌شود؛ سرور فعال حفظ می‌شود.";
    private bool _isConnected;
    private ConnectionUiPhase _connectionUiPhase;
    private bool _showAdvancedConfigTools;
    private string _statusMessage = "در حال آماده‌سازی...";
    private string _searchText = "";
    private string _statusFilter = "همه";
    private string _protocolFilter = "همه";
    private string _sortOption = "بهترین امتیاز";
    private CancellationTokenSource? _testCts;
    private int _progressDone, _progressTotal, _progressWorking, _progressFailed, _progressFullWorking;
    private List<ConfigProfile> _filteredSnapshot = new();
    private int _visibleLimit;
    private long _lastProgressUiTicks;
    private double _progressPercent;
    private string _progressSpeed = "-", _progressEta = "-", _testGoalMessage = "";
    private string _newWebsite = "", _newApplication = "";
    private string _directRoutingText = "", _routingFeedback = "";
    private string _iranRoutingSummary = "فهرست آمادهٔ ایران همراه برنامه است.";
    private string _newSubscriptionName = "", _newSubscriptionUrl = "";
    private bool _isSubscriptionEditorExpanded;
    private string _communityHealthStatusMessage = "داده جمعی خاموش است. برای استفاده، یک آدرس HTTPS JSON عمومی وارد کن.";
    private string _connectionStatusMessage = "اتصال فعال نیست.";
    private string _diagnosticsReport = "هنوز عیب‌یابی اجرا نشده است.";
    private ConfigProfile? _recommendedHealthyProfile;
    private int _totalProfiles, _workingProfiles, _reachableProfiles, _failedProfiles, _untestedProfiles;

    public MainViewModel(
        MauiJsonStore store,
        ConfigExtractor extractor,
        GitHubConfigService github,
        CommunityHealthService communityHealth,
        QrCodeService qrCode,
        ITunnelService tunnel)
    {
        _store = store; _extractor = extractor; _github = github; _communityHealth = communityHealth; _qrCode = qrCode; _tunnel = tunnel;

        GetConfigCommand = new Command(async () => await RunSafeAsync(GetConfigAsync));
        FindServersCommand = new Command(async () => await RunSafeAsync(FindServersAsync));
        SelectHomeServerCommand = new Command<ConfigProfile>(profile =>
        {
            if (CanSelectHomeServer && profile is not null) SelectedHealthyProfile = profile;
        });
        PingSelectedServerCommand = new Command(async () => await RunSafeAsync(PingSelectedServerAsync));
        CleanupOldServersCommand = new Command(async () => await RunSafeAsync(CleanupOldServersAsync));
        ViewFailedServersCommand = new Command(async () => await RunSafeAsync(async () =>
        {
            SearchText = "";
            ProtocolFilter = "همه";
            StatusFilter = "ناموفق";
            ShowAdvancedConfigTools = true;
            if (Shell.Current is not null) await Shell.Current.GoToAsync("advanced-configs");
        }));
        FetchSubscriptionCommand = new Command<SubscriptionSourceViewModel>(async source =>
        {
            if (source is not null) await RunSafeAsync(() => FetchSubscriptionAsync(source));
        });
        ToggleSubscriptionCommand = new Command<SubscriptionSourceViewModel>(source => source?.ToggleExpanded());
        ToggleSubscriptionEditorCommand = new Command(ToggleSubscriptionEditor);
        AddSubscriptionCommand = new Command(async () => await RunSafeAsync(AddSubscriptionAsync));
        RemoveSubscriptionCommand = new Command<SubscriptionSourceViewModel>(async source =>
        {
            if (source is not null) await RunSafeAsync(() => RemoveSubscriptionAsync(source));
        });
        ShareProfileCommand = new Command<ConfigProfile>(async profile =>
        {
            if (profile is not null) await ShareProfileSafelyAsync(profile);
        });
        RefreshCommunityHealthCommand = new Command(async () => await RunSafeAsync(async () => await RefreshCommunityHealthAsync()));
        ImportClipboardCommand = new Command(async () => await RunSafeAsync(ImportClipboardAsync));
        TestFilteredCommand = new Command(async () => await RunSafeAsync(TestFilteredAsync));
        TestSelectedCommand = new Command(async () => await RunSafeAsync(TestSelectedAsync));
        SelectVisibleCommand = new Command(SelectVisibleForTest);
        ClearSelectionCommand = new Command(ClearTestSelection);
        TestHealthySelectionCommand = new Command(async () => await RunSafeAsync(TestHealthySelectionAsync));
        CancelTestCommand = new Command(CancelTest);
        ConnectCommand = new Command(async () => await ConnectFromHomeAsync(), () => CanConnectHome);
        ConnectBestCommand = new Command(async () => await RunSafeAsync(ConnectBestAsync), () => CanStartConnection);
        DisconnectCommand = new Command(async () => await DisconnectFromHomeAsync(), () => CanStopConnection);
        SaveSettingsCommand = new Command(async () => await RunSafeAsync(SaveSettingsAsync));
        BrowseXrayCommand = new Command(async () => await RunSafeAsync(BrowseXrayAsync));
        AddWebsiteCommand = new Command(AddWebsite);
        RemoveWebsiteCommand = new Command<string>(RemoveWebsite);
        AddDirectRoutesCommand = new Command(async () => await RunSafeAsync(() => AddDirectRoutesAsync(DirectRoutingText)));
        ImportDirectRoutesCommand = new Command(async () => await RunSafeAsync(ImportDirectRoutesAsync));
        RemoveDirectRouteCommand = new Command<string>(async entry => await RunSafeAsync(async () =>
        {
            if (!CanSearchServers || entry is null) return;
            DirectRoutingEntries.Remove(entry);
            RefreshRoutingList();
            await PersistSettingsAsync();
        }));
        ClearDirectRoutesCommand = new Command(async () => await RunSafeAsync(async () =>
        {
            if (!CanSearchServers || DirectRoutingEntries.Count == 0) return;
            if (!await Shell.Current.DisplayAlert("پاک کردن فهرست شخصی", "فهرست شخصی پاک شود؟ فهرست پیش‌فرض ایران حفظ می‌شود.", "پاک شود", "لغو")) return;
            DirectRoutingEntries.Clear();
            RefreshRoutingList();
            await PersistSettingsAsync();
        }));
        AddApplicationCommand = new Command(AddApplication);
        BrowseApplicationCommand = new Command(async () => await RunSafeAsync(BrowseApplicationAsync));
        OpenAndroidVpnSettingsCommand = new Command(async () => await RunSafeAsync(OpenAndroidVpnSettingsAsync));
        OpenAndroidNotificationSettingsCommand = new Command(() =>
        {
#if ANDROID
            AndroidNotificationPermission.OpenSettings();
#endif
        });
        RemoveApplicationCommand = new Command<WhitelistApplication>(RemoveApplication);
        RefreshDiagnosticsCommand = new Command(() => RefreshDiagnosticsReport());
        CopyDiagnosticsCommand = new Command(async () => await RunSafeAsync(CopyDiagnosticsAsync));
        DiagnoseConnectionCommand = new Command(async () => await RunSafeAsync(DiagnoseConnectionAsync));
#if ANDROID
        AndroidVpnRuntime.StatusChanged += OnAndroidVpnStatusChanged;
        _connectionStatusMessage = AndroidVpnRuntime.StatusMessage;
#endif
        ResetFiltersCommand = new Command(() => { _visibleLimit = InitialVisibleLimit(); SearchText = ""; StatusFilter = "همه"; ProtocolFilter = "همه"; SortOption = "بهترین امتیاز"; RefreshFilters(); });
        ToggleAdvancedConfigToolsCommand = new Command(ToggleAdvancedConfigTools);
        LoadMoreCommand = new Command(LoadMore);
    }

    public ObservableRangeCollection<ConfigProfile> Profiles { get; } = new();
    public ObservableRangeCollection<ConfigProfile> FilteredProfiles { get; } = new();
    public ObservableRangeCollection<ConfigProfile> HealthyProfiles { get; } = new();
    public ObservableRangeCollection<HomeServerViewModel> HomeServers { get; } = new();
    public Command<ConfigProfile> SelectHomeServerCommand { get; }
    public ObservableRangeCollection<SubscriptionSourceViewModel> SubscriptionSources { get; } = new();
    public ObservableRangeCollection<string> WhitelistWebsites { get; } = new();
    public ObservableRangeCollection<string> DirectRoutingEntries { get; } = new();
    public IReadOnlyList<string> DirectRoutingPreview => DirectRoutingEntries.Take(20).ToArray();
    public string DirectRoutingSummary => DirectRoutingEntries.Count > 20
        ? $"{DirectRoutingEntries.Count:N0} مورد شخصی؛ ۲۰ مورد اول نمایش داده می‌شود."
        : $"{DirectRoutingEntries.Count:N0} مورد در فهرست شخصی";
    public string DirectRoutingText { get => _directRoutingText; set => SetProperty(ref _directRoutingText, value); }
    public string RoutingFeedback { get => _routingFeedback; private set => SetProperty(ref _routingFeedback, value); }
    public string IranRoutingSummary => _iranRoutingSummary;
    public bool IranBypassEnabled
    {
        get => Settings.EnableIranBypass;
        set
        {
            if (Settings.EnableIranBypass == value || !CanSearchServers) return;
            Settings.EnableIranBypass = value;
            if (value) Settings.EnableWhitelistRouting = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LegacyWhitelistEnabled));
            OnPropertyChanged(nameof(LegacyRoutingAllowed));
            _ = RunSafeAsync(() => PersistSettingsAsync());
        }
    }
    public bool LegacyRoutingAllowed => !IranBypassEnabled && CanSearchServers;
    public bool LegacyWhitelistEnabled
    {
        get => Settings.EnableWhitelistRouting && !Settings.EnableIranBypass;
        set
        {
            if (!LegacyRoutingAllowed || Settings.EnableWhitelistRouting == value) return;
            Settings.EnableWhitelistRouting = value;
            OnPropertyChanged();
            _ = RunSafeAsync(() => PersistSettingsAsync());
        }
    }
    public ObservableRangeCollection<WhitelistApplication> WhitelistApplications { get; } = new();
    public IReadOnlyList<string> StatusFilters { get; } = new[] { "همه", "سالم", "TCP قابل دسترس", "ناموفق", "تست نشده", "پشتیبانی‌نشده" };
    public IReadOnlyList<string> ProtocolFilters { get; } = new[] { "همه", "VLESS", "VMess", "Trojan", "Shadowsocks" };
    public IReadOnlyList<string> SortOptions { get; } = new[] { "بهترین امتیاز", "جدیدترین اضافه‌شده", "قدیمی‌ترین اضافه‌شده", "کمترین Ping", "بیشترین Ping", "جدیدترین تست", "نام" };

    public AppSettings Settings { get => _settings; private set => SetProperty(ref _settings, value); }
    public ConfigProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value)) return;
            OnPropertyChanged(nameof(SelectedProfileSummary));
            if (!IsConnected && value is not null && SavedServerPolicy.IsReady(value) && !ReferenceEquals(_selectedHealthyProfile, value))
                SelectedHealthyProfile = value;
        }
    }
    public ConfigProfile? SelectedHealthyProfile
    {
        get => _selectedHealthyProfile;
        set
        {
            if (IsConnected && value?.Id != _activeProfileId) return;
            if (!SetProperty(ref _selectedHealthyProfile, value)) return;
            _homeNotice = "";
            _pingFeedback = "";
            if (value is not null) SelectedProfile = value;
            foreach (var row in HomeServers) row.RefreshSelection(row.Profile.Id == value?.Id);
            if (value is not null && Settings.SelectedServerId != value.Id)
            {
                Settings.SelectedServerId = value.Id;
                if (_initialized) _ = PersistHomeSelectionAsync();
                else if (_isInitializing) _saveInitialSelection = true;
            }
            OnPropertyChanged(nameof(SelectedHealthyPingText));
            OnPropertyChanged(nameof(HealthySelectionSummary));
            OnPropertyChanged(nameof(HasSelectedHealthyProfile));
            NotifyHomeChanged();
        }
    }
    public bool HasSelectedHealthyProfile => SelectedHealthyProfile is not null;
    private async Task PersistHomeSelectionAsync()
    {
        try { await _store.SaveSettingsAsync(Settings); }
        catch { _homeNotice = _store.LastStorageError; NotifyHomeChanged(); }
    }
    public string SelectedProfileSummary => SelectedProfile is null ? "کانفیگی انتخاب نشده" : $"{SelectedProfile.ProtocolText} • {SelectedProfile.Endpoint} • {SelectedProfile.HealthText} • {SelectedProfile.LatencyText}";
    public string SelectedHealthyPingText => SelectedHealthyProfile?.LatencyMs is int ms ? $"{ms} ms"
        : SelectedHealthyProfile?.LastSuccessfulLatencyMs is int previous ? $"قبلی: {previous} ms" : "—";
    public string HealthySelectionSummary => SelectedHealthyProfile is null
        ? "هنوز سرور سالمی انتخاب نشده است."
        : $"{SelectedHealthyProfile.ProtocolText} • {SelectedHealthyProfile.Endpoint} • آخرین Ping: {SelectedHealthyProfile.LatencyText}";
    public ConfigProfile? RecommendedHealthyProfile => _recommendedHealthyProfile;
    private bool RecommendedProfileIsCommunityOnly => RecommendedHealthyProfile is { Health: not ProfileHealth.Working } profile && IsTrustedCommunityCandidate(profile);
    public bool HasRecommendedProfile => RecommendedHealthyProfile is not null;
    public bool NoRecommendedProfile => !HasRecommendedProfile;
    public string RecommendedProfileName => RecommendedHealthyProfile?.DisplayName ?? "هنوز سرور پیشنهادی نداریم";
    public string RecommendedProfileScoreText => RecommendedHealthyProfile is null
        ? "بعد از تست سلامت یا دریافت داده جمعی ساخته می‌شود"
        : RecommendedProfileIsCommunityOnly
            ? $"{RecommendedHealthyProfile.QualitySummaryText} • پیشنهاد جمعی"
            : RecommendedHealthyProfile.QualitySummaryText;
    public string RecommendedProfileDetails => RecommendedHealthyProfile is null
        ? "از صفحه کانفیگ‌ها چند سرور سالم پیدا کن یا داده جمعی سرورها را از تنظیمات دریافت کن."
        : RecommendedProfileIsCommunityOnly
            ? $"{RecommendedHealthyProfile.ProtocolText} • {RecommendedHealthyProfile.Endpoint} • {RecommendedHealthyProfile.CommunityHealthText} • اتصال بعد از تست اینترنت تایید می‌شود"
            : $"{RecommendedHealthyProfile.ProtocolText} • {RecommendedHealthyProfile.Endpoint} • {RecommendedHealthyProfile.LatencyText}";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsNotBusy));
            RefreshConnectionActions();
        }
    }
    public bool IsNotBusy => !IsBusy;
    public Command FindServersCommand { get; }
    public Command PingSelectedServerCommand { get; }
    public Command CleanupOldServersCommand { get; }
    public Command ViewFailedServersCommand { get; }
    public string PendingCleanupSummary => $"{FailedProfiles:N0} سرور بی‌پاسخ در فهرست بررسی و پاک‌سازی";
    public string CleanupSummary { get => _cleanupSummary; private set => SetProperty(ref _cleanupSummary, value); }
    public bool CanPingSelectedServer => !_isInitializing && SelectedHealthyProfile is not null && IsNotBusy && !_findingServers && !IsConnectionBusy &&
        (!IsConnected || SelectedHealthyProfile.Id == _activeProfileId);
    public string PingButtonText => _isPinging ? "در حال اندازه‌گیری…" : "گرفتن پینگ";
    public string PingFeedback => _pingFeedback;
    public bool HasPingFeedback => _pingFeedback.Length > 0;
    public bool HasHomeServers => HomeServers.Count > 0;
    public bool CanSelectHomeServer => HasHomeServers && !IsConnected && !IsConnectionBusy && !_waitingForTests;
    public bool IsHomeBusy => _isLoadingHome || IsConnectionBusy || _isPinging || _findingServers || _waitingForTests;
    public string HomeConnectText => IsTesting || _findingServers ? "توقف تست و اتصال" : "اتصال";
    public bool ShowHomeConnectAction => !IsConnected && (HasHomeServers || IsConnectionBusy);
    public bool CanCancelHomeSearch => _findingServers || IsTesting;
    public bool CanSearchServers => CanStartConnection && !_findingServers;
    public bool CanConnectHome => !_isInitializing && !_waitingForTests && !IsConnected && !IsConnectionBusy &&
        (IsNotBusy || IsTesting || _findingServers) && SelectedHealthyProfile is not null && CanTunnel;
    public string SearchServersLabel => HasHomeServers ? "جست‌وجوی دوبارهٔ سرورها" : "پیدا کردن سرور";
    public string HomeConnectionTitle => IsConnectionBusy ? ConnectionBadgeText : IsConnected ? "متصل هستی" : "آمادهٔ اتصال";
    public string HomeSearchProgress => _connectedSearch
        ? $"{ProgressFullWorking:N0} از ۱۰ سرور آماده · {ProgressDone:N0} بررسی شد"
        : $"{ProgressDone:N0} بررسی شد · {ProgressFullWorking:N0} سرور سالم";
    public string HomeHint => !CanTunnel ? "اتصال روی این دستگاه پشتیبانی نمی‌شود."
        : _isPinging ? "در حال گرفتن پینگ همین سرور…"
        : _waitingForTests ? "در حال پایان‌دادن به تست برای اتصال…"
        : _isLoadingHome ? "در حال بازیابی سرورهای ذخیره‌شده…"
        : _connectedSearch ? "VPN متصل است؛ در حال پیدا کردن سرورهای جایگزین…"
        : _findingServers ? (IsTesting ? "در حال بررسی سرورها؛ کمی صبر کن." : "در حال دریافت سرورها…")
        : IsConnectionBusy ? "چند لحظه صبر کن…"
        : IsConnected ? (_homeNotice.Length > 0 ? _homeNotice : "برای پایان، قطع اتصال را بزن.")
        : _homeNotice.Length > 0 ? _homeNotice
        : HasHomeServers ? "سرور را انتخاب کن و اتصال را بزن."
        : _homeSearchCompleted ? "سرور سالمی پیدا نشد. اینترنت را بررسی کن و دوباره جست‌وجو کن."
        : "برای شروع، پیدا کردن سرور را بزن.";

    private void NotifyHomeChanged()
    {
        foreach (var name in new[] { nameof(HasHomeServers), nameof(CanSearchServers), nameof(CanConnectHome),
            nameof(SearchServersLabel), nameof(HomeConnectionTitle), nameof(HomeHint), nameof(HomeSearchProgress), nameof(CanCancelHomeSearch), nameof(ShowHomeConnectAction),
            nameof(CanPingSelectedServer), nameof(PingButtonText), nameof(PingFeedback), nameof(HasPingFeedback) })
            OnPropertyChanged(name);
        OnPropertyChanged(nameof(CanSelectHomeServer));
        OnPropertyChanged(nameof(IsHomeBusy));
        OnPropertyChanged(nameof(HomeConnectText));
        ConnectCommand?.ChangeCanExecute();
    }

    private async Task ConnectFromHomeAsync()
    {
        if (!CanConnectHome || SelectedHealthyProfile is not { } selected) return;
        if (IsTesting || _findingServers)
        {
            var completion = _findingServers ? _discoveryCompletion?.Task : _testCompletion?.Task;
            _waitingForTests = true;
            NotifyHomeChanged();
            CancelTest();
            try
            {
                if (completion is not null) await completion.WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (TimeoutException)
            {
                _homeNotice = "تست هنوز پایان نیافته؛ سرورها حفظ شده‌اند. چند لحظه بعد دوباره اتصال را بزن.";
                return;
            }
            finally { _waitingForTests = false; NotifyHomeChanged(); }
            SelectedHealthyProfile = Profiles.FirstOrDefault(p => p.Id == selected.Id) ?? SelectedHealthyProfile;
        }
        await RunSafeAsync(ConnectSelectedAsync);
    }

    private async Task PingSelectedServerAsync()
    {
        if (!CanPingSelectedServer || SelectedHealthyProfile is not { } profile) return;
        var wasConnected = IsConnected;
        var generation = _connectionGeneration;
        _isPinging = true;
        _pingFeedback = "";
        IsBusy = true;
        NotifyHomeChanged();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _pingCts = deadline;
            var result = wasConnected
                ? await _tunnel.TestCurrentConnectionAsync(Settings, deadline.Token)
                : await _tunnel.TestAsync(profile, Settings, deadline.Token);
            if (wasConnected && (!IsConnected || generation != _connectionGeneration)) return;
            var activeTunnelFailed = wasConnected && !result.Success;
#if ANDROID
            // A failing TUN ping may be a phone/VPN issue. Confirm server failure
            // using the isolated, physical-network probe before demoting it.
            if (activeTunnelFailed)
                result = await _tunnel.TestAsync(profile, Settings, deadline.Token);
#endif
            deadline.Token.ThrowIfCancellationRequested();
            if (wasConnected && (!IsConnected || generation != _connectionGeneration)) return;
            profile.LatencyMs = result.Success ? result.LatencyMs : null;
            profile.LastTested = DateTime.Now;
            profile.TestMessage = FormatTestDetails(result);
            if (result.Success)
            {
                profile.FailureCount = 0;
                profile.Health = result.Level == ValidationLevel.FullProxy ? ProfileHealth.Working : ProfileHealth.Reachable;
                SavedServerPolicy.RememberSuccess(profile);
            }
            else
            {
                profile.FailureCount++;
                profile.Health = ProfileHealth.Failed;
            }
            var feedback = result.Success && result.LatencyMs.HasValue
                ? activeTunnelFailed
                    ? "اتصال فعال پاسخ نداد، اما خود سرور در تست مستقیم سالم بود."
                    : "پینگ همین الان به‌روز شد."
                : result.Success ? "سرور پاسخ داد."
                : "سرور پاسخ نداد؛ از صفحهٔ اول کنار رفت و وارد فهرست بررسی و پاک‌سازی شد.";
            // Update the home list before persistence/cleanup, including selecting
            // a remaining server when the failed one was selected and VPN is off.
            RefreshStats();
            var removed = await ApplyAutomaticCleanupAsync();
            if (removed > 0) await _store.SaveProfilesAsync(Profiles);
            else await _store.SaveProfileAsync(profile, Profiles);
            RefreshFilters();
            RefreshStats();
            _pingFeedback = feedback;
            _homeNotice = result.Success ? "" : feedback;
        }
        catch (OperationCanceledException)
        {
            _pingFeedback = wasConnected && !IsConnected
                ? "VPN قطع شد؛ پینگ این سرور متوقف شد."
                : "تست متوقف شد یا زمان دریافت پاسخ تمام شد؛ سرور حفظ شد.";
        }
        finally
        {
            _isPinging = false;
            _pingCts = null;
            IsBusy = false;
            OnPropertyChanged(nameof(SelectedHealthyPingText));
            NotifyHomeChanged();
        }
    }

    private async Task<int> RemoveOldFailedProfilesAsync()
    {
        if (IsConnected && string.IsNullOrEmpty(_activeProfileId)) return 0;
        var removable = ProfileCleanupPolicy.FindRemovable(Profiles, DateTime.Now,
            IsConnected ? _activeProfileId : _waitingForTests ? SelectedHealthyProfile?.Id : null);
        if (removable.Count == 0) return 0;
        var ids = removable.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        Settings.SuppressedProfileIds = Settings.SuppressedProfileIds
            .Where(p => p.Value > now).ToDictionary(p => p.Key, p => p.Value);
        foreach (var id in ids) Settings.SuppressedProfileIds[id] = now.AddDays(7);
        // Persist suppression first, so a refresh cannot immediately re-add the
        // same dead entries. Explicit clipboard imports remain available.
        await _store.SaveSettingsAsync(Settings);
        Profiles.ReplaceRange(Profiles.Where(p => !ids.Contains(p.Id)).ToList());
        if (SelectedProfile is { } selected && ids.Contains(selected.Id)) SelectedProfile = null;
        RefreshFilters();
        RefreshStats();
        CleanupSummary = $"{removable.Count:N0} سرور قدیمی و ناموفق پاک شد.";
        return removable.Count;
    }

    private async Task<int> ApplyAutomaticCleanupAsync()
    {
        return Settings.AutoCleanupOldServers ? await RemoveOldFailedProfilesAsync() : 0;
    }

    private async Task PersistFinishedTestRunAsync()
    {
        try
        {
            await ApplyAutomaticCleanupAsync();
            await _store.SaveProfilesAsync(Profiles);
        }
        finally { IsTesting = false; IsBusy = false; }
    }

    private async Task CleanupOldServersAsync()
    {
        IsBusy = true;
        try
        {
            var removed = await RemoveOldFailedProfilesAsync();
            if (removed > 0) await _store.SaveProfilesAsync(Profiles);
            else CleanupSummary = "موردی برای حذف نیست؛ پاک‌سازی به ۳ شکست پیاپی، عمر ۷ روز و یک تست سالم اخیر نیاز دارد.";
        }
        finally { IsBusy = false; }
    }

    private async Task FindServersAsync()
    {
        if (!CanSearchServers) return;
        _findingServers = true;
        _discoveryCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _homeNotice = "";
        using var discovery = new CancellationTokenSource();
        discovery.CancelAfter(TimeSpan.FromMinutes(2));
        _discoveryCts = discovery;
        NotifyHomeChanged();
        try
        {
            var enabledSources = SubscriptionSources.Where(source => source.IsEnabled).ToList();
            var recentSources = enabledSources.Count > 0 && enabledSources.All(source =>
                source.Source.LastFetchedUtc >= DateTime.UtcNow.AddMinutes(-10));
            var expiredSuppression = Settings.SuppressedProfileIds.Values.Any(until => until <= DateTime.UtcNow);
            var recentWorkingSet = Profiles.Count(p => p.Health == ProfileHealth.Working &&
                p.LastTested >= DateTime.Now.AddMinutes(-30)) >= SimpleSearchHealthyTarget;
            if (Profiles.Count == 0 || (!recentSources && !recentWorkingSet) || expiredSuppression)
                await GetConfigAsync(discovery.Token);
            discovery.Token.ThrowIfCancellationRequested();
            var started = DateTime.Now;
            var candidates = ConfigTestPlanner.OrderForHealthySearch(Profiles.Where(p => p.Health != ProfileHealth.Unsupported));
            await TestProfilesAsync(candidates, guidedHealthySearch: true, simpleSearch: true);
            SelectedHealthyProfile = HealthyProfiles.Where(p => p.LastTested >= started)
                .OrderBy(p => p.LatencyMs ?? int.MaxValue).FirstOrDefault() ?? SelectedHealthyProfile;
            _homeSearchCompleted = true;
            if (discovery.IsCancellationRequested && !HasHomeServers)
                _homeNotice = "جست‌وجو متوقف شد. برای بررسی سرورهای بیشتر دوباره جست‌وجو کن.";
        }
        catch (OperationCanceledException)
        {
            _homeNotice = "جست‌وجو متوقف شد. هر وقت خواستی دوباره شروع کن.";
        }
        finally
        {
            _discoveryCts = null;
            _findingServers = false;
            _discoveryCompletion?.TrySetResult();
            NotifyHomeChanged();
        }
    }
    public bool IsConnectionBusy => _connectionUiPhase != ConnectionUiPhase.Idle;
    public bool IsDisconnected => !IsConnected && !IsConnectionBusy;
    public bool ShowConnectAction => !IsConnected;
    public bool ShowDisconnectAction => IsConnected;
    public bool ShowConnectionTools => !IsConnected && !IsConnectionBusy;
    public bool CanStartConnection => !_isInitializing && IsNotBusy && !IsConnected && !IsConnectionBusy;
    public bool CanStopConnection => !_isInitializing && (IsNotBusy || _connectedSearch || _isPinging) && IsConnected && !IsConnectionBusy;
    public string ConnectionBadgeText => _connectionUiPhase switch
        {
            ConnectionUiPhase.Connecting => "در حال اتصال",
            ConnectionUiPhase.Validating => "در حال تست اینترنت",
            ConnectionUiPhase.Disconnecting => "در حال قطع اتصال",
            _ => IsConnected ? "متصل و تأییدشده" : "قطع"
        };
    public bool IsTesting { get => _isTesting; private set { if (SetProperty(ref _isTesting, value)) NotifyHomeChanged(); } }
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetProperty(ref _isConnected, value)) return;
            _connectionGeneration++;
            if (!value && _connectedSearch) CancelTest();
            if (!value && _isPinging) _pingCts?.Cancel();
            if (!value && SelectedHealthyProfile is { } selected && !SavedServerPolicy.IsReady(selected))
                RefreshHealthyProfiles();
            RefreshConnectionActions();
        }
    }
    public bool ShowAdvancedConfigTools
    {
        get => _showAdvancedConfigTools;
        private set
        {
            if (!SetProperty(ref _showAdvancedConfigTools, value)) return;
            OnPropertyChanged(nameof(ShowConfigWorkflow));
            OnPropertyChanged(nameof(AdvancedConfigToolsText));
            OnPropertyChanged(nameof(HasMoreProfiles));
            OnPropertyChanged(nameof(VisibleProfilesLabel));
        }
    }
    public bool ShowConfigWorkflow => !ShowAdvancedConfigTools;
    public string AdvancedConfigToolsText => ShowAdvancedConfigTools ? "بستن فهرست پیشرفته" : "نمایش فهرست و فیلترهای پیشرفته";
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string BackendTitle => $"{_tunnel.Capabilities.PlatformName} • {_tunnel.Capabilities.BackendName}";
    public string BackendNote => _tunnel.Capabilities.Note;
    public bool CanTunnel => _tunnel.Capabilities.SupportsTunnel;
    public bool CannotTunnel => !CanTunnel;
    public bool CanAppWhitelist => _tunnel.Capabilities.SupportsApplicationWhitelist;
    public bool IsWindows => DeviceInfo.Platform == DevicePlatform.WinUI;
    public bool IsAndroid => DeviceInfo.Platform == DevicePlatform.Android;
    public string ConnectionStatusMessage { get => _connectionStatusMessage; private set => SetProperty(ref _connectionStatusMessage, value); }
    public string CommunityHealthStatusMessage { get => _communityHealthStatusMessage; private set => SetProperty(ref _communityHealthStatusMessage, value); }
    public bool QuickMode
    {
        get => Settings.QuickMode;
        set
        {
            if (Settings.QuickMode == value) return;
            Settings.QuickMode = value;
            ShowAdvancedConfigTools = !value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConfigModeText));
            OnPropertyChanged(nameof(ConfigModeHint));
            RefreshFilters();
            _ = PersistSettingsAsync();
        }
    }
    public bool AutoReconnect
    {
        get => Settings.AutoReconnect;
        set
        {
            if (Settings.AutoReconnect == value) return;
            Settings.AutoReconnect = value;
            OnPropertyChanged();
            RefreshDiagnosticsReport();
            _ = PersistSettingsAsync();
        }
    }
    public int AutoReconnectAttempts
    {
        get => Settings.AutoReconnectAttempts;
        set
        {
            var normalized = Math.Clamp(value <= 0 ? 3 : value, 1, 5);
            if (Settings.AutoReconnectAttempts == normalized) return;
            Settings.AutoReconnectAttempts = normalized;
            OnPropertyChanged();
            RefreshDiagnosticsReport();
            _ = PersistSettingsAsync();
        }
    }
    public string ConfigModeText => QuickMode ? "حالت سریع" : "حالت حرفه‌ای";
    public string ConfigModeHint => QuickMode
        ? "برنامه مسیر اصلی را ساده نگه می‌دارد و ابزارهای سنگین را پشت جزئیات می‌گذارد."
        : "فیلترها، انتخاب دستی و لیست کامل برای بررسی دقیق باز می‌ماند.";
    public string ReleaseVersionText => $"SaePar Tunnel {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";
    public string RuntimeSummaryText => $"{DeviceInfo.Platform} • {DeviceInfo.Model} • {DeviceInfo.VersionString}";
    public string DiagnosticsReport { get => _diagnosticsReport; private set => SetProperty(ref _diagnosticsReport, value); }
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

    public int TotalProfiles => _totalProfiles;
    public int WorkingProfiles => _workingProfiles;
    public int ReachableProfiles => _reachableProfiles;
    public int FailedProfiles => _failedProfiles;
    public int UntestedProfiles => _untestedProfiles;
    public int HealthyProfilesCount => HealthyProfiles.Count;
    public string HealthyProfilesCountText => $"{HealthyProfilesCount:N0} سالم";
    public string PrimaryConfigFlowHint => TotalProfiles == 0
        ? "بدون کانفیگ"
        : WorkingProfiles == 0
            ? "بدون کانفیگ Full-Test سالم"
            : $"{WorkingProfiles:N0} کانفیگ Full-Test سالم آماده اتصال است.";
    public string ConfigFlowNextStep => TotalProfiles == 0
        ? "اول کانفیگ‌ها را از Subscription یا Clipboard وارد کن."
        : WorkingProfiles == 0
            ? "حالا تست سلامت را اجرا کن تا چند سرور قابل اعتماد پیدا شود."
            : "بهترین سرور آماده است؛ می‌توانی از خانه با اتصال سریع وصل شوی.";
    public string ConfigFlowHealthSummary => $"{TotalProfiles:N0} کل • {WorkingProfiles:N0} سالم • {FailedProfiles:N0} ناموفق • {UntestedProfiles:N0} تست‌نشده";
    public int FilteredCount => _filteredSnapshot.Count;
    public int VisibleCount => FilteredProfiles.Count;
    public bool HasMoreProfiles => ShowAdvancedConfigTools && VisibleCount < FilteredCount;
    public string VisibleProfilesLabel => ShowAdvancedConfigTools
        ? HasMoreProfiles ? $"نمایش {VisibleCount:N0} از {FilteredCount:N0}" : $"نمایش {FilteredCount:N0}"
        : $"{FilteredCount:N0} کانفیگ آماده بررسی";

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
    public string NewSubscriptionName { get => _newSubscriptionName; set => SetProperty(ref _newSubscriptionName, value ?? string.Empty); }
    public string NewSubscriptionUrl { get => _newSubscriptionUrl; set => SetProperty(ref _newSubscriptionUrl, value ?? string.Empty); }
    public bool IsSubscriptionEditorExpanded
    {
        get => _isSubscriptionEditorExpanded;
        private set
        {
            if (!SetProperty(ref _isSubscriptionEditorExpanded, value)) return;
            OnPropertyChanged(nameof(SubscriptionEditorGlyph));
        }
    }
    public string SubscriptionEditorGlyph => IsSubscriptionEditorExpanded ? "−" : "+";
    public string SubscriptionSummaryText => $"{SubscriptionSources.Count(x => x.IsEnabled):N0} منبع فعال از {SubscriptionSources.Count:N0}";

    public Command GetConfigCommand { get; }
    public Command<SubscriptionSourceViewModel> FetchSubscriptionCommand { get; }
    public Command<SubscriptionSourceViewModel> ToggleSubscriptionCommand { get; }
    public Command ToggleSubscriptionEditorCommand { get; }
    public Command AddSubscriptionCommand { get; }
    public Command<SubscriptionSourceViewModel> RemoveSubscriptionCommand { get; }
    public Command<ConfigProfile> ShareProfileCommand { get; }
    public Command RefreshCommunityHealthCommand { get; }
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
    public Command AddDirectRoutesCommand { get; }
    public Command ImportDirectRoutesCommand { get; }
    public Command<string> RemoveDirectRouteCommand { get; }
    public Command ClearDirectRoutesCommand { get; }
    public Command AddApplicationCommand { get; }
    public Command BrowseApplicationCommand { get; }
    public Command OpenAndroidVpnSettingsCommand { get; }
    public Command OpenAndroidNotificationSettingsCommand { get; }
    public Command<WhitelistApplication> RemoveApplicationCommand { get; }
    public Command ResetFiltersCommand { get; }
    public Command ToggleAdvancedConfigToolsCommand { get; }
    public Command LoadMoreCommand { get; }
    public Command RefreshDiagnosticsCommand { get; }
    public Command CopyDiagnosticsCommand { get; }
    public Command DiagnoseConnectionCommand { get; }

    private void RefreshConnectionActions()
    {
        NotifyHomeChanged();
        OnPropertyChanged(nameof(IsConnectionBusy));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(ShowConnectAction));
        OnPropertyChanged(nameof(ShowDisconnectAction));
        OnPropertyChanged(nameof(ShowConnectionTools));
        OnPropertyChanged(nameof(CanStartConnection));
        OnPropertyChanged(nameof(LegacyRoutingAllowed));
        OnPropertyChanged(nameof(CanStopConnection));
        OnPropertyChanged(nameof(ConnectionBadgeText));
        ConnectCommand?.ChangeCanExecute();
        ConnectBestCommand?.ChangeCanExecute();
        DisconnectCommand?.ChangeCanExecute();
    }

    private void SetConnectionUiPhase(ConnectionUiPhase phase)
    {
        if (_connectionUiPhase == phase) return;
        _connectionUiPhase = phase;
        RefreshConnectionActions();
    }

    private void NotifyRecommendedProfileChanged()
    {
        OnPropertyChanged(nameof(RecommendedHealthyProfile));
        OnPropertyChanged(nameof(HasRecommendedProfile));
        OnPropertyChanged(nameof(NoRecommendedProfile));
        OnPropertyChanged(nameof(RecommendedProfileName));
        OnPropertyChanged(nameof(RecommendedProfileScoreText));
        OnPropertyChanged(nameof(RecommendedProfileDetails));
    }

    public Task InitializeAsync()
    {
        if (_initialized) return Task.CompletedTask;
        if (_initializationTask is { IsCompleted: false }) return _initializationTask;
        if (IsBusy) return Task.CompletedTask;
        return _initializationTask = InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        _isInitializing = true;
        _isLoadingHome = true;
        RefreshConnectionActions();
        var initialized = false;
        var loadErrors = new List<string>();
        StatusMessage = "در حال بارگذاری تنظیمات و کانفیگ‌ها...";
        try
        {
            _store.EnsureCreated();
            try { Settings = await _store.LoadSettingsAsync(); }
            catch { loadErrors.Add(_store.LastStorageError); }
            // Publish a tiny home snapshot before reading the full server archive.
            try
            {
                var home = await _store.LoadHomeServersAsync();
                if (home.Count > 0)
                {
                    Profiles.ReplaceRange(home);
                    IsConnected = _tunnel.IsConnected;
                    RefreshStats();
                    _isLoadingHome = false;
                    NotifyHomeChanged();
                }
            }
            catch { /* Optional cache errors do not block the full archive. */ }
            // Restore the server list even when the independent settings file is damaged.
            try
            {
                Profiles.ReplaceRange(await _store.LoadProfilesAsync());
            }
            catch { loadErrors.Add(_store.LastStorageError); }
            RefreshStats();
            _isLoadingHome = false;
            NotifyHomeChanged();
            DirectRoutingEntries.ReplaceRange(Settings.DirectRoutingEntries);
            RefreshRoutingList();
            OnPropertyChanged(nameof(IranBypassEnabled));
            OnPropertyChanged(nameof(IranRoutingSummary));
            OnPropertyChanged(nameof(LegacyWhitelistEnabled));
            OnPropertyChanged(nameof(LegacyRoutingAllowed));
            LoadSubscriptionSources();
            ShowAdvancedConfigTools = !Settings.QuickMode;
            if (Settings.TestConcurrency <= 0)
                Settings.TestConcurrency = DeviceInfo.Platform == DevicePlatform.WinUI ? 24 : DeviceInfo.Platform == DevicePlatform.Android ? 6 : 4;
            Settings.TestConcurrency = Math.Clamp(Settings.TestConcurrency, 1, DeviceInfo.Platform == DevicePlatform.WinUI ? 64 : 12);
            if (Settings.ProbePort <= 1024 || Settings.ProbePort > 65535) Settings.ProbePort = 10810;
            Settings.AutoReconnectAttempts = Math.Clamp(Settings.AutoReconnectAttempts <= 0 ? 3 : Settings.AutoReconnectAttempts, 1, 5);
            OnPropertyChanged(nameof(QuickMode));
            OnPropertyChanged(nameof(AutoReconnect));
            OnPropertyChanged(nameof(AutoReconnectAttempts));
            OnPropertyChanged(nameof(ConfigModeText));
            OnPropertyChanged(nameof(ConfigModeHint));
            RefreshCommunityHealthStatusMessage();

            _visibleLimit = InitialVisibleLimit();
            WhitelistWebsites.ReplaceRange(Settings.WhitelistWebsites.Distinct(StringComparer.OrdinalIgnoreCase));
            WhitelistApplications.ReplaceRange(Settings.WhitelistApplications);
            RefreshFilters();
            RefreshStats();
            RefreshCommunityHealthStatusMessage();
            string? initialConnectionMessage = null;
            if (DeviceInfo.Platform == DevicePlatform.iOS)
            {
                try
                {
                    await _tunnel.EnsureReadyAsync(Settings);
                    if (_tunnel.IsConnected)
                    {
                        StatusMessage = "در حال تأیید اتصال قبلی iOS...";
                        var validation = await _tunnel.TestCurrentConnectionAsync(Settings);
                        IsConnected = validation.Success && validation.Level == ValidationLevel.FullProxy;
                        if (IsConnected)
                        {
                            initialConnectionMessage = $"✓ اینترنت از VPN iOS تأیید شد • {validation.Message}";
                        }
                        else
                        {
                            try { await _tunnel.DisconnectAsync(Settings); } catch { }
                            initialConnectionMessage = "اتصال قبلی iOS اینترنت سالم نداشت و قطع شد.";
                        }
                    }
                }
                catch (Exception ex)
                {
                    IsConnected = false;
                    initialConnectionMessage = "راه‌اندازی VPN iOS آماده نیست: " + HumanizeException(ex);
                }
            }
            else
            {
#if ANDROID
                _activeProfileId = AndroidVpnRuntime.ConnectedProfileId;
                if (_tunnel.IsConnected)
                    SelectedHealthyProfile = Profiles.FirstOrDefault(p => p.Id == _activeProfileId);
#endif
                IsConnected = _tunnel.IsConnected;
            }

            StatusMessage = IsConnected ? $"VPN فعال • {BackendTitle}" : $"آماده • {BackendTitle}";
            if (!string.IsNullOrWhiteSpace(initialConnectionMessage))
                ConnectionStatusMessage = initialConnectionMessage;
            else if (DeviceInfo.Platform == DevicePlatform.Android)
                ConnectionStatusMessage = IsConnected ? "VPN Android فعال است." : "آماده برای اتصال؛ اگر مجوز قبلاً داده شده باشد Android پنجره مجوز را دوباره نشان نمی‌دهد.";
            else if (DeviceInfo.Platform == DevicePlatform.iOS)
                ConnectionStatusMessage = "آماده برای اتصال؛ iOS در اولین اتصال مجوز افزودن VPN را نمایش می‌دهد.";
            RefreshDiagnosticsReport();
            initialized = loadErrors.Count == 0;
            if (initialized)
            {
                if (_saveInitialSelection)
                {
                    await _store.SaveSettingsAsync(Settings);
                    _saveInitialSelection = false;
                }
                try { await _store.SaveHomeServersAsync(Profiles); }
                catch { /* The full archive remains authoritative if fast caching fails. */ }
            }
            _ = LoadIranRoutingSummaryAsync();
        }
        catch (Exception)
        {
            StatusMessage = "خواندن داده‌های ذخیره‌شده انجام نشد؛ فهرست قبلی پاک نشده است.";
            _homeNotice = _store.LastStorageError.Length > 0 ? _store.LastStorageError : StatusMessage;
            ConnectionStatusMessage = _homeNotice;
            RefreshDiagnosticsReport();
            NotifyHomeChanged();
        }
        finally
        {
            _initialized = initialized;
            _isInitializing = false;
            _isLoadingHome = false;
            if (loadErrors.Count > 0)
            {
                _homeNotice = string.Join("\n", loadErrors);
                ConnectionStatusMessage = _homeNotice;
                NotifyHomeChanged();
            }
            RefreshConnectionActions();
        }
    }

    private async Task LoadIranRoutingSummaryAsync()
    {
        try
        {
            var summary = await Task.Run(() => $"{IranRoutingCatalog.Default.DirectDomains.Length:N0} دامنه و {IranRoutingCatalog.Default.IpRanges.Length:N0} محدودهٔ IP در فهرست پیش‌فرض");
            _iranRoutingSummary = summary;
            OnPropertyChanged(nameof(IranRoutingSummary));
        }
        catch { /* Routing construction reports a missing catalog when actually needed. */ }
    }

    private Task GetConfigAsync() => GetConfigAsync(CancellationToken.None);

    private async Task GetConfigAsync(CancellationToken cancellationToken)
    {
        var activeSources = SubscriptionSources.Where(x => x.IsEnabled).ToList();
        if (activeSources.Count == 0)
        {
            StatusMessage = "حداقل یک منبع اشتراک را فعال کن.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"در حال دریافت از {activeSources.Count:N0} منبع فعال...";
        try
        {
            var existing = Profiles
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var failures = new List<string>();
            var added = 0;
            var unchanged = 0;
            var succeeded = 0;

            using var fetchGate = new SemaphoreSlim(2, 2);
            async Task FetchSourceAsync(SubscriptionSourceViewModel source)
            {
                await fetchGate.WaitAsync(cancellationToken);
                try
                {
                    StatusMessage = $"در حال دریافت • {source.Name}";
                    // Await continuations stay on the UI thread, where source
                    // metadata, profile merging and aggregate counters are updated.
                    var outcome = await FetchSubscriptionCoreAsync(source, existing, cancellationToken);
                    added += outcome.Added;
                    unchanged += outcome.NotModified ? 1 : 0;
                    succeeded++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"{source.Name}: {HumanizeException(ex)}");
                }
                finally { fetchGate.Release(); }
            }
            await Task.WhenAll(activeSources.Select(FetchSourceAsync));
            if (succeeded > 0)
            {
                await _store.SaveProfilesAsync(Profiles);
                await PersistSubscriptionSettingsAsync();
                if (!_findingServers) await TryRefreshCommunityHealthAfterConfigAsync();
                RefreshFilters();
                RefreshStats();
            }

            StatusMessage = failures.Count == 0
                ? $"دریافت کامل شد • {added:N0} کانفیگ جدید • {unchanged:N0} منبع بدون تغییر"
                : $"{succeeded:N0} منبع دریافت شد و {failures.Count:N0} منبع خطا داشت • {added:N0} کانفیگ جدید";

            if (failures.Count > 0 && Shell.Current is not null && !_findingServers)
            {
                var details = string.Join("\n", failures.Take(5));
                await Shell.Current.DisplayAlert("گزارش دریافت Subscription", details, "باشه");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task FetchSubscriptionAsync(SubscriptionSourceViewModel source)
    {
        IsBusy = true;
        StatusMessage = $"در حال دریافت از {source.Name}...";
        try
        {
            var existing = Profiles
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var outcome = await FetchSubscriptionCoreAsync(source, existing);
            await _store.SaveProfilesAsync(Profiles);
            await PersistSubscriptionSettingsAsync();
            await TryRefreshCommunityHealthAfterConfigAsync();
            RefreshFilters();
            RefreshStats();
            StatusMessage = outcome.NotModified
                ? $"{source.Name} تغییری نداشت • {outcome.Host} • {outcome.Route}"
                : $"{source.Name} دریافت شد • {outcome.Added:N0} کانفیگ جدید • {outcome.Host} • {outcome.Route}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<SubscriptionFetchOutcome> FetchSubscriptionCoreAsync(
        SubscriptionSourceViewModel item,
        IDictionary<string, ConfigProfile> existing,
        CancellationToken cancellationToken = default)
    {
        var source = item.Source;
        var expiredIds = Settings.SuppressedProfileIds.Where(entry => entry.Value <= DateTime.UtcNow)
            .Select(entry => entry.Key).ToArray();
        var expiredSuppression = expiredIds.Length > 0;
        if (expiredSuppression)
        {
            foreach (var subscription in SubscriptionSources) subscription.Source.ETag = string.Empty;
            Settings.GitHubETag = string.Empty;
            foreach (var id in expiredIds) Settings.SuppressedProfileIds.Remove(id);
        }
        var result = await _github.FetchAsync(source.Url, existing.Count == 0 || expiredSuppression ? null : source.ETag, cancellationToken);

        var host = Uri.TryCreate(result.SourceUrl, UriKind.Absolute, out var uri) ? uri.Host : "source";
        var route = result.UsedDirectConnection ? "direct fallback" : "system route";
        if (result.NotModified)
        {
            source.LastFetchedUtc = DateTime.UtcNow;
            item.RefreshFetchMetadata();
            return new SubscriptionFetchOutcome(0, true, host, route);
        }

        StatusMessage = $"{item.Name} دریافت شد؛ در حال پردازش...";
        var extracted = result.Profiles;
        if (extracted.Count == 0)
            throw new InvalidDataException("Subscription دریافت شد، اما هیچ کانفیگ پشتیبانی‌شده‌ای در آن پیدا نشد.");

        var additions = new List<ConfigProfile>();
        foreach (var profile in extracted)
        {
            if (Settings.SuppressedProfileIds.TryGetValue(profile.Id, out var until) && until > DateTime.UtcNow)
                continue;
            profile.Source = $"Subscription: {item.Name}";
            if (existing.TryGetValue(profile.Id, out var old))
            {
                old.LastSeen = DateTime.Now;
                continue;
            }

            additions.Add(profile);
            existing[profile.Id] = profile;
        }

        Profiles.AddRange(additions);
        // Commit validators only after a usable body has been imported. A mirror's
        // validator is not valid for the original URL on the next refresh.
        source.ETag = string.Equals(result.SourceUrl, source.Url, StringComparison.OrdinalIgnoreCase)
            ? result.ETag : string.Empty;
        source.LastFetchedUtc = DateTime.UtcNow;
        item.RefreshFetchMetadata();
        return new SubscriptionFetchOutcome(additions.Count, false, host, route);
    }

    private void LoadSubscriptionSources()
    {
        SubscriptionSources.ReplaceRange(Settings.Subscriptions.Select((source, index) =>
            new SubscriptionSourceViewModel(source, OnSubscriptionSourceChanged, isExpanded: index == 0)));
        OnPropertyChanged(nameof(SubscriptionSummaryText));
    }

    private void ToggleSubscriptionEditor() => IsSubscriptionEditorExpanded = !IsSubscriptionEditorExpanded;

    private async Task AddSubscriptionAsync()
    {
        if (!SubscriptionCatalog.TryNormalizeHttpsUrl(NewSubscriptionUrl, out var url))
            throw new InvalidOperationException("آدرس Subscription باید یک URL کامل و امن با https:// باشد.");

        var existing = SubscriptionSources.FirstOrDefault(x => SubscriptionCatalog.UrlsEqual(x.Url, url));
        if (existing is not null)
        {
            existing.Expand();
            existing.IsEnabled = true;
            NewSubscriptionName = string.Empty;
            NewSubscriptionUrl = string.Empty;
            IsSubscriptionEditorExpanded = false;
            StatusMessage = "این Subscription از قبل در فهرست وجود دارد.";
            return;
        }

        var name = NewSubscriptionName.Trim();
        if (name.Length == 0 && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            name = uri.Host;

        var source = new SubscriptionSource
        {
            Name = name.Length == 0 ? "اشتراک شخصی" : name,
            Url = url,
            IsEnabled = true
        };
        var item = new SubscriptionSourceViewModel(source, OnSubscriptionSourceChanged, isExpanded: true);
        SubscriptionSources.Add(item);
        NewSubscriptionName = string.Empty;
        NewSubscriptionUrl = string.Empty;
        IsSubscriptionEditorExpanded = false;
        await PersistSubscriptionSettingsAsync();
        OnPropertyChanged(nameof(SubscriptionSummaryText));
        StatusMessage = $"Subscription «{source.Name}» اضافه شد؛ برای دریافت، دکمه همان منبع یا دریافت از همه را بزن.";
    }

    private async Task RemoveSubscriptionAsync(SubscriptionSourceViewModel item)
    {
        if (!item.CanRemove)
        {
            StatusMessage = "منبع پیش‌فرض قابل حذف نیست؛ می‌توانی آن را غیرفعال کنی.";
            return;
        }

        var confirmed = Shell.Current is null ||
                        await Shell.Current.DisplayAlert(
                            "حذف Subscription",
                            $"منبع «{item.Name}» حذف شود؟ کانفیگ‌هایی که قبلاً دریافت شده‌اند باقی می‌مانند.",
                            "حذف",
                            "انصراف");
        if (!confirmed) return;

        SubscriptionSources.Remove(item);
        await PersistSubscriptionSettingsAsync();
        OnPropertyChanged(nameof(SubscriptionSummaryText));
        StatusMessage = $"Subscription «{item.Name}» حذف شد.";
    }

    private void OnSubscriptionSourceChanged()
    {
        OnPropertyChanged(nameof(SubscriptionSummaryText));
        _ = PersistSubscriptionSettingsSafelyAsync();
    }

    private async Task PersistSubscriptionSettingsSafelyAsync()
    {
        try
        {
            await PersistSubscriptionSettingsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "ذخیره وضعیت Subscription ناموفق بود: " + HumanizeException(ex);
        }
    }

    private async Task PersistSubscriptionSettingsAsync()
    {
        SyncSubscriptionSettings();
        await _store.SaveSettingsAsync(Settings);
        RefreshDiagnosticsReport();
    }

    private void SyncSubscriptionSettings()
    {
        Settings.Subscriptions = SubscriptionSources.Select(x => x.Source).ToList();
        var builtIn = Settings.Subscriptions.FirstOrDefault(x => x.IsBuiltIn);
        if (builtIn is null) return;
        Settings.GitHubSubscriptionUrl = builtIn.Url;
        Settings.GitHubETag = builtIn.ETag;
        Settings.LastGitHubFetchUtc = builtIn.LastFetchedUtc;
    }

    private async Task ShareProfileSafelyAsync(ConfigProfile profile)
    {
        try
        {
            await ShareProfileAsync(profile);
        }
        catch (Exception ex)
        {
            var message = HumanizeException(ex);
            StatusMessage = "اشتراک‌گذاری انجام نشد: " + message;
            if (Shell.Current is not null)
                await Shell.Current.DisplayAlert("اشتراک‌گذاری انجام نشد", message, "باشه");
        }
    }

    private async Task ShareProfileAsync(ConfigProfile profile)
    {
        var shareText = (profile.OriginalUri ?? string.Empty).Trim();
        if (shareText.Length == 0)
            throw new InvalidOperationException("لینک اصلی این کانفیگ برای اشتراک‌گذاری موجود نیست.");
        if (Shell.Current is null)
            throw new InvalidOperationException("صفحه اشتراک‌گذاری در دسترس نیست.");

        const string shareTextChoice = "ارسال به‌صورت متن";
        const string copyTextChoice = "کپی متن";
        const string showQrChoice = "نمایش و ارسال QR";
        var choice = await Shell.Current.DisplayActionSheet(
            $"اشتراک {profile.DisplayName}",
            "انصراف",
            null,
            shareTextChoice,
            copyTextChoice,
            showQrChoice);

        switch (choice)
        {
            case shareTextChoice:
                await ShareProfileTextAsync(profile, shareText);
                break;
            case copyTextChoice:
                await Clipboard.Default.SetTextAsync(shareText);
                StatusMessage = $"متن کانفیگ «{profile.DisplayName}» کپی شد.";
                break;
            case showQrChoice:
                StatusMessage = "در حال ساخت QR کانفیگ...";
                byte[] qrPng;
                try
                {
                    qrPng = await Task.Run(() => _qrCode.CreatePng(shareText));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "ساخت QR برای این کانفیگ ممکن نشد؛ احتمالاً متن لینک از ظرفیت یک QR بیشتر است.",
                        ex);
                }

                var details = $"{profile.ProtocolText} • {profile.Endpoint}";
                await Shell.Current.Navigation.PushModalAsync(
                    new ProfileSharePage(profile.DisplayName, details, shareText, qrPng));
                StatusMessage = $"QR کانفیگ «{profile.DisplayName}» آماده است.";
                break;
        }
    }

    private async Task ShareProfileTextAsync(ConfigProfile profile, string shareText)
    {
        try
        {
            await Share.Default.RequestAsync(new ShareTextRequest
            {
                Title = $"کانفیگ {profile.DisplayName}",
                Text = shareText
            });
        }
        catch (Exception)
        {
            await Clipboard.Default.SetTextAsync(shareText);
            StatusMessage = "اشتراک سیستمی در دسترس نبود؛ متن کانفیگ کپی شد.";
            if (Shell.Current is not null)
                await Shell.Current.DisplayAlert("متن کپی شد", StatusMessage, "باشه");
        }
    }

    private async Task TryRefreshCommunityHealthAfterConfigAsync()
    {
        if (!Settings.EnableCommunityHealth || string.IsNullOrWhiteSpace(Settings.CommunityHealthIndexUrl))
        {
            RefreshCommunityHealthStatusMessage();
            return;
        }

        try
        {
            await RefreshCommunityHealthAsync(silent: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CommunityHealthStatusMessage = "داده جمعی فعلا دریافت نشد: " + HumanizeException(ex);
            RefreshDiagnosticsReport();
        }
    }

    private async Task RefreshCommunityHealthAsync(bool silent = false)
    {
        if (!Settings.EnableCommunityHealth)
        {
            CommunityHealthStatusMessage = "داده جمعی خاموش است.";
            if (!silent) StatusMessage = CommunityHealthStatusMessage;
            return;
        }

        if (string.IsNullOrWhiteSpace(Settings.CommunityHealthIndexUrl))
        {
            CommunityHealthStatusMessage = "برای داده جمعی، آدرس HTTPS فایل ranked-profiles.json را وارد کن.";
            if (!silent) StatusMessage = CommunityHealthStatusMessage;
            return;
        }

        if (!silent)
        {
            IsBusy = true;
            StatusMessage = "در حال دریافت داده جمعی سرورها...";
        }

        try
        {
            var previousETag = silent ? Settings.CommunityHealthETag : null;
            var result = await _communityHealth.FetchAsync(Settings.CommunityHealthIndexUrl, previousETag);
            Settings.LastCommunityHealthFetchUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(result.ETag))
                Settings.CommunityHealthETag = result.ETag;

            var applied = result.NotModified
                ? Profiles.Count(x => x.HasCommunityHealth)
                : ApplyCommunityHealth(result.Index, result.SourceUrl);

            if (!result.NotModified)
                await _store.SaveProfilesAsync(Profiles);

            await _store.SaveSettingsAsync(Settings);
            RefreshFilters();
            RefreshStats();

            var host = Uri.TryCreate(result.SourceUrl, UriKind.Absolute, out var uri) ? uri.Host : "community";
            var route = result.UsedDirectConnection ? "direct fallback" : "system route";
            CommunityHealthStatusMessage = result.NotModified
                ? $"داده جمعی تغییری نکرد • {applied:N0} کانفیگ دارای امتیاز • {host} • {route}"
                : $"داده جمعی اعمال شد • {applied:N0} کانفیگ امتیاز گرفت • {host} • {route}";
            if (!silent) StatusMessage = CommunityHealthStatusMessage;
            RefreshDiagnosticsReport();
        }
        finally
        {
            if (!silent) IsBusy = false;
        }
    }

    private int ApplyCommunityHealth(CommunityHealthIndex index, string sourceUrl)
    {
        var byId = new Dictionary<string, CommunityServerHealth>(StringComparer.OrdinalIgnoreCase);
        var byEndpoint = new Dictionary<string, CommunityServerHealth>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in index.Profiles.Where(x => x is not null))
        {
            if (!string.IsNullOrWhiteSpace(entry.ProfileId))
                AddBestCommunityEntry(byId, entry.ProfileId.Trim(), entry);

            var endpointKey = CommunityEndpointKey(entry.Protocol, entry.Endpoint);
            if (!string.IsNullOrWhiteSpace(endpointKey))
                AddBestCommunityEntry(byEndpoint, endpointKey, entry);
        }

        var source = Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ? uri.Host : "Community";
        var applied = 0;
        foreach (var profile in Profiles)
        {
            var match = byId.TryGetValue(profile.Id, out var byIdMatch)
                ? byIdMatch
                : byEndpoint.TryGetValue(CommunityEndpointKey(profile.ProtocolText, profile.Endpoint), out var byEndpointMatch)
                    ? byEndpointMatch
                    : null;

            if (match is null)
            {
                ClearCommunityHealth(profile);
                continue;
            }

            profile.CommunityLatencyMs = match.MedianLatencyMs;
            profile.CommunityScore = CalculateCommunityScore(match);
            profile.CommunitySuccessCount = Math.Max(0, match.SuccessCount);
            profile.CommunityFailureCount = Math.Max(0, match.FailureCount);
            profile.CommunityLastSeenUtc = match.LastSeenUtc?.UtcDateTime;
            profile.CommunitySource = source;
            applied++;
        }

        return applied;
    }

    private static void AddBestCommunityEntry(
        IDictionary<string, CommunityServerHealth> map,
        string key,
        CommunityServerHealth entry)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (!map.TryGetValue(key, out var existing) || IsBetterCommunityEntry(entry, existing))
            map[key] = entry;
    }

    private static bool IsBetterCommunityEntry(CommunityServerHealth candidate, CommunityServerHealth existing)
    {
        var candidateScore = CalculateCommunityScore(candidate) ?? 0;
        var existingScore = CalculateCommunityScore(existing) ?? 0;
        if (candidateScore != existingScore) return candidateScore > existingScore;

        if (candidate.SampleCount != existing.SampleCount) return candidate.SampleCount > existing.SampleCount;

        var candidateLatency = candidate.MedianLatencyMs ?? int.MaxValue;
        var existingLatency = existing.MedianLatencyMs ?? int.MaxValue;
        if (candidateLatency != existingLatency) return candidateLatency < existingLatency;

        return (candidate.LastSeenUtc ?? DateTimeOffset.MinValue) > (existing.LastSeenUtc ?? DateTimeOffset.MinValue);
    }

    private static string CommunityEndpointKey(string protocol, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(protocol) || string.IsNullOrWhiteSpace(endpoint)) return string.Empty;
        return $"{protocol.Trim().ToUpperInvariant()}|{endpoint.Trim().ToLowerInvariant()}";
    }

    private static int? CalculateCommunityScore(CommunityServerHealth entry)
    {
        if (entry.Score is int score)
            return Math.Clamp(score, 0, 100);

        var samples = entry.SampleCount;
        if (samples == 0 && entry.MedianLatencyMs is null) return null;

        var successRate = samples > 0
            ? Math.Clamp(entry.SuccessRate > 0 ? entry.SuccessRate : entry.SuccessCount / (double)samples, 0d, 1d)
            : 0.5d;

        var calculated = 35 + (int)Math.Round(successRate * 45d);
        calculated += entry.MedianLatencyMs switch
        {
            null => 0,
            <= 250 => 15,
            <= 600 => 11,
            <= 1000 => 7,
            <= 1800 => 3,
            _ => 0
        };
        if (samples is > 0 and < 3) calculated -= 8;

        return Math.Clamp(calculated, 0, 100);
    }

    private static void ClearCommunityHealth(ConfigProfile profile)
    {
        profile.CommunityLatencyMs = null;
        profile.CommunityScore = null;
        profile.CommunitySuccessCount = 0;
        profile.CommunityFailureCount = 0;
        profile.CommunityLastSeenUtc = null;
        profile.CommunitySource = string.Empty;
    }

    private void RefreshCommunityHealthStatusMessage()
    {
        if (!Settings.EnableCommunityHealth)
        {
            CommunityHealthStatusMessage = "داده جمعی خاموش است. برای رتبه‌بندی مشترک، آن را روشن کن و URL بده.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Settings.CommunityHealthIndexUrl))
        {
            CommunityHealthStatusMessage = "داده جمعی روشن است، اما URL فایل JSON هنوز وارد نشده.";
            return;
        }

        var last = Settings.LastCommunityHealthFetchUtc is null
            ? "هنوز دریافت نشده"
            : Settings.LastCommunityHealthFetchUtc.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
        var scored = Profiles.Count(x => x.HasCommunityHealth);
        CommunityHealthStatusMessage = $"آماده دریافت داده جمعی • {scored:N0} کانفیگ دارای امتیاز • آخرین دریافت: {last}";
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

    private void ToggleAdvancedConfigTools()
    {
        ShowAdvancedConfigTools = !ShowAdvancedConfigTools;
        RefreshFilters();
    }

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

    private async Task TestProfilesAsync(IReadOnlyList<ConfigProfile> candidates, bool guidedHealthySearch = false, bool simpleSearch = false,
        bool connectedDiscovery = false, int alreadyReady = 0)
    {
        if (IsBusy || IsTesting) return;
        if (guidedHealthySearch)
            candidates = ConfigTestPlanner.OrderForHealthySearch(candidates);
        if (!guidedHealthySearch || (candidates.Count <= 5 && !simpleSearch && !connectedDiscovery))
        {
            TestGoalMessage = "تست این لیست تا پایان اجرا می‌شود.";
            ProgressFullWorking = 0;
            await TestProfilesLegacyAsync(candidates);
            return;
        }

        if (candidates.Count == 0) { StatusMessage = "کانفیگی برای تست وجود ندارد."; return; }
#if ANDROID
        // Connected discovery uses :probe and explicitly bound physical sockets.
        if (_tunnel.IsConnected && !connectedDiscovery)
        {
            StatusMessage = "برای تست کانفیگ‌ها در Android ابتدا VPN را قطع کن؛ اتصال فعال دست‌نخورده باقی ماند.";
            if (Shell.Current is not null)
                await Shell.Current.DisplayAlert("VPN فعال است", StatusMessage, "باشه");
            return;
        }
#endif
        _testCts?.Cancel(); _testCts?.Dispose();
        var testCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _testCompletion = testCompletion;
        _testCts = CancellationTokenSource.CreateLinkedTokenSource(simpleSearch || connectedDiscovery ? _discoveryCts?.Token ?? CancellationToken.None : CancellationToken.None);
        var ct = _testCts.Token;
        var initialTarget = connectedDiscovery ? ConnectedDiscoveryPolicy.HealthyTarget : SimpleSearchHealthyTarget;
        IsBusy = true; IsTesting = true; ProgressTotal = candidates.Count; ProgressDone = 0; ProgressWorking = 0; ProgressFailed = 0; ProgressFullWorking = alreadyReady;
        TestGoalMessage = connectedDiscovery ? "در حال تکمیل ۱۰ سرور سالم؛ اتصال فعلی فعال می‌ماند."
            : simpleSearch ? "در حال پیدا کردن ۵ سرور سالم" : "هدف فعلی: پیدا کردن ۵ کانفیگ سالم؛ بعد از آن از شما می‌پرسم ادامه بدهم یا نه.";
        _healthySearchTarget = initialTarget;
        var sw = Stopwatch.StartNew();
        UpdateProgress(sw, 0);
        var tested = 0; var concurrency = Math.Min(Math.Clamp(Settings.TestConcurrency, 1, 64), candidates.Count);
        // Keep endpoint checks bounded while serializing native probes in :probe.
        if (connectedDiscovery) concurrency = Math.Min(concurrency, 4);
        IReadOnlyList<ConfigProfile> remaining = candidates; int? healthyTarget = initialTarget; var stoppedAfterEnough = false;
        StatusMessage = $"در حال پیدا کردن {healthyTarget} سرور سالم از بین {candidates.Count:N0} مورد...";

        async Task TestSkippedAsync()
        {
            var skipped = Interlocked.Increment(ref tested);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                ProgressDone++;
                MaybeUpdateProgress(sw, skipped, false);
            });
        }

        async Task TestOneAsync(ConfigProfile profile, CancellationToken workerToken)
        {
            if (profile.Health == ProfileHealth.Unsupported)
            {
                await TestSkippedAsync();
                return;
            }

            var old = profile.Health;
            // Retain the last successful state while a cached server is retested.
            if (old != ProfileHealth.Working)
                await MainThread.InvokeOnMainThreadAsync(() => profile.Health = ProfileHealth.Testing);
            try
            {
                var result = await _tunnel.TestAsync(profile, Settings, workerToken).ConfigureAwait(false);
                var newHealth = result.Success ? (result.Level == ValidationLevel.FullProxy ? ProfileHealth.Working : ProfileHealth.Reachable) : ProfileHealth.Failed;
                var done = Interlocked.Increment(ref tested);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    profile.LatencyMs = result.LatencyMs;
                    profile.LastTested = DateTime.Now;
                    profile.TestMessage = FormatTestDetails(result);
                    profile.Health = newHealth;
                    SavedServerPolicy.RememberSuccess(profile);
                    if (!result.Success) profile.FailureCount++; else profile.FailureCount = 0;
                    ProgressDone++;
                    if (newHealth is ProfileHealth.Working or ProfileHealth.Reachable) ProgressWorking++;
                    if (newHealth == ProfileHealth.Working) ProgressFullWorking++;
                    else if (newHealth == ProfileHealth.Failed) ProgressFailed++;
                    if (newHealth == ProfileHealth.Failed && SavedServerPolicy.IsSaved(profile)) RefreshHealthyProfiles();
                    MaybeUpdateProgress(sw, done, done == ProgressTotal);
                });
                if (newHealth == ProfileHealth.Working || (newHealth == ProfileHealth.Failed && SavedServerPolicy.IsSaved(profile)))
                {
                    await _store.SaveProfileAsync(profile, Profiles);
                    await MainThread.InvokeOnMainThreadAsync(RefreshStats);
                }
            }
            catch (OperationCanceledException)
            {
                await MainThread.InvokeOnMainThreadAsync(() => profile.Health = old);
                throw;
            }
            catch
            {
                await MainThread.InvokeOnMainThreadAsync(() => profile.Health = old);
                _testCts?.Cancel();
                throw;
            }
        }

        try
        {
            await _tunnel.EnsureReadyAsync(Settings, cancellationToken: ct);
            while (!ct.IsCancellationRequested && remaining.Count > 0)
            {
                _healthySearchTarget = healthyTarget;
                StatusMessage = healthyTarget is int activeTarget
                    ? $"در حال پیدا کردن {activeTarget:N0} سرور سالم…"
                    : "در حال بررسی سرورهای باقی‌مانده…";
                using (EndpointPrecheckService.BeginBatch())
                {
                    remaining = await ConcurrentTestRunner.RunAsync(remaining, concurrency, TestOneAsync,
                        () => healthyTarget is int target && Volatile.Read(ref _progressFullWorking) >= target, ct);
                }
                RefreshStats();
                if (healthyTarget is null || ProgressFullWorking < healthyTarget.Value) continue;

                if (simpleSearch || connectedDiscovery)
                {
                    stoppedAfterEnough = true;
                    break;
                }

                if (remaining.Count == 0)
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
            try { await PersistFinishedTestRunAsync(); }
            finally { testCompletion.TrySetResult(); }
            RefreshFilters(); RefreshStats();
            IsTesting = false; IsBusy = false;
            StatusMessage = ct.IsCancellationRequested
                ? $"تست متوقف شد؛ {ProgressDone}/{ProgressTotal} بررسی شد."
                : connectedDiscovery ? $"جست‌وجوی سرور جایگزین تمام شد؛ {ProgressFullWorking:N0} سرور آماده داریم."
                : simpleSearch
                    ? $"جست‌وجو تمام شد؛ {ProgressFullWorking:N0} سرور سالم پیدا شد."
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
        var testCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _testCompletion = testCompletion;
        IsBusy = true; IsTesting = true; ProgressTotal = candidates.Count; ProgressDone = 0; ProgressWorking = 0; ProgressFailed = 0; ProgressFullWorking = 0;
        _healthySearchTarget = null;
        var sw = Stopwatch.StartNew();
        UpdateProgress(sw, 0);
        var next = -1; var tested = 0; var concurrency = Math.Min(Math.Clamp(Settings.TestConcurrency, 1, 64), candidates.Count);
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
                    await MainThread.InvokeOnMainThreadAsync(() => { ProgressDone++; MaybeUpdateProgress(sw, skipped, false); });
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
                        p.TestMessage = FormatTestDetails(result);
                        p.Health = newHealth;
                        SavedServerPolicy.RememberSuccess(p);
                        if (!result.Success) p.FailureCount++; else p.FailureCount = 0;
                        ProgressDone++;
                        if (newHealth is ProfileHealth.Working or ProfileHealth.Reachable) ProgressWorking++;
                        if (newHealth == ProfileHealth.Working) ProgressFullWorking++;
                        else if (newHealth == ProfileHealth.Failed) ProgressFailed++;
                        if (newHealth == ProfileHealth.Failed && SavedServerPolicy.IsSaved(p)) RefreshHealthyProfiles();
                        MaybeUpdateProgress(sw, done, done == ProgressTotal);
                    });
                    if (newHealth == ProfileHealth.Working || (newHealth == ProfileHealth.Failed && SavedServerPolicy.IsSaved(p)))
                        await _store.SaveProfileAsync(p, Profiles);
                }
                catch (OperationCanceledException)
                {
                    await MainThread.InvokeOnMainThreadAsync(() => p.Health = old);
                    return;
                }
                catch
                {
                    await MainThread.InvokeOnMainThreadAsync(() => p.Health = old);
                    _testCts?.Cancel();
                    throw;
                }
            }
        }

        try
        {
            await _tunnel.EnsureReadyAsync(Settings, cancellationToken: ct);
            await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Worker()));
        }
        finally
        {
            MaybeUpdateProgress(sw, tested, true);
            try { await PersistFinishedTestRunAsync(); }
            finally { testCompletion.TrySetResult(); }
            RefreshFilters(); RefreshStats();
            IsTesting = false; IsBusy = false;
            StatusMessage = ct.IsCancellationRequested
                ? $"تست متوقف شد؛ {ProgressDone}/{ProgressTotal} بررسی شد."
                : $"تست تمام شد؛ {ProgressFullWorking:N0} سالم کامل، {ProgressWorking:N0} موفق/قابل‌دسترس و {ProgressFailed:N0} ناموفق.";
        }
    }

    private void CancelTest()
    {
        _discoveryCts?.Cancel();
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
        OnPropertyChanged(nameof(HomeSearchProgress));
        OnPropertyChanged(nameof(HomeHint));
        var rate = sw.Elapsed.TotalSeconds > .2 && tested > 0 ? tested / sw.Elapsed.TotalSeconds : 0;
        ProgressSpeed = rate > 0 ? $"{rate:0.0} config/s" : "در حال محاسبه...";
        var remain = Math.Max(0, ProgressTotal - ProgressDone);
        ProgressEta = _healthySearchTarget.HasValue
            ? ProgressFullWorking >= _healthySearchTarget.Value
                ? "سرورهای سالم آماده‌اند."
                : "زمان پیدا شدن سرور به دسترسی شبکه بستگی دارد."
            : rate > 0 ? $"ETA: {TimeSpan.FromSeconds(remain / rate):hh\\:mm\\:ss}" : "ETA: -";
    }

    private static ConfigProfile? GetConnectableProfile(ConfigProfile? profile) =>
        profile is null
            ? null
            : SavedServerPolicy.IsReady(profile) || IsTrustedCommunityCandidate(profile)
                ? profile
                : null;

    private static bool IsTrustedCommunityCandidate(ConfigProfile profile)
    {
        if (!profile.HasCommunityHealth) return false;
        if (profile.Health is ProfileHealth.Failed or ProfileHealth.Unsupported or ProfileHealth.Testing) return false;

        var score = profile.CommunityScore ?? 0;
        var samples = profile.CommunitySuccessCount + profile.CommunityFailureCount;
        if (samples >= 3)
            return score >= 65 && profile.CommunitySuccessCount >= 2;

        return score >= 82;
    }

    private async Task ConnectSelectedAsync()
    {
        if (IsConnected)
        {
            StatusMessage = "اتصال فعال است؛ برای تغییر سرور ابتدا اتصال را قطع کن.";
            ConnectionStatusMessage = "اتصال تأییدشده فعال است. برای اتصال دوباره، اول قطع اتصال را بزن.";
            return;
        }

        var candidate = GetConnectableProfile(SelectedHealthyProfile)
            ?? GetConnectableProfile(SelectedProfile)
            ?? HealthyProfiles.FirstOrDefault()
            ?? RecommendedHealthyProfile;
        if (candidate is null)
        {
            StatusMessage = "کانفیگ Full-Test یا پیشنهاد جمعی قابل اعتماد برای اتصال نداریم؛ ابتدا کانفیگ‌ها را تست کن یا داده جمعی را دریافت کن.";
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

        var attempts = BuildConnectionAttempts(candidate).ToList();
        if (attempts.Count == 0) attempts.Add(candidate);
        var maxAttempts = Settings.AutoReconnect ? Math.Clamp(Settings.AutoReconnectAttempts, 1, 5) : 1;
        attempts = attempts.Take(maxAttempts).ToList();
        var failures = new List<string>();
        var startupFailed = false;
        var connectedSuccessfully = false;

        _homeNotice = "";
        IsBusy = true;
        try
        {
            IsConnected = false;
            for (var attemptIndex = 0; attemptIndex < attempts.Count; attemptIndex++)
            {
                var profile = attempts[attemptIndex];
                SelectedHealthyProfile = profile;
                SelectedProfile = profile;
                var attemptText = attempts.Count == 1 ? "" : $" ({attemptIndex + 1}/{attempts.Count})";

                try
                {
                    SetConnectionUiPhase(ConnectionUiPhase.Connecting);
                    StatusMessage = $"در حال اتصال به {profile.DisplayName}{attemptText}...";
                    ConnectionStatusMessage = DeviceInfo.Platform == DevicePlatform.Android
                        ? $"مرحله 1: بررسی مجوز VPN Android{attemptText}..."
                        : DeviceInfo.Platform == DevicePlatform.iOS
                            ? $"مرحله 1: آماده‌سازی مجوز VPN iOS{attemptText}..."
                            : $"در حال ساخت تونل{attemptText}...";
                    await _tunnel.EnsureReadyAsync(Settings, cancellationToken: default);
                    await _tunnel.ConnectAsync(profile, Settings);

                    SetConnectionUiPhase(ConnectionUiPhase.Validating);
                    StatusMessage = $"تونل آماده شد؛ در حال تست اینترنت {profile.DisplayName}...";
                    ConnectionStatusMessage = "در حال تست عبور واقعی اینترنت از تونل...";

                    var validation = await _tunnel.TestCurrentConnectionAsync(Settings);
                    if (validation.Success && validation.Level == ValidationLevel.FullProxy)
                    {
                        profile.Health = ProfileHealth.Working;
                        profile.LatencyMs = validation.LatencyMs ?? profile.LatencyMs;
                        profile.LastTested = DateTime.Now;
                        SavedServerPolicy.RememberSuccess(profile);
                        profile.TestMessage = HumanizeTestResult(validation);
                        profile.FailureCount = 0;
                        SelectedHealthyProfile = profile;
                        SelectedProfile = profile;
                        _activeProfileId = profile.Id;
                        IsConnected = true;
                        StatusMessage = DeviceInfo.Platform == DevicePlatform.Android
                            ? $"VPN Android متصل و تست اینترنت تأیید شد: {profile.DisplayName}"
                            : DeviceInfo.Platform == DevicePlatform.iOS
                                ? $"VPN iOS متصل و تست اینترنت تأیید شد: {profile.DisplayName}"
                                : $"متصل و تست اینترنت تأیید شد: {profile.DisplayName} • HTTP محلی: 127.0.0.1:{Settings.HttpPort}";
                        ConnectionStatusMessage = DeviceInfo.Platform == DevicePlatform.WinUI
                            ? $"✓ اینترنت از Proxy تأیید شد • {validation.Message}"
                            : $"✓ اینترنت از VPN تأیید شد • {profile.DisplayName} • {validation.Message}";
                        await _store.SaveProfilesAsync(Profiles);
                        RefreshFilters();
                        RefreshStats();
                        RefreshDiagnosticsReport();
                        connectedSuccessfully = true;
                        return;
                    }

                    var friendly = HumanizeTestResult(validation);
                    failures.Add($"{profile.DisplayName}: {friendly}");
                    MarkConnectionAttemptFailed(profile, friendly);
                    try { await _tunnel.DisconnectAsync(Settings); } catch { }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var friendly = HumanizeException(ex);
                    failures.Add($"{profile.DisplayName}: {friendly}");
                    MarkConnectionAttemptFailed(profile, friendly);
                    try { await _tunnel.DisconnectAsync(Settings); } catch { }
                    if (!ConnectionAttemptPolicy.ShouldTryAnotherServer(ex))
                    {
                        startupFailed = true;
                        break;
                    }
                }
            }

            IsConnected = false;
            var summary = failures.Count == 0
                ? "هیچ سرور سالمی برای تلاش بعدی باقی نماند."
                : failures[0];
            StatusMessage = startupFailed
                ? "VPN روی گوشی راه‌اندازی یا تأیید نشد؛ سرورهای پیدا‌شده حفظ شدند."
                : "اتصال تأیید نشد؛ سرورهای پیدا‌شده برای تلاش دوباره حفظ شدند.";
            ConnectionStatusMessage = summary;
            _homeNotice = startupFailed
                ? "راه‌اندازی VPN تأیید نشد. جزئیات خطا در پیشرفته ← عیب‌یابی است."
                : "اتصال برقرار نشد. دوباره تلاش کن یا پینگ همین سرور را بگیر.";
            await _store.SaveProfilesAsync(Profiles);
            RefreshFilters();
            RefreshStats();
            RefreshDiagnosticsReport();
            if (Shell.Current is not null)
                await Shell.Current.DisplayAlert("اتصال برقرار نشد", summary + "\n\nسرورهای پیدا‌شده حفظ شدند. جزئیات در پیشرفته ← عیب‌یابی است.", "باشه");
        }
        finally
        {
            SetConnectionUiPhase(ConnectionUiPhase.Idle);
            IsBusy = false;
            if (connectedSuccessfully) await OfferConnectedDiscoveryAsync();
        }
    }

    private async Task OfferConnectedDiscoveryAsync()
    {
        // Android has explicit physical-network isolation. Other platforms keep
        // their existing discovery behavior until they have an equivalent path.
#if ANDROID
        var generation = _connectionGeneration;
        if (!IsConnected || Shell.Current is null || IsBusy || _findingServers ||
            ConnectedDiscoveryPolicy.CountReady(Profiles, DateTime.Now) >= ConnectedDiscoveryPolicy.HealthyTarget) return;
        try
        {
            var accepted = await Shell.Current.DisplayAlert("سرور جایگزین پیدا کنم؟",
                "اتصال برقرار شد. جست‌وجو را از اینترنت مستقیم گوشی ادامه بدهم تا در مجموع ۱۰ سرور سالم داشته باشی؟ تا ۳ دقیقه جست‌وجو می‌کنیم و هر وقت خواستی می‌توانی متوقفش کنی.",
                "بله، ادامه بده", "فعلاً نه");
            if (!accepted || !IsConnected || generation != _connectionGeneration || IsBusy || _findingServers) return;
            await FindConnectedServersAsync(generation);
        }
        catch (Exception ex)
        {
            _homeNotice = HumanizeException(ex);
            StatusMessage = _homeNotice;
            NotifyHomeChanged();
        }
#endif
    }

#if ANDROID
    private async Task FindConnectedServersAsync(long generation)
    {
        using var network = AndroidDirectNetwork.BeginScope();
        using var discovery = new CancellationTokenSource(ConnectedDiscoveryPolicy.Budget);
        _discoveryCts = discovery;
        _discoveryCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectedSearch = true;
        _findingServers = true;
        RefreshConnectionActions();
        try
        {
            var now = DateTime.Now;
            var ready = ConnectedDiscoveryPolicy.CountReady(Profiles, now);
            if (ready >= ConnectedDiscoveryPolicy.HealthyTarget) return;
            // Test the existing archive first. Fetch only when it has no candidates;
            // a large subscription download should not delay saved candidates.
            var candidates = ConnectedDiscoveryPolicy.Candidates(Profiles, _activeProfileId, now);
            if (candidates.Count == 0)
            {
                await GetConfigAsync(discovery.Token);
                now = DateTime.Now;
                ready = ConnectedDiscoveryPolicy.CountReady(Profiles, now);
                candidates = ConnectedDiscoveryPolicy.Candidates(Profiles, _activeProfileId, now);
            }
            discovery.Token.ThrowIfCancellationRequested();
            if (!IsConnected || generation != _connectionGeneration) return;
            await TestProfilesAsync(candidates, guidedHealthySearch: true, connectedDiscovery: true, alreadyReady: ready);
            // If the archive was exhausted, try updated subscriptions once. Do
            // not retest the same failed configurations again in this search.
            if (!discovery.IsCancellationRequested && IsConnected && generation == _connectionGeneration &&
                ConnectedDiscoveryPolicy.CountReady(Profiles, DateTime.Now) < ConnectedDiscoveryPolicy.HealthyTarget)
            {
                var attempted = candidates.Select(profile => profile.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                await GetConfigAsync(discovery.Token);
                discovery.Token.ThrowIfCancellationRequested();
                if (!IsConnected || generation != _connectionGeneration) return;
                now = DateTime.Now;
                candidates = ConnectedDiscoveryPolicy.Candidates(Profiles, _activeProfileId, now)
                    .Where(profile => !attempted.Contains(profile.Id)).ToList();
                if (candidates.Count > 0)
                    await TestProfilesAsync(candidates, guidedHealthySearch: true, connectedDiscovery: true,
                        alreadyReady: ConnectedDiscoveryPolicy.CountReady(Profiles, now));
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "جست‌وجوی سرور جایگزین متوقف شد؛ سرورهای پیدا‌شده ذخیره شدند.";
        }
        finally
        {
            if (IsConnected)
            {
                var ready = ConnectedDiscoveryPolicy.CountReady(Profiles, DateTime.Now);
                _homeNotice = ready >= ConnectedDiscoveryPolicy.HealthyTarget
                    ? "۱۰ سرور سالم آماده و ذخیره شده‌اند. اتصال فعلی فعال است."
                    : $"جست‌وجو پایان یافت؛ {ready:N0} سرور سالم ذخیره شد. اتصال فعلی فعال است.";
            }
            _discoveryCts = null;
            _findingServers = false;
            _connectedSearch = false;
            _discoveryCompletion.TrySetResult();
            RefreshConnectionActions();
        }
    }
#endif

    private async Task DisconnectFromHomeAsync()
    {
        if (!CanStopConnection) return;
        if (_connectedSearch) CancelTest();
        _pingCts?.Cancel();
        try { await DisconnectAsync(); }
        catch (Exception ex)
        {
            StatusMessage = HumanizeException(ex);
            if (Shell.Current is not null) await Shell.Current.DisplayAlert("قطع اتصال", StatusMessage, "باشه");
        }
    }

    private IEnumerable<ConfigProfile> BuildConnectionAttempts(ConfigProfile first)
    {
        yield return first;

        foreach (var profile in Profiles
                     .Where(x => x.Id != first.Id && (SavedServerPolicy.IsReady(x) || IsTrustedCommunityCandidate(x)))
                     .OrderByDescending(x => x.QualityScore)
                     .ThenBy(x => x.LatencyMs ?? int.MaxValue)
                     .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            yield return profile;
        }
    }

    private static void MarkConnectionAttemptFailed(ConfigProfile profile, string message)
    {
        ConnectionAttemptPolicy.RecordFailure(profile, message);
    }

    private async Task ConnectBestAsync()
    {
        var best = RecommendedHealthyProfile;
        if (best is null) { StatusMessage = "هنوز کانفیگ Full-Test یا پیشنهاد جمعی قابل اعتماد نداریم."; return; }
        SelectedHealthyProfile = best;
        SelectedProfile = best;
        await ConnectSelectedAsync();
    }

    private async Task DisconnectAsync()
    {
        if (!IsConnected && !_tunnel.IsConnected)
        {
            StatusMessage = "اتصال فعالی برای قطع کردن وجود ندارد.";
            ConnectionStatusMessage = "VPN قطع است.";
            return;
        }

        IsBusy = true;
        SetConnectionUiPhase(ConnectionUiPhase.Disconnecting);
        try
        {
            await _tunnel.DisconnectAsync(Settings);
            IsConnected = false;
            StatusMessage = "اتصال قطع شد.";
            ConnectionStatusMessage = "VPN قطع است.";
            RefreshDiagnosticsReport();
        }
        finally
        {
            SetConnectionUiPhase(ConnectionUiPhase.Idle);
            IsBusy = false;
        }
    }

#if ANDROID
    private void OnAndroidVpnStatusChanged(object? sender, AndroidVpnStatusEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ConnectionStatusMessage = e.Message;
            StatusMessage = e.Message;
            if (e.Connected.HasValue)
            {
                if (e.Connected.Value && !string.IsNullOrWhiteSpace(e.ProfileId))
                    _activeProfileId = e.ProfileId;
                IsConnected = e.Connected.Value;
                SetConnectionUiPhase(ConnectionUiPhase.Idle);
            }
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

    private void RefreshDiagnosticsReport()
    {
        var selected = SelectedHealthyProfile ?? SelectedProfile;
        var builder = new StringBuilder();
        builder.AppendLine("SaePar Tunnel Diagnostics");
        builder.AppendLine($"Version: {ReleaseVersionText}");
        builder.AppendLine($"Runtime: {RuntimeSummaryText}");
        builder.AppendLine($"Backend: {BackendTitle}");
        builder.AppendLine($"Tunnel supported: {CanTunnel}");
        builder.AppendLine($"Connection: {ConnectionBadgeText}");
        builder.AppendLine($"Connection message: {ConnectionStatusMessage}");
        builder.AppendLine($"Profiles: total={TotalProfiles}, working={WorkingProfiles}, reachable={ReachableProfiles}, failed={FailedProfiles}, untested={UntestedProfiles}");
        if (_store.LastStorageError.Length > 0) builder.AppendLine($"Storage: {_store.LastStorageError}");
        builder.AppendLine($"Recommended: {RecommendedProfileName} | {RecommendedProfileScoreText}");
        builder.AppendLine($"Selected: {(selected is null ? "-" : $"{selected.DisplayName} | {selected.Endpoint} | {selected.QualitySummaryText} | {selected.HealthText}")}");
        builder.AppendLine($"Ports: socks={Settings.SocksPort}, http={Settings.HttpPort}, probe={Settings.ProbePort}");
        builder.AppendLine($"Quick mode: {Settings.QuickMode}");
        builder.AppendLine($"Auto reconnect: {Settings.AutoReconnect} ({Settings.AutoReconnectAttempts})");
        builder.AppendLine($"System proxy: {Settings.EnableSystemProxy}");
        builder.AppendLine($"Subscriptions: total={SubscriptionSources.Count}, active={SubscriptionSources.Count(x => x.IsEnabled)}");
        foreach (var source in SubscriptionSources)
        {
            var host = Uri.TryCreate(source.Url, UriKind.Absolute, out var sourceUri) ? sourceUri.Host : "invalid";
            var lastFetch = source.Source.LastFetchedUtc?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "-";
            builder.AppendLine($"Subscription: {source.Name} | active={source.IsEnabled} | host={host} | last={lastFetch}");
        }
        builder.AppendLine($"Community health: enabled={Settings.EnableCommunityHealth}, scored={Profiles.Count(x => x.HasCommunityHealth)}, url={Settings.CommunityHealthIndexUrl}");
        builder.AppendLine($"Community health fetch: {(Settings.LastCommunityHealthFetchUtc is null ? "-" : Settings.LastCommunityHealthFetchUtc.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm"))}");
        if (IsWindows) builder.AppendLine($"Xray path: {(string.IsNullOrWhiteSpace(Settings.XrayPath) ? "auto" : Settings.XrayPath)}");
        DiagnosticsReport = builder.ToString().TrimEnd();
    }

    private async Task CopyDiagnosticsAsync()
    {
        RefreshDiagnosticsReport();
        await Clipboard.Default.SetTextAsync(DiagnosticsReport);
        StatusMessage = "گزارش عیب‌یابی کپی شد.";
    }

    private async Task DiagnoseConnectionAsync()
    {
        RefreshDiagnosticsReport();
        if (!IsConnected && !_tunnel.IsConnected)
        {
            StatusMessage = "اتصال فعالی برای تست زنده وجود ندارد.";
            ConnectionStatusMessage = "VPN قطع است.";
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "در حال تست اتصال فعلی...";
            var result = await _tunnel.TestCurrentConnectionAsync(Settings);
            var message = HumanizeTestResult(result);
            ConnectionStatusMessage = result.Success && result.Level == ValidationLevel.FullProxy
                ? "اتصال فعلی سالم و تأییدشده است."
                : message;
            StatusMessage = ConnectionStatusMessage;
            RefreshDiagnosticsReport();
            if (Shell.Current is not null)
                await Shell.Current.DisplayAlert("نتیجه عیب‌یابی", $"{message}\n{result.Message}", "باشه");
        }
        finally
        {
            IsBusy = false;
        }
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
        await PersistSettingsAsync(announce: true);
    }

    private async Task PersistSettingsAsync(bool announce = false)
    {
        Settings.TestConcurrency = Math.Clamp(Settings.TestConcurrency, 1, DeviceInfo.Platform == DevicePlatform.WinUI ? 64 : 12);
        Settings.AutoReconnectAttempts = Math.Clamp(Settings.AutoReconnectAttempts <= 0 ? 3 : Settings.AutoReconnectAttempts, 1, 5);
        Settings.WhitelistWebsites = WhitelistWebsites.ToList();
        Settings.DirectRoutingEntries = DirectRoutingEntries.ToList();
        Settings.WhitelistApplications = WhitelistApplications.ToList();
        SyncSubscriptionSettings();
        await _store.SaveSettingsAsync(Settings);
        OnPropertyChanged(nameof(QuickMode));
        OnPropertyChanged(nameof(AutoReconnect));
        OnPropertyChanged(nameof(AutoReconnectAttempts));
        OnPropertyChanged(nameof(ConfigModeText));
        OnPropertyChanged(nameof(ConfigModeHint));
        RefreshCommunityHealthStatusMessage();
        RefreshDiagnosticsReport();
        if (announce) StatusMessage = "تنظیمات ذخیره شد.";
    }

    private void RefreshRoutingList()
    {
        OnPropertyChanged(nameof(DirectRoutingPreview));
        OnPropertyChanged(nameof(DirectRoutingSummary));
    }

    private async Task AddDirectRoutesAsync(string text)
    {
        if (!CanSearchServers) return;
        var result = await Task.Run(() => RoutingListParser.Parse(text));
        if (!CanSearchServers) { RoutingFeedback = "برای تغییر فهرست، اتصال را قطع کن."; return; }
        var before = DirectRoutingEntries.Count;
        var merged = DirectRoutingEntries.Concat(result.Entries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (merged.Length > RoutingListParser.MaxEntries) throw new InvalidOperationException("فهرست شخصی بیش از ۱۰۰ هزار مورد می‌شود.");
        DirectRoutingEntries.ReplaceRange(merged);
        DirectRoutingText = "";
        RefreshRoutingList();
        await PersistSettingsAsync();
        RoutingFeedback = $"{merged.Length - before:N0} مورد اضافه شد؛ {result.InvalidCount:N0} مورد نامعتبر نادیده گرفته شد.";
    }

    private async Task ImportDirectRoutesAsync()
    {
        if (!CanSearchServers) return;
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "انتخاب فهرست سایت‌ها یا محدوده‌های IP" });
        if (file is null) return;
        using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[8192];
        var text = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            if (text.Length + count > RoutingListParser.MaxTextLength) throw new InvalidOperationException("حجم فایل نباید بیشتر از ۴ مگابایت باشد.");
            text.Append(buffer, 0, count);
        }
        await AddDirectRoutesAsync(text.ToString());
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

        if (ShowAdvancedConfigTools) query = SortOption switch
        {
            "بهترین امتیاز" => query.OrderByDescending(x => x.QualityScore).ThenBy(x => x.LatencyMs ?? int.MaxValue).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            "قدیمی‌ترین اضافه‌شده" => query.OrderBy(x => x.FirstSeen).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            "کمترین Ping" => query.OrderBy(x => x.LatencyMs ?? int.MaxValue).ThenByDescending(x => x.FirstSeen),
            "بیشترین Ping" => query.OrderByDescending(x => x.LatencyMs ?? int.MinValue).ThenByDescending(x => x.FirstSeen),
            "جدیدترین تست" => query.OrderByDescending(x => x.LastTested ?? DateTime.MinValue).ThenByDescending(x => x.FirstSeen),
            "نام" => query.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderByDescending(x => x.FirstSeen).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        };

        _filteredSnapshot = query.ToList();
        if (_visibleLimit <= 0) _visibleLimit = InitialVisibleLimit();
        FilteredProfiles.ReplaceRange(ShowAdvancedConfigTools
            ? _filteredSnapshot.Take(_visibleLimit)
            : Array.Empty<ConfigProfile>());
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasMoreProfiles));
        OnPropertyChanged(nameof(VisibleProfilesLabel));
        NotifyConfigFlowChanged();
    }

    private int InitialVisibleLimit() => DeviceInfo.Platform == DevicePlatform.WinUI ? 240 : 80;

    private void LoadMore()
    {
        if (!HasMoreProfiles) return;
        _visibleLimit = Math.Min(_visibleLimit + (DeviceInfo.Platform == DevicePlatform.WinUI ? 240 : 80), FilteredCount);
        FilteredProfiles.ReplaceRange(_filteredSnapshot.Take(_visibleLimit));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasMoreProfiles));
        OnPropertyChanged(nameof(VisibleProfilesLabel));
    }

    private void RefreshHealthyProfiles()
    {
        var selectedId = SelectedHealthyProfile?.Id ?? Settings.SelectedServerId;
        var healthy = Profiles
            .Where(x => x.Health == ProfileHealth.Working)
            .OrderByDescending(x => x.QualityScore)
            .ThenBy(x => x.LatencyMs ?? int.MaxValue)
            .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        HealthyProfiles.ReplaceRange(healthy);
        var saved = SavedServerPolicy.ForHome(Profiles, selectedId, ConnectedDiscoveryPolicy.HealthyTarget);
        HomeServers.ReplaceRange(saved.Select((profile, index) => new HomeServerViewModel(profile, index + 1)));
        _recommendedHealthyProfile = healthy.FirstOrDefault() ?? Profiles
            .Where(IsTrustedCommunityCandidate)
            .OrderByDescending(x => x.QualityScore)
            .ThenBy(x => x.CommunityLatencyMs ?? int.MaxValue)
            .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();
        var next = saved.FirstOrDefault(x => x.Id == selectedId) ?? saved.FirstOrDefault();
        SelectedHealthyProfile = next;
        foreach (var row in HomeServers) row.RefreshSelection(row.Profile.Id == SelectedHealthyProfile?.Id);
        NotifyHomeChanged();
        OnPropertyChanged(nameof(HealthyProfilesCount));
        OnPropertyChanged(nameof(HealthyProfilesCountText));
        OnPropertyChanged(nameof(SelectedHealthyPingText));
        OnPropertyChanged(nameof(HealthySelectionSummary));
        NotifyRecommendedProfileChanged();
    }

    private void RefreshStats()
    {
        var working = 0;
        var reachable = 0;
        var failed = 0;
        var untested = 0;
        foreach (var profile in Profiles)
        {
            switch (profile.Health)
            {
                case ProfileHealth.Working:
                    working++;
                    break;
                case ProfileHealth.Reachable:
                    reachable++;
                    break;
                case ProfileHealth.Failed:
                    failed++;
                    break;
                case ProfileHealth.Untested:
                    untested++;
                    break;
            }
        }

        _totalProfiles = Profiles.Count;
        _workingProfiles = working;
        _reachableProfiles = reachable;
        _failedProfiles = failed;
        _untestedProfiles = untested;

        RefreshHealthyProfiles();
        OnPropertyChanged(nameof(TotalProfiles));
        OnPropertyChanged(nameof(WorkingProfiles));
        OnPropertyChanged(nameof(ReachableProfiles));
        OnPropertyChanged(nameof(FailedProfiles));
        OnPropertyChanged(nameof(PendingCleanupSummary));
        OnPropertyChanged(nameof(UntestedProfiles));
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasMoreProfiles));
        OnPropertyChanged(nameof(VisibleProfilesLabel));
        NotifyConfigFlowChanged();
        RefreshDiagnosticsReport();
    }

    private void NotifyConfigFlowChanged()
    {
        OnPropertyChanged(nameof(PrimaryConfigFlowHint));
        OnPropertyChanged(nameof(ConfigFlowNextStep));
        OnPropertyChanged(nameof(ConfigFlowHealthSummary));
        NotifyRecommendedProfileChanged();
    }

    private async Task RunSafeAsync(Func<Task> action)
    {
        if (IsBusy || _isInitializing || _findingServers) return;
        try { await action(); }
        catch (OperationCanceledException)
        {
            StatusMessage = "عملیات متوقف شد.";
            IsTesting = false;
            IsBusy = false;
        }
        catch (Exception ex)
        {
            var friendly = HumanizeException(ex);
            _homeNotice = friendly;
            NotifyHomeChanged();
            StatusMessage = "خطا: " + friendly;
            if (DeviceInfo.Platform == DevicePlatform.Android) ConnectionStatusMessage = "✕ " + friendly;
            IsTesting = false;
            IsBusy = false;
            if (Shell.Current is not null)
                await Shell.Current.DisplayAlert("خطا", friendly, "باشه");
        }
    }

    private static string HumanizeTestResult(TestResult result) =>
        result.Success
            ? result.Level == ValidationLevel.FullProxy
                ? "این سرور اینترنت را کامل از تونل عبور داد."
                : "سرور پاسخ داد، اما عبور کامل اینترنت از تونل تأیید نشد."
            : HumanizeProblem(result.Message);

    private static string FormatTestDetails(TestResult result)
    {
        var summary = HumanizeTestResult(result);
        return result.Success || summary == result.Message
            ? summary : summary + "\n" + result.Message;
    }

    private static string HumanizeException(Exception ex)
    {
        var message = FlattenException(ex);
        // Keep the actual Android stage/error. Generic text previously hid Binder
        // and TUN failures behind an unrelated permission or bad-server message.
        if (ex is TunnelStartupException or TunnelValidationException)
            return message.Length <= 400 ? message : message[..400] + "...";
        return HumanizeProblem(message);
    }

    private static string HumanizeProblem(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "یک خطای نامشخص رخ داد. دوباره تلاش کن یا از Diagnostics گزارش بگیر.";

        var lower = message.ToLowerInvariant();
        if (lower.Contains("timeout") || lower.Contains("timed out") || lower.Contains("operation timed out"))
            return "سرور دیر پاسخ داد. معمولاً با انتخاب یک سرور دیگر حل می‌شود.";
        if (lower.Contains("connection refused") || lower.Contains("actively refused"))
            return "سرور اتصال را رد کرد. این کانفیگ احتمالاً دیگر فعال نیست.";
        if (lower.Contains("network is unreachable") || lower.Contains("no route") || lower.Contains("host unreachable"))
            return "مسیر اینترنت به این سرور برقرار نشد. اینترنت دستگاه یا سرور مقصد را بررسی کن.";
        if (lower.Contains("permission") || lower.Contains("vpn"))
            return "مجوز یا سرویس VPN کامل فعال نشده است. تنظیمات VPN دستگاه را بررسی کن.";
        if (lower.Contains("proxy") && lower.Contains("failed"))
            return "تونل ساخته شد، اما عبور اینترنت از Proxy تأیید نشد. یک سرور دیگر امتحان کن.";
        if (lower.Contains("xray") || lower.Contains("core"))
            return "موتور Xray با این کانفیگ مشکل داشت. کانفیگ دیگری را تست کن یا Diagnostics را ببین.";
        if (message.Length <= 180) return message;
        return message[..180] + "...";
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
