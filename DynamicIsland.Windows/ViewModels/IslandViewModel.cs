using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Windows.Infrastructure;
using DynamicIsland.Windows.Models;
using DynamicIsland.Windows.Services;
using DynamicIsland.Windows.Services.Q;
using DynamicIsland.Q.Core;
using MediaBrush = System.Windows.Media.Brush;

namespace DynamicIsland.Windows.ViewModels;

public sealed partial class IslandViewModel : ObservableObject, IDisposable
{
    private static readonly ConcurrentDictionary<string, MediaBrush> BrushCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<(double Size, double Radius), Geometry> RingGeometryCache = new();
    private readonly MediaSessionService _mediaService;
    private readonly AudioSessionService _audioService;
    private readonly BatteryService _batteryService;
    private readonly ClockService _clockService;
    private readonly TimerAlarmService _timerAlarmService;
    private readonly ThemeService _themeService;
    private readonly WeatherService _weatherService;
    private readonly SystemMonitorService _systemMonitorService;
    private readonly AudioSpectrumService _spectrumService;
    private readonly StocksService _stocksService;
    private readonly CalendarService _calendarService;
    private readonly NotificationListenerService _notificationService;
    private readonly NotificationHistoryService _notificationHistoryService;
    private readonly SettingsService _settingsPersistence;
    private readonly PrivacySensorService _privacyService;
    private readonly AirPodsService? _airPodsService;
    private AirPodsState _airPods = AirPodsState.Unavailable;
    private PrivacySensorState _privacy = PrivacySensorState.None;
    private readonly System.Windows.Threading.DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly System.Windows.Threading.DispatcherTimer _volumeWarningTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly System.Windows.Threading.DispatcherTimer _airPodsBannerTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _volumeWarningActive;
    private bool _airPodsBannerActive;
    private int _bannerSeq;
    private int _privacySeq;
    private bool _prevAboveVolumeThreshold = true; // initialised true so first audio event never triggers
    private DateTimeOffset _volumeWarnCooldownUntil = DateTimeOffset.MinValue;
    private int _lastWarnedVolumePercent;
    private IReadOnlyList<StockQuote> _stocks = [];
    private IReadOnlyList<StockTile> _stockTiles = [];
    private IReadOnlyList<WorldClock> _worldClocks = [];
    private IReadOnlyList<LaunchEntry> _launchItems = [];
    private QuoteItem[] _quotes = [];
    private int _quoteIndex;
    private readonly System.Windows.Threading.DispatcherTimer _quoteTimer = new();
    private readonly System.Windows.Threading.DispatcherTimer _visualizerTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly System.Windows.Threading.DispatcherTimer _connectivityTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly System.Windows.Threading.DispatcherTimer _qAutoCloseTimer = new();
    private double _visualizerPhase;
    private (string Label, TimeZoneInfo Zone)[] _worldClockZones = [];
    private string _worldClockConfig = string.Empty;
    private string[] _expandedOrder = ["media", "volume", "status"];
    private MeetingInfo? _meeting;
    private NotificationInfo? _notification;
    private NotificationHistoryItem? _currentNotificationHistoryItem;
    private int _notificationSeq;
    private MediaInfo _media = MediaInfo.Empty;
    private AudioState _audio = AudioState.Unknown;
    private WeatherInfo? _weather;
    private SystemStats _sysStats = SystemStats.Empty;
    private double[] _spectrum = new double[AudioSpectrumService.BandCount];
    private string? _adaptiveAccent;
    private BatteryState _battery = BatteryState.Unavailable;
    private DateTimeOffset _now = DateTimeOffset.Now;
    private bool _isExpanded;
    private bool _keepExpanded;
    private bool _isDarkTheme = true;
    private BitmapImage? _artwork;
    private byte[]? _artworkBytes;
    private readonly IQSessionController _qSession;
    private readonly IQScreenContextService _qScreen;
    private readonly IQSpeechInputService _qSpeech;
    private readonly Services.Q.IQSecretStore _qSecrets;
    private readonly CodexAccountCoordinator? _codexAccount;
    private IReadOnlyList<CodexModel> _codexModels = [];
    private QSessionSnapshot _qSnapshot = new(QRunState.Idle, QMode.Ask, string.Empty, string.Empty, "Ready", null, null, "", "");
    private nint _qTargetWindow;

    public IslandViewModel(AppSettings settings, MediaSessionService mediaService,
        AudioSessionService audioService, BatteryService batteryService, ClockService clockService,
        TimerAlarmService timerAlarmService, ThemeService themeService, WeatherService weatherService,
        SystemMonitorService systemMonitorService, AudioSpectrumService spectrumService,
        StocksService stocksService, CalendarService calendarService,
        NotificationListenerService notificationService, PrivacySensorService privacyService,
        NotificationHistoryService notificationHistoryService,
        IQSessionController qSession, IQScreenContextService qScreen, IQSpeechInputService qSpeech,
        Services.Q.IQSecretStore qSecrets, CodexAccountCoordinator? codexAccount = null, AirPodsService? airPodsService = null, SettingsService? settingsService = null)
    {
        Settings = settings;
        _settingsPersistence = settingsService ?? new SettingsService(new LoggingService());
        _mediaService = mediaService;
        _audioService = audioService;
        _batteryService = batteryService;
        _clockService = clockService;
        _timerAlarmService = timerAlarmService;
        _themeService = themeService;
        _weatherService = weatherService;
        _systemMonitorService = systemMonitorService;
        _spectrumService = spectrumService;
        _stocksService = stocksService;
        _calendarService = calendarService;
        _notificationService = notificationService;
        _privacyService = privacyService;
        _notificationHistoryService = notificationHistoryService;
        _qSession = qSession;
        _qScreen = qScreen;
        _qSpeech = qSpeech;
        _qSecrets = qSecrets;
        _codexAccount = codexAccount;
        if (_codexAccount is not null)
        {
            _codexModels = _codexAccount.Snapshot.Models ?? [];
            _codexAccount.Changed += OnCodexAccountChanged;
        }
        _airPodsService = airPodsService;
        if (_airPodsService != null)
        {
            _airPods = _airPodsService.Current;
            _airPodsService.Changed += OnAirPodsChanged;
        }

        PreviousCommand = new RelayCommand(() => _ = _mediaService.PreviousAsync(), () => Media.CanPrevious);
        PlayPauseCommand = new RelayCommand(() => _ = _mediaService.TogglePlayPauseAsync(), () => Media.CanPlayPause);
        NextCommand = new RelayCommand(() => _ = _mediaService.NextAsync(), () => Media.CanNext);
        SeekBackCommand = new RelayCommand(() => _ = _mediaService.SeekByAsync(TimeSpan.FromSeconds(-SeekBackSeconds)), () => CanSeek);
        SeekForwardCommand = new RelayCommand(() => _ = _mediaService.SeekByAsync(TimeSpan.FromSeconds(SeekForwardSeconds)), () => CanSeek);
        ToggleMuteCommand = new RelayCommand(() => _audioService.SetMuted(!Audio.SystemMuted),
            () => Audio.Availability == AudioAvailability.Available);
        AdjustVolumeCommand = new RelayCommand<string>(delta =>
        {
            if (int.TryParse(delta, out var amount))
                _audioService.SetMasterVolume(Audio.MasterVolumePercent + amount);
        });
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        RefreshCodexAccountCommand = new RelayCommand(() => _ = _codexAccount?.RefreshAsync());
        CodexPrimaryActionCommand = new RelayCommand(() => _ = CodexPrimaryActionAsync());
        SignOutCodexCommand = new RelayCommand(() => _ = SignOutCodexAsync());

        _mediaService.Changed += OnMediaChanged;
        _audioService.Changed += OnAudioChanged;
        _weatherService.Changed += OnWeatherChanged;
        _systemMonitorService.Changed += OnSysStatsChanged;
        _spectrumService.BandsChanged += OnSpectrumChanged;
        _stocksService.Changed += OnStocksChanged;
        _calendarService.Changed += OnMeetingChanged;
        _notificationService.BatchReceived += OnNotificationBatch;
        _calendarService.StatusChanged += OnIntegrationStatusChanged;
        _notificationService.StatusChanged += OnIntegrationStatusChanged;
        _privacyService.Changed += OnPrivacyChanged;
        _notificationTimer.Interval = TimeSpan.FromSeconds(1); _notificationTimer.Tick += (_, _) => PumpNotifications(); _notificationTimer.Start();
        _volumeWarningTimer.Tick += (_, _) => { _volumeWarningTimer.Stop(); _volumeWarningActive = false; RaiseMany(nameof(ShowBanner), nameof(IsAirPodsBannerActive), nameof(BannerApp), nameof(BannerTitle), nameof(BannerBody)); };
        _airPodsBannerTimer.Tick += (_, _) => { _airPodsBannerTimer.Stop(); _airPodsBannerActive = false; RaiseMany(nameof(ShowBanner), nameof(IsAirPodsBannerActive), nameof(BannerApp), nameof(BannerTitle), nameof(BannerBody)); };
        _quoteTimer.Tick += (_, _) => OnUi(AdvanceQuote);
        _visualizerTimer.Tick += (_, _) => OnUi(() => { if (IsAudioActive) { _visualizerPhase += 0.32; RaiseVisualizerProperties(); } });
        _visualizerTimer.Start();
        _connectivityTimer.Tick += (_, _) => OnUi(() => RaiseMany(nameof(WifiConnected), nameof(WifiStatusText), nameof(WifiStatusBrush)));
        _connectivityTimer.Start();
        _qAutoCloseTimer.Tick += (_, _) =>
        {
            _qAutoCloseTimer.Stop();
            if (Settings.QAutoCloseAfterResponse && QState == QRunState.Complete) ClearQ();
        };
        LaunchCommand = new RelayCommand<string>(LaunchApp);
        OpenMeetingCommand = new RelayCommand(() => { if (!string.IsNullOrWhiteSpace(_meeting?.JoinUrl)) OpenUrl(_meeting!.JoinUrl); });
        SeekCommand = new RelayCommand<double>(f => _ = _mediaService.SeekFractionAsync(f));
        OpenMediaAppCommand = new RelayCommand(() => { if (Settings.ClickArtOpensApp) _mediaService.LaunchSource(); });
        OpenWifiSettingsCommand = new RelayCommand(() => LaunchApp("ms-settings:network-wifi"));
        OpenBluetoothSettingsCommand = new RelayCommand(() => LaunchApp("ms-settings:bluetooth"));
        ToggleFavoriteCommand = new RelayCommand(() => IsFavorite = !IsFavorite);
        SelectOutputDeviceCommand = new RelayCommand<string>(id => { if (!string.IsNullOrEmpty(id)) _audioService.SetDefaultOutputDevice(id); });
        ToggleFocusCommand = new RelayCommand(() =>
        {
            Settings.FocusModeEnabled = !Settings.FocusModeEnabled;
            _ = PersistSettingsAsync();
            RaiseFocusModeProperties();
        });
        OpenNotificationHistoryCommand = new RelayCommand(RefreshNotificationHistory);
        ClearNotificationHistoryCommand = new RelayCommand(() =>
        {
            _notificationHistoryService.Clear();
            RefreshNotificationHistory();
        });
        DismissCurrentNotificationCommand = new RelayCommand(DismissCurrentNotification);
        OpenNotificationCommand = new RelayCommand<NotificationHistoryItem>(OpenNotification);
        DismissHistoryItemCommand = new RelayCommand<NotificationHistoryItem>(item =>
        {
            if (item is null) return;
            _notificationHistoryService.Dismiss(item.Id);
            RefreshNotificationHistory();
        });
        RefreshNotificationHistory();
        _batteryService.Changed += OnBatteryChanged;
        _clockService.Tick += OnClockTick;
        _timerAlarmService.Changed += OnTimerAlarmChanged;
        _themeService.SystemThemeChanged += OnSystemThemeChanged;
        _qSession.Changed += OnQChanged;
        ApplySettings();
    }

    public AppSettings Settings { get; }
    public QRunState QState => _qSnapshot.State;
    public QMode QCurrentMode => _qSnapshot.Mode;
    public string QStatusText => _qSnapshot.Status;
    public string QHeaderStatusText => QState switch
    {
        QRunState.Capturing => "Reading…",
        QRunState.Ready => "Ready",
        QRunState.Listening => "Listening…",
        QRunState.Thinking => "Thinking…",
        QRunState.Streaming => "Responding…",
        QRunState.Complete => "Complete",
        QRunState.Cancelled => "Cancelled",
        QRunState.Error => "Needs attention",
        _ => "Ready"
    };
    public string QResponse => _qSnapshot.Response;
    public string QPromptText => _qSnapshot.Prompt;
    public string QPromptDisplay => string.IsNullOrWhiteSpace(QPromptText) ? "Ask Q about what you’re looking at." : QPromptText;
    public bool QHasPrompt => !string.IsNullOrWhiteSpace(QPromptText);
    public bool QShowInlineThinking => QState is QRunState.Capturing or QRunState.Thinking or QRunState.Streaming;
    public bool QCanStop => QState is QRunState.Capturing or QRunState.Listening or QRunState.Thinking or QRunState.Streaming;
    public bool QCanCopyResponse => !string.IsNullOrWhiteSpace(QResponse);
    public bool QCanRetry => !string.IsNullOrWhiteSpace(QPromptText) && QState is QRunState.Complete or QRunState.Cancelled or QRunState.Error;
    public bool QShowResponseActions => QCanStop || QCanCopyResponse || QCanRetry;
    public string QInlineStatusText => QState switch
    {
        QRunState.Capturing => "Reading…",
        QRunState.Thinking => "Thinking…",
        QRunState.Streaming => "Responding…",
        _ => string.Empty
    };
    public string QResponseDisplay => string.IsNullOrWhiteSpace(QResponse)
        ? QState switch
        {
            QRunState.Capturing => string.Empty,
            QRunState.Listening => "Listening for your question…",
            QRunState.Thinking or QRunState.Streaming => string.Empty,
            QRunState.Error when !string.IsNullOrWhiteSpace(QError) => QError,
            _ => "Ask Q about what you’re looking at."
        }
        : CleanQResponseText(QResponse);
    public string QError => _qSnapshot.Error ?? string.Empty;
    public string QSourceText => _qSnapshot.Context is { } context
        ? string.IsNullOrWhiteSpace(context.ProcessName) ? "Active window" : context.ProcessName
        : "Screen context ready";
    public string QCompactText => QState switch
    {
        QRunState.Capturing => "Q · Reading…",
        QRunState.Thinking or QRunState.Streaming => "Q · Thinking…",
        QRunState.Complete => string.IsNullOrWhiteSpace(QResponse) ? "Q · Complete" : QResponse.Replace(Environment.NewLine, " "),
        QRunState.Error => "Q · Try again",
        _ => "Q · Ready"
    };
    public bool IsQActive => QState != QRunState.Idle;
    public bool ShowQSurface => IsQActive && !TimerEditorOpen;
    public bool QIsAsk => QCurrentMode == DynamicIsland.Q.Core.QMode.Ask;
    public bool QIsSay => QCurrentMode == DynamicIsland.Q.Core.QMode.Say;
    public bool QIsListening => QState == QRunState.Listening;
    public bool QNeedsConsent => !Settings.QDisclosureAccepted;
    public bool QSpeechAvailable => _qSpeech.IsAvailable;
    public IReadOnlyList<QShortcut> QShortcuts => Settings.QShortcuts ?? [];
    public bool QHasShortcuts => QShortcuts.Count > 0;
    public string QSelectedProvider => Settings.QSelectedProvider;
    public string QSelectedModel
    {
        get => Settings.QSelectedModel;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || string.Equals(Settings.QSelectedModel, value, StringComparison.Ordinal)) return;
            Settings.QSelectedModel = value.Trim();
            NormalizeProviderReasoningEffort();
            RaisePropertyChanged();
            _ = PersistSettingsAsync();
            RaiseQProperties();
        }
    }
    public IReadOnlyList<string> QModelOptions => IsCodexSelected && _codexModels.Count > 0
        ? _codexModels.Select(model => model.Id).ToArray()
        : QProviderPolicy.ModelSuggestions(Settings.QSelectedProvider, Settings.QSelectedModel);
    public string QReasoningEffort
    {
        get => Settings.QReasoningEffort;
        set
        {
            var effort = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();
            if (string.Equals(Settings.QReasoningEffort, effort, StringComparison.OrdinalIgnoreCase)) return;
            Settings.QReasoningEffort = effort;
            NormalizeProviderReasoningEffort();
            RaisePropertyChanged();
            _ = PersistSettingsAsync();
        }
    }
    public IReadOnlyList<string> QReasoningEffortOptions => IsCodexSelected
        ? CodexModelSelectionPolicy.EffortOptions(SelectedCodexModel)
        : QProviderPolicy.EffortOptions(Settings.QSelectedProvider, Settings.QSelectedModel);
    public bool QIsCodexSelected => string.Equals(Settings.QSelectedProvider, "codex", StringComparison.OrdinalIgnoreCase);
    private bool IsCodexSelected => QIsCodexSelected;
    private CodexModel? SelectedCodexModel => _codexModels.FirstOrDefault(model =>
        string.Equals(model.Id, Settings.QSelectedModel, StringComparison.OrdinalIgnoreCase));
    public bool QCodexIsConnected => _codexAccount?.Snapshot.IsConnected == true;
    public bool QShowCodexSignOut => QIsCodexSelected && QCodexIsConnected;
    public string QCodexChipText => _codexAccount?.Snapshot switch
    {
        { State: CodexAccountState.Connected, UsedPercent: int used } => $"ChatGPT · {used}% used",
        { State: CodexAccountState.Connected } => "ChatGPT connected",
        { State: CodexAccountState.LimitReached } => "Codex limit reached",
        { State: CodexAccountState.LoginPending, PendingLogin: { } login } => $"Code {login.UserCode}",
        { State: CodexAccountState.Checking } => "Checking Codex…",
        { State: CodexAccountState.RuntimeUnavailable } => "Codex unavailable",
        { State: CodexAccountState.Error } => "Codex needs attention",
        _ => "Sign in to ChatGPT"
    };
    public string QCodexAccountDetails
    {
        get
        {
            var snapshot = _codexAccount?.Snapshot;
            if (snapshot is null) return "Codex account status is unavailable.";
            if (!string.IsNullOrWhiteSpace(snapshot.Error)) return snapshot.Error;
            var plan = snapshot.Account?.PlanType ?? "ChatGPT plan";
            var reset = snapshot.RateLimit?.ResetsAt is { } at ? $" · resets {at.LocalDateTime:g}" : string.Empty;
            var runtime = snapshot.Runtime is { } info ? $" · Codex {info.Version ?? "unknown"} ({info.Source})" : string.Empty;
            return $"{plan}{reset}{runtime}";
        }
    }
    public bool IsEdgePill => Settings.DefaultPosition is PositionMode.TopLeft or PositionMode.TopRight;
    public bool IsHolePunchMode => Settings.IslandWidth <= 90 && Settings.IslandHeight <= 48;
    public bool ShowCompactMediaContent => ShowMedia && !IsQActive && !IsHolePunchMode;
    public bool ShowCompactQContent => IsQActive && !IsHolePunchMode;
    public bool ShowCompactHolePunchQOutput => IsQActive && IsHolePunchMode;
    public bool ShowCompactArtSurface => !IsQActive && ((ShowMedia && !IsHolePunchMode) || (IsHolePunchMode && HasArtwork));
    public bool ShowCompactStatusContent => !IsHolePunchMode;

    private static string CleanQResponseText(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return string.Empty;
        response = TextEncodingRepair.RepairUtf8ReadAsWindows1252(response);
        var cleaned = response.Replace("```", string.Empty, StringComparison.Ordinal)
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);
        cleaned = Regex.Replace(cleaned, @"(?m)^\s{0,3}#{1,6}\s+", string.Empty);
        return cleaned.Trim();
    }
    public MediaInfo Media { get => _media; private set { if (SetProperty(ref _media, value)) UpdateArtwork(value.Artwork); } }
    public AudioState Audio { get => _audio; private set => SetProperty(ref _audio, value); }
    public BatteryState Battery { get => _battery; private set => SetProperty(ref _battery, value); }
    public bool IsExpanded { get => _isExpanded; set { if (SetProperty(ref _isExpanded, value)) { if (value && Settings.QuoteRotation == QuoteRotation.EveryExpand) AdvanceQuote(); RaiseExpansionProperties(); } } }
    public bool IsCompact => !IsExpanded;
    // When a settings window is open we pin the island expanded so size/appearance changes are visible live.
    public bool KeepExpanded { get => _keepExpanded; set { if (SetProperty(ref _keepExpanded, value)) { RaisePropertyChanged(nameof(PinExpanded)); if (value) IsExpanded = true; } } }
    public bool IsDarkTheme { get => _isDarkTheme; private set => SetProperty(ref _isDarkTheme, value); }
    private MediaBrush? CustomTextBrush => Settings.UseCustomColors && !string.IsNullOrWhiteSpace(Settings.TextColorHex)
        ? FrozenBrush(Settings.TextColorHex) : null;
    public MediaBrush PrimaryTextBrush => CustomTextBrush ?? (IsDarkTheme ? FrozenBrush("#F8FBFF") : FrozenBrush("#172033"));
    public MediaBrush SecondaryTextBrush => CustomTextBrush ?? (IsDarkTheme ? FrozenBrush("#AAB5C6") : FrozenBrush("#526078"));
    public MediaBrush AccentTextBrush => AccentBrush;
    // Effective accent: album-art adaptive > custom > default. Drives the accent brush used across the island.
    private string EffectiveAccentHex =>
        Settings.AdaptiveAccent && _adaptiveAccent is not null ? _adaptiveAccent
        : Settings.UseCustomColors ? Settings.AccentColorHex
        : "#5AA7FF";
    public MediaBrush AccentBrush => FrozenBrush(EffectiveAccentHex);
    public MediaBrush AccentSoftBrush => FrozenBrush(WithAlpha(EffectiveAccentHex, 0x33));
    public MediaBrush IslandSurfaceBrush => IsDarkTheme ? FrozenBrush("#FA000000") : FrozenBrush("#FFF5F5F7");
    public MediaBrush IslandCardBrush => IsDarkTheme ? FrozenBrush("#FF1C1C1E") : FrozenBrush("#FFFFFFFF");
    public MediaBrush IslandDividerBrush => IsDarkTheme ? FrozenBrush("#FF2D2D30") : FrozenBrush("#FFD1D1D6");
    // Shell controls and progress bars need real theme-aware contrast. The old shared
    // AppleWhite resources disappeared against the light island surface.
    public MediaBrush ShellControlBrush => IsDarkTheme ? FrozenBrush("#FFF5F5F7") : FrozenBrush("#FF1C1C1E");
    public MediaBrush ShellControlHoverBrush => IsDarkTheme ? FrozenBrush("#1FFFFFFF") : FrozenBrush("#14000000");
    public MediaBrush ProgressFillBrush => IsDarkTheme ? FrozenBrush("#FFF5F5F7") : FrozenBrush("#FF3A3A3C");
    public MediaBrush ProgressTrackBrush => IsDarkTheme ? FrozenBrush("#FF36363A") : FrozenBrush("#FFD1D1D6");
    public System.Windows.Media.FontFamily UiFontFamily
    {
        get { try { return new System.Windows.Media.FontFamily(Settings.FontFamilyName); } catch { return new System.Windows.Media.FontFamily("Segoe UI Variable Text"); } }
    }
    public MediaBrush PanelBrush => IsDarkTheme ? FrozenBrush("#16FFFFFF") : FrozenBrush("#B8FFFFFF");
    public MediaBrush PanelBorderBrush => IsDarkTheme ? FrozenBrush("#28FFFFFF") : FrozenBrush("#280C213B");
    public ImageSource? Artwork => _artwork;
    public bool HasArtwork => _artwork is not null;
    public bool ShowCompactArt => Settings.ShowAlbumArtInCompact && HasArtwork && PrimaryActivity == IslandActivity.Media;
    public double AlbumScale => Math.Clamp(Settings.AlbumArtScale, 70, 130) / 100.0;
    public double ExpandedAlbumScale => AlbumScale * Math.Clamp(Settings.ExpandedAlbumArtSize, 40, 160) / 100.0;
    public double PreviewIslandWidth => Math.Clamp(Settings.IslandWidth, 72, 360);
    public double PreviewIslandHeight => Math.Clamp(Settings.IslandHeight, 38, 90);
    // Keep the two album-size controls useful without allowing them to break the compact/expanded layouts.
    // Keep artwork comfortably inside the compact pill so a large album-art preference cannot
    // crowd out the title and status lane on small custom heights.
    public double CompactAlbumSize => Math.Clamp((Settings.IslandHeight - 12) * AlbumScale, 36, 52);
    public double ExpandedAlbumSize => Math.Clamp(120 * ExpandedAlbumScale, 48, 140);
    // Preview sizes must match real island sizes exactly, otherwise the settings preview truncates
    // titles differently from the live pill (e.g. "Outside T..." in preview vs "Outs..." live).
    public double PreviewCompactAlbumSize => CompactAlbumSize;
    public double PreviewExpandedAlbumSize => ExpandedAlbumSize;
    // Album / icon corner radius deliberately stops at a squircle, never a circle.
    public double AlbumRadiusFraction => Math.Clamp(Settings.AlbumCornerRadius, 0, 30) / 100.0;
    public double CompactAlbumRadius => (CompactAlbumSize / 2) * AlbumRadiusFraction;
    public double ExpandedAlbumRadius => (ExpandedAlbumSize / 2) * AlbumRadiusFraction;
    public double PreviewCompactAlbumRadius => CompactAlbumRadius;
    public double PreviewExpandedAlbumRadius => ExpandedAlbumRadius;
    public CornerRadius CompactIconCorner => new(CompactAlbumRadius);
    public CornerRadius ExpandedIconCorner => new(ExpandedAlbumRadius);
    public CornerRadius PreviewCompactIconCorner => CompactIconCorner;
    public CornerRadius PreviewExpandedIconCorner => ExpandedIconCorner;
    public Geometry CompactRingGeometry => RoundedSquare(32, 32 * AlbumRadiusFraction);
    public Geometry ExpandedRingGeometry => RoundedSquare(66, 66 * AlbumRadiusFraction);
    public double CompactRingPerimeterUnits => RoundedSquarePerimeter(32, 32 * AlbumRadiusFraction) / 2.5;
    public double ExpandedRingPerimeterUnits => RoundedSquarePerimeter(66, 66 * AlbumRadiusFraction) / 3.0;
    private bool HasMediaDuration => Media.HasSession && Media.Duration.TotalSeconds > 0;
    public bool ShowCompactMediaRing => ShowCompactArt && Settings.ShowMediaProgressRing && HasMediaDuration;
    public bool ShowCompactTimerRing => PrimaryActivity == IslandActivity.Timer && Settings.ShowTimerRing;
    public bool ShowCompactRingTrack => ShowCompactMediaRing || ShowCompactTimerRing;
    public bool ShowTimerOrb => IsCompact && !TimerEditorOpen && DisplayTimer is not null;
    public double TimerRemainingProgress => Math.Clamp(100d - TimerProgress, 0d, 100d);
    public double TimerOrbPerimeterUnits => Math.PI * 36d / 3d;
    public bool ShowExpandedMediaRing => Settings.ShowMedia && HasArtwork && Settings.ShowMediaProgressRing && HasMediaDuration;
    public bool ShowMedia => Settings.ShowMedia;
    public bool ScrollTitles => Settings.ScrollLongTitles;
    public bool ShowVolume => Settings.ShowVolume && Audio.Availability == AudioAvailability.Available;
    public bool ShowBattery => Settings.ShowBattery && Battery.IsAvailable;
    public bool IsCharging => ShowBattery && Battery.IsCharging;
    public bool ShowBatteryLevel => ShowBattery && !Battery.IsCharging;
    // Keep the compact island calm: when plugged in, use the right edge for a
    // tiny charging readout instead of trying to squeeze in both the clock and
    // battery percentage.
    public bool IsPowerConnected => ShowBattery && Battery.IsPluggedIn;
    public bool ShowCompactChargingIndicator => IsPowerConnected;
    public bool ShowCompactSecondary => !IsPowerConnected;
    public bool ShowClock => Settings.ShowClock;
    public bool ShowTimerAlarm => Settings.ShowTimerAlarm;
    public bool DebugOverlay => Settings.DebugOverlay;
    public bool IsReducedMotion => Settings.AnimationIntensity == AnimationIntensity.Reduced;
    public bool IsPlaying => Media.IsPlaying;
    public bool IsMuted => Audio.SystemMuted;
    public bool IsAudioActive => Audio.Availability == AudioAvailability.Available && Audio.ActiveAudioOutput && !Audio.SystemMuted;
    public bool ShowAudioStatusText => !IsAudioActive;
    public bool ShowDate => Settings.ShowDate;
    public bool IsAppleStyle => Settings.IslandVisualMode == IslandVisualMode.Apple;
    public bool IsStatsStyle => Settings.IslandVisualMode == IslandVisualMode.Stats;
    // ===== Mic / camera in-use indicators (read from the Windows privacy consent store) =====
    public bool ShowCameraInUse => Settings.ShowPrivacyIndicators && _privacy.Camera;
    public bool ShowMicInUse => Settings.ShowPrivacyIndicators && _privacy.Microphone;
    public bool ShowPrivacyInUse => ShowCameraInUse || ShowMicInUse;
    public string PrivacyActivityText => (ShowCameraInUse, ShowMicInUse) switch
    {
        (true, true) => "Camera and microphone in use",
        (true, false) => "Camera in use",
        (false, true) => "Microphone in use",
        _ => "No sensor activity"
    };
    public IReadOnlyList<string> PrivacyCameraApps => ShowCameraInUse ? _privacy.CameraApps : Array.Empty<string>();
    public IReadOnlyList<string> PrivacyMicApps => ShowMicInUse ? _privacy.MicrophoneApps : Array.Empty<string>();
    public bool HasPrivacySource => ShowPrivacyInUse && PrivacySourceNames.Count > 0;
    public IReadOnlyList<string> PrivacySourceNames
    {
        get
        {
            if (!ShowPrivacyInUse) return Array.Empty<string>();
            return _privacy.CameraApps
                .Concat(_privacy.MicrophoneApps)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToArray();
        }
    }
    /// <summary>Compact one-liner for the expanded card, e.g. "Chrome" or "Chrome • Teams".</summary>
    public string PrivacySourceText => HasPrivacySource
        ? string.Join(" • ", PrivacySourceNames)
        : string.Empty;
    /// <summary>Sensor-grouped detail for tooltips / screen readers.</summary>
    public string PrivacyDetailText
    {
        get
        {
            if (!ShowPrivacyInUse) return PrivacyActivityText;
            var parts = new List<string>();
            if (ShowCameraInUse) parts.Add("Camera: " + FormatAppList(_privacy.CameraApps));
            if (ShowMicInUse) parts.Add("Microphone: " + FormatAppList(_privacy.MicrophoneApps));
            var sources = string.Join("  •  ", parts);
            return $"{PrivacyActivityText} — {sources}";
        }
    }
    private static string FormatAppList(IReadOnlyList<string> apps)
    {
        if (apps.Count == 0) return "Unknown app";
        var shown = apps.Take(3);
        var text = string.Join(", ", shown);
        if (apps.Count > 3) text += $" +{apps.Count - 3} more";
        return text;
    }
    public string PrivacyActivityGlyph => ShowCameraInUse
        ? char.ConvertFromUtf32(0xE714)
        : char.ConvertFromUtf32(0xE720);
    public MediaBrush PrivacyIndicatorBrush => FrozenBrush(ShowCameraInUse ? "#FF30D158" : "#FFFF9F0A");
    public int PrivacySeq => _privacySeq;

    // ===== AirPods =====
    public AirPodsState AirPods => _airPods;
    public bool ShowAirPods => _airPods.IsConnected && _airPods.IsAvailable && !FocusModeEnabled;
    public bool ShowAirPodsCard => ShowAirPods;
    public string AirPodsName => _airPods.DisplayName;
    public string AirPodsModelName => _airPods.ModelName;
    public string AirPodsLeftBatteryText => FormatAirPodsBattery(_airPods.LeftBatteryPercent);
    public string AirPodsRightBatteryText => FormatAirPodsBattery(_airPods.RightBatteryPercent);
    public string AirPodsCaseBatteryText => FormatAirPodsBattery(_airPods.CaseBatteryPercent);
    public string AirPodsBatterySummary
    {
        get
        {
            var parts = new List<string>();
            if (_airPods.LeftBatteryAvailable) parts.Add($"L {FormatAirPodsBattery(_airPods.LeftBatteryPercent)}{(_airPods.LeftCharging ? " ⚡" : "")}");
            if (_airPods.RightBatteryAvailable) parts.Add($"R {FormatAirPodsBattery(_airPods.RightBatteryPercent)}{(_airPods.RightCharging ? " ⚡" : "")}");
            if (_airPods.CaseBatteryAvailable) parts.Add($"Case {FormatAirPodsBattery(_airPods.CaseBatteryPercent)}{(_airPods.CaseCharging ? " ⚡" : "")}");
            return parts.Count > 0 ? string.Join("  •  ", parts) : "Connected";
        }
    }
    public string AirPodsCompactBatteryText
    {
        get
        {
            var parts = new List<string>();
            if (_airPods.LeftBatteryAvailable) parts.Add($"L {FormatAirPodsBattery(_airPods.LeftBatteryPercent)}");
            if (_airPods.RightBatteryAvailable) parts.Add($"R {FormatAirPodsBattery(_airPods.RightBatteryPercent)}");
            if (_airPods.CaseBatteryAvailable) parts.Add($"C {FormatAirPodsBattery(_airPods.CaseBatteryPercent)}");
            return parts.Count > 0 ? string.Join("  ", parts) : "Connected";
        }
    }
    public bool ShowAirPodsLeft => _airPods.LeftBatteryAvailable;
    public bool ShowAirPodsRight => _airPods.RightBatteryAvailable;
    public bool ShowAirPodsCase => _airPods.CaseBatteryAvailable;
    public bool AirPodsLeftCharging => _airPods.LeftCharging;
    public bool AirPodsRightCharging => _airPods.RightCharging;
    public bool AirPodsCaseCharging => _airPods.CaseCharging;

    private static string FormatAirPodsBattery(int? percent)
    {
        if (!percent.HasValue) return "—";
        var value = Math.Clamp(percent.Value, 0, 100);
        if (value < 100 && value % 10 == 0)
            return $"{value}-{value + 9}%";
        return $"{value}%";
    }
    public string AirPodsStatusText
    {
        get
        {
            if (!_airPods.IsConnected) return string.Empty;
            if (_airPods.BothInCase && _airPods.CaseLidOpen) return "Case open";
            if (_airPods.BothInEar) return "In ear";
            if (_airPods.LeftInEar) return "Left in ear";
            if (_airPods.RightInEar) return "Right in ear";
            if (_airPods.BothInCase) return "In case";
            return "Connected";
        }
    }

    // ===== Per-element font sizes (each = default × element% × interface%) =====
    private double Scaled(int elementPercent, double baseSize) =>
        baseSize * Math.Clamp(elementPercent, 60, 160) / 100.0 * Math.Clamp(Settings.InterfaceScale, 70, 150) / 100.0;
    public double InterfaceScaleFactor => Math.Clamp(Settings.InterfaceScale, 70, 150) / 100.0;
    public double ClockFontSize => Scaled(Settings.ClockSize, 17);
    public double DateFontSize => Scaled(Settings.DateSize, 10);
    public double BatteryGlyphFontSize => Scaled(Settings.BatterySize, 11);
    public double BatteryTextFontSize => Scaled(Settings.BatterySize, 10);
    public double ChargingGlyphFontSize => Scaled(Settings.BatterySize, 11);
    public double ChargingTextFontSize => Scaled(Settings.BatterySize, 11);
    public double CompactChargingTextFontSize => Scaled(Settings.BatterySize, 10);
    public double MediaTitleFontSize => Scaled(Settings.MediaTitleSize, 14);
    public double MediaArtistFontSize => Scaled(Settings.MediaArtistSize, 11);
    public double VolumeFontSize => Scaled(Settings.VolumeSize, 13);
    public double CompactGlyphFontSize => Scaled(Settings.CompactTextSize, 12);
    public double CompactPrimaryFontSize => Scaled(Settings.CompactTextSize, 13);
    public double CompactSecondaryFontSize => Scaled(Settings.CompactTextSize, 11);
    public double CompactClockFontSize => Scaled(Settings.CompactTextSize, 11);
    public double ExpandedMediaTitleFontSize => Scaled(Settings.MediaTitleSize, 18);
    public double ExpandedMediaArtistFontSize => Scaled(Settings.MediaArtistSize, 13);

    // ===== Focus mode / Weather =====
    public bool FocusModeEnabled => Settings.FocusModeEnabled;
    public string FocusModeText => FocusModeEnabled ? "Focus on" : "Focus off";
    public bool ShowWeather => !FocusModeEnabled && Settings.ShowWeather;
    public string WeatherGlyph => _weather?.Glyph ?? char.ConvertFromUtf32(0xE753);
    public string WeatherTempText => _weather?.TempText
        ?? (string.IsNullOrWhiteSpace(Settings.WeatherLocation) ? "Set location" : "Loading…");
    public string WeatherDescText => _weather?.Description
        ?? (string.IsNullOrWhiteSpace(Settings.WeatherLocation) ? "Open Settings to choose a city" : Settings.WeatherLocation);
    public string WeatherCityText => _weather?.City ?? Settings.WeatherLocation;

    // ===== System monitor =====
    public bool ShowSystemMonitor => !FocusModeEnabled && Settings.ShowSystemMonitor;
    public bool ShowCompactRam => Settings.ShowRamInCompact;
    public string CpuText => $"{_sysStats.CpuPercent}%";
    public string RamText => $"{_sysStats.RamPercent}%";
    public string NetText => _sysStats.NetworkText;
    // Numeric RAM load (0–100) for the mini-card progress bar in the redesigned status panel.
    public double RamPercentValue => Math.Clamp(_sysStats.RamPercent, 0, 100);
    // Rolling network-throughput sparkline (last N samples, drawn into a small box).
    private const int SparkHistoryLength = 24;
    private readonly Queue<double> _netHistory = new();
    private PointCollection _netSparkline = Sparkline([], 46, 16);
    public PointCollection NetSparkline => _netSparkline; // auto-scaled to the window's peak

    // Builds a polyline for a series in a w×h box. Without fixed bounds it auto-scales to the sample range.
    private static PointCollection Sparkline(IEnumerable<double> source, double w, double h, double? fixedMin = null, double? fixedMax = null)
    {
        var samples = source as IReadOnlyList<double> ?? source.ToArray();
        var pts = new PointCollection();
        if (samples.Count == 0)
        {
            pts.Add(new System.Windows.Point(0, h));
            pts.Add(new System.Windows.Point(w, h));
            pts.Freeze();
            return pts;
        }
        var min = fixedMin ?? samples.Min();
        var max = fixedMax ?? samples.Max();
        var range = Math.Max(1e-9, max - min);
        var step = samples.Count > 1 ? w / (samples.Count - 1) : w;
        for (int i = 0; i < samples.Count; i++)
        {
            var x = i * step;
            var norm = Math.Clamp((samples[i] - min) / range, 0, 1);
            pts.Add(new System.Windows.Point(x, h - norm * (h - 1) - 0.5));
        }
        pts.Freeze();
        return pts;
    }

    // ===== Countdown =====
    public bool ShowCountdown => !FocusModeEnabled && Settings.ShowCountdown && !string.IsNullOrWhiteSpace(Settings.CountdownDate);
    public string CountdownText
    {
        get
        {
            if (!DateTime.TryParse(Settings.CountdownDate, out var target)) return string.Empty;
            var days = (target.Date - _now.Date).Days;
            var label = string.IsNullOrWhiteSpace(Settings.CountdownLabel) ? "" : " · " + Settings.CountdownLabel;
            return days > 0 ? $"{days}d{label}" : days == 0 ? $"Today{label}" : $"{-days}d ago{label}";
        }
    }

    // ===== Quotes =====
    public bool ShowQuotes => Settings.QuotePlacement != QuotePlacement.Off && _quotes.Length > 0;
    // Time-sensitive activities must stay visible even when compact quotes are enabled.
    public bool ShowQuoteInCompact => ShowQuotes && PrimaryActivity is not (IslandActivity.Alarm or IslandActivity.Timer or IslandActivity.Q)
        && Settings.QuotePlacement is (QuotePlacement.Compact or QuotePlacement.Both);
    public bool ShowQuoteInExpanded => !IsQActive && ShowQuotes
        && Settings.QuotePlacement is (QuotePlacement.Expanded or QuotePlacement.Both);
    public string QuoteText => ShowQuotes ? _quotes[_quoteIndex % _quotes.Length].Text : string.Empty;
    public string QuoteAuthor => ShowQuotes ? _quotes[_quoteIndex % _quotes.Length].Author : string.Empty;
    public string QuoteAuthorDisplay => QuoteAuthor.Length > 0 ? "— " + QuoteAuthor : string.Empty;
    public bool ShowQuoteAuthor => QuoteAuthor.Length > 0;
    public double QuoteTextFontSize => Scaled(Settings.QuoteSize, 13);
    public double QuoteAuthorFontSize => Scaled(Settings.QuoteSize, 10);

    private void AdvanceQuote()
    {
        if (_quotes.Length == 0) return;
        _quoteIndex = (_quoteIndex + 1) % _quotes.Length;
        RaiseMany(nameof(QuoteText), nameof(QuoteAuthor), nameof(QuoteAuthorDisplay), nameof(ShowQuoteAuthor),
            nameof(CompactGlyph), nameof(CompactPrimaryText), nameof(CompactSecondaryText));
    }

    private void ConfigureQuoteRotation()
    {
        _quoteTimer.Stop();
        var interval = Settings.QuoteRotation switch
        {
            QuoteRotation.EveryMinute => TimeSpan.FromMinutes(1),
            QuoteRotation.Every5Minutes => TimeSpan.FromMinutes(5),
            QuoteRotation.Every15Minutes => TimeSpan.FromMinutes(15),
            QuoteRotation.Every30Minutes => TimeSpan.FromMinutes(30),
            QuoteRotation.EveryHour => TimeSpan.FromHours(1),
            _ => TimeSpan.Zero
        };
        if (ShowQuotes && _quotes.Length > 1 && interval > TimeSpan.Zero)
        {
            _quoteTimer.Interval = interval;
            _quoteTimer.Start();
        }
    }

    // ===== World clocks =====
    public bool ShowWorldClocks => !FocusModeEnabled && Settings.ShowWorldClocks && WorldClocks.Count > 0;
    public IReadOnlyList<WorldClock> WorldClocks => _worldClocks;
    private static string ShortZone(string id)
    {
        var t = id.Replace(" Standard Time", "").Replace(" Daylight Time", "");
        var slash = t.LastIndexOf('/');
        return (slash >= 0 ? t[(slash + 1)..] : t).Replace('_', ' ');
    }

    // ===== Stocks =====
    public bool ShowStocks => !FocusModeEnabled && Settings.ShowStocks && _stocks.Count > 0;
    public IReadOnlyList<StockTile> Stocks => _stockTiles;

    // ===== Next meeting =====
    public bool ShowNextMeeting => !FocusModeEnabled && Settings.ShowNextMeeting;
    public string MeetingTitle => _meeting?.Title ?? CalendarStatusText;
    public string MeetingWhen => _meeting?.CountdownText ?? (_calendarService.Status.State == IntegrationState.Ready ? "" : "Open Settings to resolve");
    public bool HasMeetingJoin => !string.IsNullOrWhiteSpace(_meeting?.JoinUrl);

    // ===== Battery time =====
    public bool ShowBatteryTime => Settings.ShowBatteryTime && Battery.IsAvailable && !Battery.IsCharging && Battery.MinutesRemaining > 0;
    public string BatteryTimeText => Battery.TimeRemainingText;

    // ===== Quick launch =====
    public bool ShowQuickLaunch => !FocusModeEnabled && Settings.ShowQuickLaunch && LaunchItems.Count > 0;
    public IReadOnlyList<LaunchEntry> LaunchItems => _launchItems;

    public bool ShowClipboard => Settings.ShowClipboard;
    // True when any of the grouped live-activity widgets is enabled (so the panel can be shown/hidden cleanly).
    public bool ShowWidgetsPanel => !FocusModeEnabled && (ShowWeather || ShowStocks || ShowCountdown || ShowNextMeeting
        || ShowWorldClocks || ShowBatteryTime || ShowConnectivity);
    public Thickness LiveWidgetRailMargin => new(ShowAirPodsCard ? 8 : 0, 8, 0, 0);
    public double LiveWidgetRailWidth => WindowSizingPolicy.WidgetViewport(AccessoryLaneWidth, ShowAirPodsCard);
    private double _accessoryLaneWidth = 688;
    private double AccessoryLaneWidth => _accessoryLaneWidth;
    public void SetAccessoryLaneWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0 || Math.Abs(_accessoryLaneWidth - width) < 1) return;
        _accessoryLaneWidth = width; RaiseLiveWidgetLayoutProperties();
    }    private int LiveWidgetCount =>
        (ShowWeather ? 1 : 0) +
        (ShowCountdown ? 1 : 0) +
        (ShowNextMeeting ? 1 : 0) +
        (ShowBatteryTime ? 1 : 0) +
        (ShowWorldClocks ? WorldClocks.Count : 0) +
        (ShowStocks ? Stocks.Count : 0) +
        (ShowConnectivity ? 1 : 0);
    private double GetLiveWidgetCardWidth(double minimum) => ShowAirPodsCard ? minimum : WindowSizingPolicy.WidgetWidth(LiveWidgetRailWidth, LiveWidgetCount, minimum);    public double WeatherWidgetWidth => GetLiveWidgetCardWidth(180d);
    public double CountdownWidgetWidth => GetLiveWidgetCardWidth(136d);
    public double MeetingWidgetWidth => GetLiveWidgetCardWidth(190d);
    public double BatteryTimeWidgetWidth => GetLiveWidgetCardWidth(142d);
    public double SmallLiveWidgetWidth => GetLiveWidgetCardWidth(132d);
    public bool ShowConnectivity => Settings.ShowConnectivity;
    public bool WifiConnected => IsWifiConnected();
    public string WifiStatusText => WifiConnected ? "Connected" : "Offline";
    public MediaBrush WifiStatusBrush => FrozenBrush(WifiConnected ? "#FF30D158" : "#FFFF9F0A");
    public bool BluetoothAvailable => _airPods.IsAvailable;
    public string BluetoothStatusText => BluetoothAvailable ? "Available" : "Unavailable";
    public MediaBrush BluetoothStatusBrush => FrozenBrush(BluetoothAvailable ? "#FF30D158" : "#FFFF9F0A");
    private static bool IsWifiConnected()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces().Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && n.OperationalStatus == OperationalStatus.Up); }
        catch { return false; }
    }
    private void RaiseLiveWidgetLayoutProperties() => RaiseMany(
        nameof(LiveWidgetRailWidth), nameof(LiveWidgetRailMargin), nameof(WeatherWidgetWidth), nameof(CountdownWidgetWidth),
        nameof(MeetingWidgetWidth), nameof(BatteryTimeWidgetWidth), nameof(SmallLiveWidgetWidth));
    // Secondary widgets surfaced under the status panel in the redesigned expanded island (weather and the
    // system monitor get their own cards, so they're excluded here). Collapses the strip when nothing's on.
    public bool ShowStatusExtras => ShowBatteryLevel || IsCharging
        || (!FocusModeEnabled && (ShowStocks || ShowCountdown || ShowNextMeeting || ShowWorldClocks))
        || ShowBatteryTime;

    // ===== Outer island corner radius (user-chosen via the slider, 0–48 DIP) =====
    private double ClampedCornerRadius => Math.Clamp(Settings.IslandCornerRadius, 0, 48);
    public CornerRadius IslandCornerRadius => new(ClampedCornerRadius);
    public CornerRadius IslandInnerCornerRadius => new(Math.Max(0, ClampedCornerRadius - 1));

    // ===== Notification banner (transient) =====
    public bool ShowNotification => _notification is not null && Settings.ShowNotifications && !FocusModeEnabled && !DeferOrdinaryBanners;
    public string NotificationApp => _notification?.App ?? string.Empty;
    public string NotificationTitle => _notification?.Title ?? string.Empty;
    public string NotificationBody => _notification?.Body ?? string.Empty;
    public int NotificationSeq => _notificationSeq;
    public ObservableCollection<NotificationHistoryItem> NotificationHistory { get; } = [];
    public bool HasNotificationHistory => NotificationHistory.Count > 0;
    public bool ShowEmptyNotificationHistory => !HasNotificationHistory;

    // ===== Unified banner (Windows notification, volume warning, or AirPods) =====
    // Privacy activity has its own attached indicator/drop-down presentation in IslandWindow.
    public bool IsAirPodsBannerActive => _airPodsBannerActive && !FocusModeEnabled && !ShowNotification
        && !(_volumeWarningActive && Settings.VolumeWarningEnabled);
    public bool ShowBanner => !DeferOrdinaryBanners && (ShowNotification || (_volumeWarningActive && Settings.VolumeWarningEnabled && !FocusModeEnabled)
        || IsAirPodsBannerActive);
    public string BannerApp
    {
        get
        {
            if (ShowNotification) return NotificationApp;
            if (_volumeWarningActive && Settings.VolumeWarningEnabled) return "Volume";
            if (IsAirPodsBannerActive) return _airPods.DisplayName;
            return string.Empty;
        }
    }
    public string BannerTitle
    {
        get
        {
            if (ShowNotification) return NotificationTitle;
            if (_volumeWarningActive && Settings.VolumeWarningEnabled) return "High volume";
            if (IsAirPodsBannerActive) return BuildAirPodsBannerTitle();
            return string.Empty;
        }
    }
    public string BannerBody
    {
        get
        {
            if (ShowNotification) return NotificationBody;
            if (_volumeWarningActive && Settings.VolumeWarningEnabled) return $"Volume is at {_lastWarnedVolumePercent}% — consider lowering it to protect your hearing.";
            if (IsAirPodsBannerActive) return BuildAirPodsBannerBody();
            return string.Empty;
        }
    }
    // Increments once per new banner event so the view plays the entrance animation exactly once.
    public int BannerSeq => _bannerSeq;

    private string BuildAirPodsBannerTitle()
    {
        var parts = new List<string>();
        if (_airPods.LeftBatteryAvailable) parts.Add($"L {FormatAirPodsBattery(_airPods.LeftBatteryPercent)}{(_airPods.LeftCharging ? " ⚡" : "")}");
        if (_airPods.RightBatteryAvailable) parts.Add($"R {FormatAirPodsBattery(_airPods.RightBatteryPercent)}{(_airPods.RightCharging ? " ⚡" : "")}");
        if (_airPods.CaseBatteryAvailable) parts.Add($"Case {FormatAirPodsBattery(_airPods.CaseBatteryPercent)}{(_airPods.CaseCharging ? " ⚡" : "")}");
        if (parts.Count == 0) return _airPods.ModelName;
        return string.Join("   ", parts);
    }

    private string BuildAirPodsBannerBody()
    {
        var details = new List<string>();
        if (_airPods.BothInCase && _airPods.CaseLidOpen) details.Add("Case open");
        else if (_airPods.BothInCase) details.Add("In case");
        if (_airPods.LeftInEar || _airPods.RightInEar)
        {
            if (_airPods.BothInEar) details.Add("In ear");
            else if (_airPods.LeftInEar) details.Add("Left in ear");
            else if (_airPods.RightInEar) details.Add("Right in ear");
        }
        if (_airPods.LeftCharging || _airPods.RightCharging || _airPods.CaseCharging)
        {
            var ch = new List<string>();
            if (_airPods.LeftCharging) ch.Add("L charging");
            if (_airPods.RightCharging) ch.Add("R charging");
            if (_airPods.CaseCharging) ch.Add("Case charging");
            if (ch.Count > 0) details.Add(string.Join(", ", ch));
        }
        return details.Count > 0 ? string.Join("  •  ", details) : "Connected";
    }

    // ===== Audio spectrum (real, from loopback) =====
    public bool UseRealSpectrum => Settings.RealAudioSpectrum && _spectrumService.IsActive && IsAudioActive;
    public bool ShowAnimatedWave => IsAudioActive && !UseRealSpectrum;
    public bool ShowMusicVisualizer => Settings.ShowMusicVisualizer && IsAudioActive;
    public double VisualizerBar0Height => VisualizerHeight(0);
    public double VisualizerBar1Height => VisualizerHeight(1);
    public double VisualizerBar2Height => VisualizerHeight(2);
    public double VisualizerBar3Height => VisualizerHeight(3);
    public double VisualizerBar4Height => VisualizerHeight(4);
    public double VisualizerBar5Height => VisualizerHeight(5);
    public double VisualizerBar6Height => VisualizerHeight(6);
    private double VisualizerHeight(int index)
    {
        var value = UseRealSpectrum ? Band(index) : 0.15 + 0.42 * ((Math.Sin(_visualizerPhase + index * 0.78) + 1) / 2);
        return 7 + Math.Clamp(value, 0.08, 1.0) * 25;
    }
    private void RaiseVisualizerProperties() => RaiseMany(
        nameof(VisualizerBar0Height), nameof(VisualizerBar1Height), nameof(VisualizerBar2Height),
        nameof(VisualizerBar3Height), nameof(VisualizerBar4Height), nameof(VisualizerBar5Height),
        nameof(VisualizerBar6Height));
    public double SpectrumBand0 => Band(0);
    public double SpectrumBand1 => Band(1);
    public double SpectrumBand2 => Band(2);
    public double SpectrumBand3 => Band(3);
    public double SpectrumBand4 => Band(4);
    public double SpectrumBand5 => Band(5);
    public double SpectrumBand6 => Band(6);
    private double Band(int i) => i < _spectrum.Length ? Math.Clamp(_spectrum[i], 0.08, 1.0) : 0.1;

    // ===== Expanded module order (album is fixed at column 0; these occupy 1..3) =====
    private string[] Order => _expandedOrder;
    public int MediaColumn => ColumnOf("media", 1);
    public int VolumeColumn => ColumnOf("volume", 2);
    public int StatusColumn => ColumnOf("status", 3);
    private int ColumnOf(string key, int fallback)
    {
        var i = Array.IndexOf(Order, key);
        return i < 0 ? fallback : i + 1;
    }

    // ===== Behaviour =====
    public bool PinExpanded => KeepExpanded || (Settings.AlwaysExpanded && !IsHolePunchMode);
    public string MediaTitle => Media.HasSession ? (Media.DisplayTitle ?? "Media") : "No media playing";
    public string MediaArtist => Media.HasSession
        ? string.Join("  |  ", new[] { Media.Artist, Media.SourceAppName }.Where(x => !string.IsNullOrWhiteSpace(x)))
        : "Start playback in any supported app";
    public string PlayPauseGlyph => Media.IsPlaying ? "\uE769" : "\uE768";
    // Keep the progress binding read-only at runtime; WPF otherwise attempts to write back
    // when the ProgressBar initializes and aborts startup.
    public double MediaProgress
    {
        get => Media.Duration.TotalSeconds <= 0 ? 0
            : Math.Clamp(Media.Position.TotalSeconds / Media.Duration.TotalSeconds * 100, 0, 100);
        set { }
    }
    public bool ShowMediaTimes => Media.HasSession && Media.Duration.TotalSeconds > 0;
    public string MediaElapsedText => ShowMediaTimes ? FormatClockTime(Media.Position) : string.Empty;
    public string MediaTotalText => ShowMediaTimes ? FormatClockTime(Media.Duration) : string.Empty;
    public string MediaTimeRemaining
    {
        get
        {
            if (!ShowMediaTimes) return string.Empty;
            var remaining = Media.Duration - Media.Position;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            return "-" + FormatClockTime(remaining);
        }
    }
    // The trailing label honours the "Song time remaining" toggle: countdown when on, total length when off.
    public string MediaTrailingTimeText => Settings.ShowSongTimeRemaining ? MediaTimeRemaining : MediaTotalText;
    private static string FormatClockTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }
    // "NOW PLAYING" squircle chip, explicit badge and favourite heart for the redesigned media card.
    public bool ShowNowPlaying => Media.HasSession;
    public bool ShowExplicitBadge => Media.HasSession && Media.IsExplicit;
    private bool _isFavorite;
    public bool IsFavorite { get => _isFavorite; private set { if (SetProperty(ref _isFavorite, value)) RaisePropertyChanged(nameof(FavoriteGlyph)); } }
    public string FavoriteGlyph => IsFavorite ? "\uEB52" : "\uEB51"; // filled heart vs outline
    // Rewind / fast-forward controls (10s back, 30s forward). Enabled only when the active session reports
    // it can change position AND exposes a duration; otherwise the buttons disable and explain why via tooltip.
    private const int SeekBackSeconds = 10;
    private const int SeekForwardSeconds = 30;
    public bool CanSeek => Media.CanSeek && Media.Duration.TotalSeconds > 0;
    public string SeekBackTooltip => CanSeek ? $"Back {SeekBackSeconds} seconds" : "This source doesn't support seeking";
    public string SeekForwardTooltip => CanSeek ? $"Forward {SeekForwardSeconds} seconds" : "This source doesn't support seeking";
    public string VolumeText => $"{Audio.MasterVolumePercent}%";
    public string VolumeGlyph => Audio.SystemMuted ? "\uE74F" : Audio.MasterVolumePercent == 0 ? "\uE992" : "\uE767";
    public string AudioStatusText => Audio.StatusText;
    public string OutputDeviceText => string.IsNullOrWhiteSpace(Audio.OutputDeviceName) ? "System default" : Audio.OutputDeviceName;

    // ===== Output-device picker (populated on demand when the island's output row is clicked) =====
    public System.Collections.ObjectModel.ObservableCollection<OutputDeviceItem> OutputDevices { get; } = [];
    public void RefreshOutputDevices()
    {
        OutputDevices.Clear();
        var current = Audio.OutputDeviceId;
        foreach (var (id, name) in _audioService.GetOutputDevices())
            OutputDevices.Add(new OutputDeviceItem(id, name, string.Equals(id, current, StringComparison.Ordinal)));
    }
    public ICommand SelectOutputDeviceCommand { get; private set; } = null!;
    public string ClockText => _now.ToString(Settings.Use24HourClock
        ? (Settings.ShowSeconds ? "HH:mm:ss" : "HH:mm")
        : (Settings.ShowSeconds ? "h:mm:ss tt" : "h:mm tt"));
    // Split clock for the large expanded display: time without the meridiem, plus a separate AM/PM chip.
    public string ClockTimeText => _now.ToString(Settings.Use24HourClock
        ? (Settings.ShowSeconds ? "HH:mm:ss" : "HH:mm")
        : (Settings.ShowSeconds ? "h:mm:ss" : "h:mm"));
    public string ClockAmPm => Settings.Use24HourClock ? string.Empty : _now.ToString("tt");
    public string DateText => _now.ToString("ddd, MMM d");
    public string DateLongText => _now.ToString("dddd, MMMM d");
    public string BatteryText => !Battery.IsAvailable ? string.Empty : $"{Battery.Percentage}%";
    public string ChargingText => Battery.IsPluggedIn ? $"{Battery.Percentage}%" : string.Empty;
    public string BatteryGlyph => Battery.IsCharging ? "\uE83E" : "\uE850";
    public string TimerText => TimerAlarmService.FormatDuration(_timerAlarmService.Remaining(PrimaryTimer));
    public double TimerProgress => (PrimaryTimer.TotalSeconds <= 0 ? 0 : Math.Clamp(1 - _timerAlarmService.Remaining(PrimaryTimer).TotalSeconds / PrimaryTimer.TotalSeconds, 0, 1)) * 100;
    public string AlarmText => PrimaryAlarm.Phase switch
    {
        AlarmPhase.Ringing => "Alarm ringing",
        AlarmPhase.Snoozed => $"Snoozed until {PrimaryAlarm.SnoozeUntil:t}",
        AlarmPhase.Scheduled => $"Alarm {TimerAlarmService.FormatAlarmTime(PrimaryAlarm)}"
            + (PrimaryAlarm.Repeat == AlarmRepeat.Once ? "" : $" · {TimerAlarmService.FormatRepeat(PrimaryAlarm.Repeat)}"),
        _ => "No alarm"
    };

    public string CompactGlyph => IsQActive ? "Q" : ShowQuoteInCompact ? "\u201C"
        : PrimaryActivity switch
        {
            IslandActivity.Alarm => "\uEA8F",
            IslandActivity.Timer => "\uE916",
            IslandActivity.Muted => "\uE74F",
            IslandActivity.Audio => "\uE995",
            IslandActivity.Charging => "\uE83E",
            IslandActivity.Media => Media.IsPlaying ? "\uE768" : "\uE769",
            _ => "\uE121"
        };

    public string CompactPrimaryText => IsQActive
        ? QCompactText
        : ShowQuoteInCompact
        ? QuoteText
        : PrimaryActivity switch
        {
            IslandActivity.Alarm => PrimaryAlarm.Phase == AlarmPhase.Ringing
                ? "Alarm" : TimerAlarmService.FormatAlarmTime(PrimaryAlarm),
            IslandActivity.Timer => PrimaryTimer.Phase == TimerPhase.Completed
                ? "Timer done" : TimerText,
            IslandActivity.Muted => "Muted",
            IslandActivity.Media => Media.DisplayTitle,
            IslandActivity.Audio => "Audio active",
            IslandActivity.Charging => "Charging",
            _ => ClockText
        };

    public string CompactSecondaryText
    {
        get
        {
            var values = new List<string>();
            if (ShowQuoteInCompact)
            {
                if (QuoteAuthor.Length > 0) values.Add(QuoteAuthor);
                else if (Settings.ShowClock) values.Add(ClockText);
            }
            else
            {
                if (PrimaryActivity != IslandActivity.None && Settings.ShowClock) values.Add(ClockText);
            }
            return string.Join("  |  ", values);
        }
    }

    public IslandActivity PrimaryActivity => ActivityPolicy.Select(
        _timerAlarmService.State.Alarms.Any(a => a.Phase == AlarmPhase.Ringing),
        _timerAlarmService.State.Timers.Any(t => t.Phase == TimerPhase.Completed && !t.CompletionAcknowledged),
        IsQActive, Settings.PinnedActivity, Settings.PinnedActivity == IslandActivity.Media ? Settings.ShowMedia && Media.HasSession : DisplayTimer?.Id == Settings.PinnedTimerId,
        Audio.SystemMuted, Settings.ShowMedia && Media.HasSession, Audio.ActiveAudioOutput, Battery.IsCharging);
    public ICommand PreviousCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand SeekBackCommand { get; }
    public ICommand SeekForwardCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand AdjustVolumeCommand { get; }
    public ICommand ToggleExpandedCommand { get; }
    public ICommand RefreshCodexAccountCommand { get; }
    public ICommand CodexPrimaryActionCommand { get; }
    public ICommand SignOutCodexCommand { get; }
    public ICommand SeekCommand { get; private set; } = null!;
    public ICommand OpenMediaAppCommand { get; private set; } = null!;
    public ICommand LaunchCommand { get; private set; } = null!;
    public ICommand OpenMeetingCommand { get; private set; } = null!;
    public ICommand ToggleFavoriteCommand { get; private set; } = null!;
    public ICommand ToggleFocusCommand { get; private set; } = null!;
    public ICommand OpenNotificationHistoryCommand { get; private set; } = null!;
    public ICommand ClearNotificationHistoryCommand { get; private set; } = null!;
    public ICommand DismissCurrentNotificationCommand { get; private set; } = null!;
    public ICommand OpenNotificationCommand { get; private set; } = null!;
    public ICommand DismissHistoryItemCommand { get; private set; } = null!;
    public ICommand OpenWifiSettingsCommand { get; private set; } = null!;
    public ICommand OpenBluetoothSettingsCommand { get; private set; } = null!;

    public void ApplySettings()
    {
        _mediaService.SetPreferredApp(Settings.SelectedMediaApp);
        IsDarkTheme = _themeService.IsDark(Settings.Theme);
        if (Settings.AlwaysExpanded && !IsHolePunchMode) IsExpanded = true;
        else if (IsHolePunchMode && !IsQActive && !KeepExpanded) IsExpanded = false;
        UpdateCachedSettings();
        RaiseSettingsChanged();
        RaisePropertyChanged(nameof(PinExpanded));
        RaisePropertyChanged(nameof(UiFontFamily));
        RaiseQProperties();
    }

    public void RememberQTarget(nint targetWindow)
    {
        if (targetWindow != nint.Zero) _qTargetWindow = targetWindow;
    }

    public async Task StartQAsync(nint targetWindow = default, string? hotkeyShortcutName = null)
    {
        if (!Settings.QEnabled) return;
        if (targetWindow != nint.Zero) _qTargetWindow = targetWindow;
        _qAutoCloseTimer.Stop();
        _qSession.Cancel();
        if (Settings.QAutoExpandIsland) IsExpanded = true;
        OnUi(() =>
        {
            // Make Q the active presentation before capture/OCR starts. Capture can take a
            // noticeable amount of time or return no context on protected/minimized windows;
            // the user should still see a usable Q surface instead of an empty black shell.
            _qSnapshot = new QSessionSnapshot(
                QRunState.Capturing,
                QMode.Ask,
                string.Empty,
                string.Empty,
                "Reading active window…",
                null,
                null,
                Settings.QSelectedProvider,
                Settings.QSelectedModel);
            RaiseQProperties();
        });
        try
        {
            await RefreshCodexModelsAsync().ConfigureAwait(false);
            var context = await _qScreen.CaptureAsync(_qTargetWindow, Settings.QCaptureMode == Models.QCaptureMode.ActiveMonitor ? DynamicIsland.Q.Core.QCaptureMode.ActiveMonitor : DynamicIsland.Q.Core.QCaptureMode.ActiveWindow, CancellationToken.None).ConfigureAwait(false);
            await _qSession.BeginAsync(DynamicIsland.Q.Core.QMode.Ask, Settings.QSelectedProvider, Settings.QSelectedModel, context).ConfigureAwait(false);

            var shortcutName = string.IsNullOrWhiteSpace(hotkeyShortcutName) ? null : hotkeyShortcutName.Trim();
            var shortcutPrompt = shortcutName is null
                ? null
                : Settings.QShortcuts?.FirstOrDefault(shortcut =>
                    string.Equals(shortcut.Name, shortcutName, StringComparison.OrdinalIgnoreCase))?.Prompt;
            if (!string.IsNullOrWhiteSpace(shortcutPrompt))
                await SubmitQAsync(shortcutPrompt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OnUi(() => { _qSnapshot = _qSnapshot with { State = QRunState.Error, Status = "Capture failed", Error = ex.Message }; RaiseQProperties(); });
        }
    }

    public async Task SubmitQAsync(string prompt)
    {
        if (!Settings.QDisclosureAccepted)
        {
            OnUi(() => RaiseQProperties());
            return;
        }
        await RefreshCodexModelsAsync().ConfigureAwait(false);
        var baseUrl = string.Equals(Settings.QSelectedProvider, "ollama", StringComparison.OrdinalIgnoreCase) ? Settings.QOllamaBaseUrl : null;
        var credential = _qSecrets.Get(Settings.QSelectedProvider);
        var customSystemPrompt = _qSnapshot.Mode == QMode.Say ? Settings.QSaySystemPrompt : Settings.QAskSystemPrompt;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Settings.QTimeoutSeconds));
        await _qSession.SubmitAsync(prompt, _qSnapshot.Mode, Settings.QSelectedProvider, Settings.QSelectedModel, credential, baseUrl,
            Settings.QIncludeScreenImage,
            token => _qScreen.CaptureAsync(_qTargetWindow, Settings.QCaptureMode == Models.QCaptureMode.ActiveMonitor ? DynamicIsland.Q.Core.QCaptureMode.ActiveMonitor : DynamicIsland.Q.Core.QCaptureMode.ActiveWindow, token),
            cancellationToken: timeout.Token,
            maxResponseTokens: Settings.QMaxResponseTokens,
            customSystemPrompt: customSystemPrompt,
            reasoningEffort: Settings.QReasoningEffort).ConfigureAwait(false);
    }

    public async Task<string?> DictateQAsync()
    {
        if (!_qSpeech.IsAvailable) return null;
        OnUi(() => _qSnapshot = _qSnapshot with { State = QRunState.Listening, Status = "Listening…", Error = null });
        var text = await _qSpeech.DictateAsync(CancellationToken.None).ConfigureAwait(false);
        OnUi(() => _qSnapshot = _qSnapshot with { State = QRunState.Ready, Status = string.IsNullOrWhiteSpace(text) ? "Ready for a question" : "Dictation ready", Prompt = text ?? string.Empty });
        RaiseQProperties();
        return text;
    }

    public void SetQMode(QMode mode)
    {
        OnUi(() =>
        {
            _qSnapshot = _qSnapshot with { Mode = mode };
            RaiseQProperties();
        });
    }

    public void AcceptQDisclosure()
    {
        Settings.QDisclosureAccepted = true;
        _ = PersistSettingsAsync();
        RaiseQProperties();
    }

    public void CancelQ() { _qAutoCloseTimer.Stop(); _qSession.Cancel(); }
    public void ClearQ() { _qAutoCloseTimer.Stop(); _qSession.Clear(); IsExpanded = false; }
    public void CopyQResponse() { if (!string.IsNullOrWhiteSpace(QResponse)) System.Windows.Clipboard.SetText(QResponse); }

    private async Task RefreshCodexModelsAsync()
    {
        if (!IsCodexSelected || _codexAccount is null || _codexModels.Count > 0) return;
        try
        {
            await _codexAccount.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            var models = _codexAccount.Snapshot.Models ?? [];
            if (models.Count == 0) return;
            await OnUiAsync(() =>
            {
                _codexModels = models;
                var changed = false;
                if (SelectedCodexModel is null)
                {
                    Settings.QSelectedModel = models.FirstOrDefault(model => model.IsDefault)?.Id ?? models[0].Id;
                    changed = true;
                }
                changed |= NormalizeProviderReasoningEffort();
                RaiseMany(nameof(QSelectedModel), nameof(QModelOptions), nameof(QReasoningEffort), nameof(QReasoningEffortOptions));
                if (changed) _ = PersistSettingsAsync();
            }).ConfigureAwait(false);
        }
        catch
        {
            // Submission surfaces the concrete app-server/account error. Keep fallback
            // selectors available if discovery is temporarily unavailable.
        }
    }

    private async Task CodexPrimaryActionAsync()
    {
        if (_codexAccount is null) return;
        if (_codexAccount.Snapshot.IsConnected) { await _codexAccount.RefreshAsync().ConfigureAwait(false); return; }
        try
        {
            var login = await _codexAccount.StartLoginAsync().ConfigureAwait(false);
            Process.Start(new ProcessStartInfo { FileName = login.VerificationUrl, UseShellExecute = true });
            await _codexAccount.CompleteLoginAsync(login).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { await _codexAccount.RefreshAsync().ConfigureAwait(false); }
    }

    private async Task SignOutCodexAsync()
    {
        if (_codexAccount?.Snapshot.IsConnected != true) return;
        var choice = System.Windows.MessageBox.Show(
            "Signing out here also signs out official Codex apps using this Windows profile. Continue?",
            "Sign out of ChatGPT / Codex", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (choice == System.Windows.MessageBoxResult.Yes) await _codexAccount.LogoutAsync().ConfigureAwait(false);
    }

    private void OnCodexAccountChanged(CodexAccountSnapshot snapshot) => OnUi(() =>
    {
        _codexModels = snapshot.Models ?? _codexModels;
        NormalizeProviderReasoningEffort();
        RaiseMany(nameof(QCodexIsConnected), nameof(QShowCodexSignOut), nameof(QCodexChipText), nameof(QCodexAccountDetails),
            nameof(QModelOptions), nameof(QSelectedModel), nameof(QReasoningEffortOptions), nameof(QReasoningEffort));
    });

    private bool NormalizeProviderReasoningEffort()
    {
        var normalized = IsCodexSelected
            ? CodexModelSelectionPolicy.NormalizeEffort(SelectedCodexModel, Settings.QReasoningEffort)
            : QProviderPolicy.NormalizeEffort(Settings.QSelectedProvider, Settings.QSelectedModel, Settings.QReasoningEffort);
        if (string.Equals(normalized, Settings.QReasoningEffort, StringComparison.OrdinalIgnoreCase)) return false;
        Settings.QReasoningEffort = normalized;
        return true;
    }

    private void OnQChanged(QSessionSnapshot snapshot) => OnUi(() =>
    {
        _qSnapshot = snapshot;
        RaiseQProperties();
    });

    private void RaiseQProperties()
    {
        UpdateQAutoCloseTimer();
        RaiseMany(nameof(QState), nameof(QCurrentMode), nameof(QStatusText), nameof(QHeaderStatusText), nameof(QResponse), nameof(QResponseDisplay), nameof(QPromptText), nameof(QPromptDisplay), nameof(QHasPrompt), nameof(QInlineStatusText), nameof(QShowInlineThinking), nameof(QCanStop), nameof(QCanCopyResponse), nameof(QCanRetry), nameof(QShowResponseActions), nameof(QError), nameof(QShortcuts), nameof(QHasShortcuts), nameof(QSelectedModel), nameof(QModelOptions), nameof(QReasoningEffort), nameof(QReasoningEffortOptions), nameof(QIsCodexSelected), nameof(QCodexIsConnected), nameof(QShowCodexSignOut), nameof(QCodexChipText), nameof(QCodexAccountDetails),
        nameof(QSourceText), nameof(QCompactText), nameof(IsQActive), nameof(ShowQSurface), nameof(QIsAsk), nameof(QIsSay), nameof(QIsListening),
        nameof(QNeedsConsent), nameof(QSpeechAvailable), nameof(QSelectedProvider), nameof(ShowCompactMediaContent), nameof(ShowCompactQContent), nameof(ShowCompactHolePunchQOutput), nameof(ShowCompactArtSurface), nameof(ShowCompactStatusContent),
        nameof(PrimaryActivity), nameof(CompactGlyph), nameof(CompactPrimaryText), nameof(CompactSecondaryText), nameof(ShowCompactArt),
        nameof(ShowCompactMediaRing), nameof(ShowCompactTimerRing), nameof(ShowCompactRingTrack));
    }

    private void UpdateQAutoCloseTimer()
    {
        _qAutoCloseTimer.Stop();
        if (!Settings.QAutoCloseAfterResponse || QState != QRunState.Complete) return;
        _qAutoCloseTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(Settings.QAutoCloseDelaySeconds, 1, 300));
        _qAutoCloseTimer.Start();
    }

    private void OnMediaChanged(object? sender, MediaInfo value) => OnUi(() =>
    {
        var previous = Media;
        Media = value;
        if (Infrastructure.MediaPresentation.HasPresentationChanged(previous, value))
        {
            RaiseMediaPresentationChanged();
            return;
        }

        // The normal playing update changes only position and UpdatedAt. Refresh just the
        // progress/time bindings instead of re-evaluating the entire island twice per second.
        RaiseMany(nameof(MediaProgress), nameof(MediaElapsedText), nameof(MediaTimeRemaining),
            nameof(MediaTrailingTimeText));
    });
    private void OnAudioChanged(object? sender, AudioState value) => OnUi(() =>
    {
        var isAbove = Settings.VolumeWarningEnabled
            && value.Availability == AudioAvailability.Available
            && !value.SystemMuted
            && value.MasterVolumePercent > Settings.VolumeWarningThreshold;
        var prevAbove = _prevAboveVolumeThreshold;
        _prevAboveVolumeThreshold = isAbove;

        Audio = value;
        RaiseAudioProperties();

        if (isAbove && !prevAbove && DateTimeOffset.Now >= _volumeWarnCooldownUntil)
            TriggerVolumeWarning(value.MasterVolumePercent);
    });
    private void OnWeatherChanged(object? sender, WeatherInfo? value) => OnUi(() =>
    {
        _weather = value;
        RaiseMany(nameof(ShowWeather), nameof(WeatherGlyph), nameof(WeatherTempText), nameof(WeatherDescText),
            nameof(WeatherCityText), nameof(ShowWidgetsPanel), nameof(LiveWidgetRailWidth));
    });
    private void OnSysStatsChanged(object? sender, SystemStats value) => OnUi(() =>
    {
        _sysStats = value;
        _netHistory.Enqueue(value.NetBytesPerSec);
        while (_netHistory.Count > SparkHistoryLength) _netHistory.Dequeue();
        _netSparkline = Sparkline(_netHistory, 46, 16);
        RaiseMany(nameof(ShowSystemMonitor), nameof(ShowCompactRam), nameof(CpuText), nameof(RamText), nameof(NetText),
            nameof(RamPercentValue), nameof(NetSparkline), nameof(WifiConnected), nameof(WifiStatusText), nameof(WifiStatusBrush));
    });
    private void OnStocksChanged(object? sender, IReadOnlyList<StockQuote> value) => OnUi(() =>
    {
        _stocks = value;
        _stockTiles = value
            .Select(q => new StockTile(q.Symbol, q.PriceText, q.ChangeText, q.Up, Sparkline(q.History, 34, 12)))
            .ToArray();
        RaiseMany(nameof(ShowStocks), nameof(Stocks), nameof(ShowWidgetsPanel), nameof(LiveWidgetRailWidth));
    });
    private void OnMeetingChanged(object? sender, MeetingInfo? value) => OnUi(() =>
    {
        _meeting = value;
        RaiseMany(nameof(ShowNextMeeting), nameof(MeetingTitle), nameof(MeetingWhen), nameof(HasMeetingJoin),
            nameof(ShowWidgetsPanel), nameof(LiveWidgetRailWidth));
    });
    private void RefreshNotificationHistory()
    {
        NotificationHistory.Clear();
        foreach (var item in _notificationHistoryService.Items)
            NotificationHistory.Add(item);
        RaisePropertyChanged(nameof(HasNotificationHistory));
        RaisePropertyChanged(nameof(ShowEmptyNotificationHistory));
    }

    private void DismissCurrentNotification()
    {
        if (_currentNotificationHistoryItem is not null)
            _notificationHistoryService.Dismiss(_currentNotificationHistoryItem.Id);
        if (_currentGroup is { } group) foreach (var item in group.Items) _notificationHistoryService.Dismiss(item.Id);
        _currentGroup = null;
        _currentNotificationHistoryItem = null;
        _notification = null;
        RefreshNotificationHistory();
        RaiseMany(nameof(ShowNotification), nameof(ShowBanner), nameof(IsAirPodsBannerActive), nameof(BannerApp), nameof(BannerTitle), nameof(BannerBody));
    }

    private static void OpenNotification(NotificationHistoryItem? item)
    {
        if (item is null) return;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(string.IsNullOrWhiteSpace(item.AppId) ? "shell:AppsFolder" : "shell:AppsFolder\\" + item.AppId);
            System.Diagnostics.Process.Start(start);
        }
        catch { }
    }
    private Task PersistSettingsAsync() => _settingsPersistence.SaveAsync(Settings);

    // Allowlist shows only matching apps; blocklist hides them. Matching is case-insensitive substring.
    private bool PassesNotificationFilter(string app)
    {
        if (Settings.NotificationFilterMode == NotificationFilter.All) return true;
        var terms = (Settings.NotificationAppFilter ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return Settings.NotificationFilterMode == NotificationFilter.Blocklist;
        var matches = terms.Any(t => app.Contains(t, StringComparison.OrdinalIgnoreCase));
        return Settings.NotificationFilterMode == NotificationFilter.Allowlist ? matches : !matches;
    }

    private void OnPrivacyChanged(object? sender, PrivacySensorState value) => OnUi(() =>
    {
        var changed = value != _privacy;
        _privacy = value;
        if (changed && ShowPrivacyInUse) _privacySeq++;
        RaiseMany(nameof(ShowCameraInUse), nameof(ShowMicInUse), nameof(ShowPrivacyInUse),
            nameof(PrivacyActivityText), nameof(PrivacyActivityGlyph), nameof(PrivacyIndicatorBrush), nameof(PrivacySeq),
            nameof(PrivacyCameraApps), nameof(PrivacyMicApps), nameof(PrivacySourceNames),
            nameof(PrivacySourceText), nameof(PrivacyDetailText), nameof(HasPrivacySource),
            nameof(ShowStatusExtras), nameof(ShowWidgetsPanel));
    });

    private void TriggerVolumeWarning(int volumePercent)
    {
        _lastWarnedVolumePercent = volumePercent;
        _volumeWarningActive = true;
        _volumeWarnCooldownUntil = DateTimeOffset.Now.AddSeconds(60);
        _bannerSeq++;
        RaiseMany(nameof(ShowBanner), nameof(IsAirPodsBannerActive), nameof(BannerApp), nameof(BannerTitle), nameof(BannerBody), nameof(BannerSeq));
        _volumeWarningTimer.Stop();
        _volumeWarningTimer.Start();
    }

    private static void LaunchApp(string? pathOrUri)
    {
        if (string.IsNullOrWhiteSpace(pathOrUri)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = pathOrUri, UseShellExecute = true }); }
        catch { }
    }
    private static void OpenUrl(string url) => LaunchApp(url);

    private void OnSpectrumChanged(object? sender, double[] bands) => OnUi(() =>
    {
        _spectrum = bands;
        foreach (var name in new[]
        {
            nameof(SpectrumBand0), nameof(SpectrumBand1), nameof(SpectrumBand2), nameof(SpectrumBand3),
            nameof(SpectrumBand4), nameof(SpectrumBand5), nameof(SpectrumBand6),
            nameof(UseRealSpectrum), nameof(ShowAnimatedWave), nameof(ShowMusicVisualizer),
            nameof(VisualizerBar0Height), nameof(VisualizerBar1Height), nameof(VisualizerBar2Height),
            nameof(VisualizerBar3Height), nameof(VisualizerBar4Height), nameof(VisualizerBar5Height), nameof(VisualizerBar6Height)
        }) RaisePropertyChanged(name);
    });

    private static string WithAlpha(string hex, byte alpha)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 8) h = h[2..];
        return h.Length == 6 ? $"#{alpha:X2}{h}" : hex;
    }

    private void OnBatteryChanged(object? sender, BatteryState value) => OnUi(() => { Battery = value; RaiseBatteryProperties(); });
    // Only the time strings change each second — re-raising everything (brushes/geometries) every tick
    // was needless CPU churn (which the system monitor then read back as inflated usage).
    private void OnClockTick(object? sender, DateTimeOffset value) => OnUi(() =>
    {
        _now = value;
        UpdateWorldClockValues();
        RaiseMany(nameof(ClockText), nameof(ClockTimeText), nameof(ClockAmPm), nameof(DateText), nameof(DateLongText),
            nameof(CompactPrimaryText), nameof(CompactSecondaryText),
            nameof(CountdownText), nameof(WorldClocks), nameof(MeetingWhen));
    });
    private void RaiseMany(params string[] names) { foreach (var n in names) RaisePropertyChanged(n); }
    private void OnTimerAlarmChanged(object? sender, EventArgs e) => OnUi(() => { RefreshActivities(); RaiseMany(
        nameof(TimerText), nameof(TimerProgress), nameof(TimerRemainingProgress), nameof(ShowTimerOrb), nameof(AlarmText), nameof(PrimaryActivity),
        nameof(CompactGlyph), nameof(CompactPrimaryText), nameof(CompactSecondaryText),
        nameof(ShowCompactArt), nameof(ShowCompactMediaRing), nameof(ShowCompactTimerRing),
        nameof(ShowCompactRingTrack)); });
    private void OnSystemThemeChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        IsDarkTheme = _themeService.IsDark(Settings.Theme);
        RaiseThemeProperties();
    });

    // Targeted media presentation update — only properties derived from media state.
    // Structural/layout properties (IsStatsStyle, IsAppleStyle, IsCompact, PinExpanded, etc.)
    // are intentionally excluded so a track change never triggers ApplyLayout/AnimatePill.
    private void RaiseMediaPresentationChanged()
    {
        RaiseMany(nameof(MediaTitle), nameof(MediaArtist), nameof(IsPlaying), nameof(PlayPauseGlyph),
            nameof(MediaProgress), nameof(ShowMediaTimes), nameof(MediaElapsedText), nameof(MediaTotalText),
            nameof(MediaTimeRemaining), nameof(MediaTrailingTimeText), nameof(ShowNowPlaying), nameof(ShowExplicitBadge),
            nameof(CanSeek), nameof(SeekBackTooltip), nameof(SeekForwardTooltip),
            nameof(PrimaryActivity), nameof(CompactGlyph), nameof(CompactPrimaryText), nameof(CompactSecondaryText),
            nameof(ShowCompactArt), nameof(ShowCompactMediaRing), nameof(ShowExpandedMediaRing), nameof(ShowCompactRingTrack),
            nameof(Artwork), nameof(HasArtwork), nameof(ShowMedia), nameof(ScrollTitles));
        // Adaptive accent may have changed via UpdateArtwork; ensure brushes update in place.
        if (Settings.AdaptiveAccent)
            RaiseMany(nameof(AccentBrush), nameof(AccentSoftBrush), nameof(AccentTextBrush));
        RaiseMediaCommandsCanExecute();
    }

    private void RaiseMediaCommandsCanExecute()
    {
        (PreviousCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PlayPauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NextCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SeekBackCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SeekForwardCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RaiseAudioProperties()
    {
        RaiseMany(nameof(VolumeText), nameof(VolumeGlyph), nameof(OutputDeviceText), nameof(AudioStatusText),
            nameof(IsMuted), nameof(IsAudioActive), nameof(ShowAudioStatusText), nameof(ShowVolume),
            nameof(ShowMedia), nameof(PrimaryActivity), nameof(CompactGlyph), nameof(CompactPrimaryText), nameof(CompactSecondaryText),
            nameof(ShowCompactArt), nameof(ShowCompactMediaRing), nameof(ShowCompactRingTrack),
            nameof(UseRealSpectrum), nameof(ShowAnimatedWave), nameof(ShowMusicVisualizer),
            nameof(VisualizerBar0Height), nameof(VisualizerBar1Height), nameof(VisualizerBar2Height),
            nameof(VisualizerBar3Height), nameof(VisualizerBar4Height), nameof(VisualizerBar5Height), nameof(VisualizerBar6Height));
        (ToggleMuteCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RaiseBatteryProperties()
    {
        RaiseMany(nameof(BatteryText), nameof(ChargingText), nameof(BatteryGlyph),
            nameof(ShowBattery), nameof(IsCharging), nameof(IsPowerConnected), nameof(ShowBatteryLevel),
            nameof(ShowCompactChargingIndicator), nameof(ShowCompactSecondary), nameof(ShowBatteryTime), nameof(BatteryTimeText),
            nameof(ShowStatusExtras), nameof(ShowWidgetsPanel), nameof(LiveWidgetRailWidth), nameof(PrimaryActivity),
            nameof(CompactGlyph), nameof(CompactPrimaryText), nameof(CompactSecondaryText),
            nameof(ShowCompactArt), nameof(ShowCompactMediaRing), nameof(ShowCompactRingTrack));
    }

    private void RaiseThemeProperties()
    {
        RaiseMany(nameof(PrimaryTextBrush), nameof(SecondaryTextBrush), nameof(AccentTextBrush), nameof(PanelBrush),
            nameof(PanelBorderBrush), nameof(AccentBrush), nameof(AccentSoftBrush), nameof(IslandSurfaceBrush), nameof(IslandCardBrush), nameof(IslandDividerBrush),
            nameof(ShellControlBrush), nameof(ShellControlHoverBrush), nameof(ProgressFillBrush), nameof(ProgressTrackBrush), nameof(UiFontFamily));
    }

    private void RaiseExpansionProperties()
    {
        RaiseMany(nameof(IsCompact), nameof(ShowTimerOrb), nameof(ShowQuoteInCompact), nameof(ShowQuoteInExpanded));
    }

    private void RaiseFocusModeProperties()
    {
        RaiseMany(nameof(FocusModeEnabled), nameof(FocusModeText),
            nameof(ShowWeather), nameof(ShowSystemMonitor), nameof(ShowCountdown), nameof(ShowStocks), nameof(ShowWorldClocks),
            nameof(ShowNextMeeting), nameof(ShowQuickLaunch), nameof(ShowBatteryTime), nameof(ShowWidgetsPanel),
            nameof(LiveWidgetRailWidth), nameof(ShowStatusExtras),
            nameof(ShowNotification), nameof(ShowBanner), nameof(IsAirPodsBannerActive),
            nameof(BannerApp), nameof(BannerTitle), nameof(BannerBody), nameof(ShowAirPods), nameof(ShowAirPodsCard));
    }

    private void RaiseAirPodsProperties()
    {
        RaiseMany(nameof(AirPods), nameof(ShowAirPods), nameof(ShowAirPodsCard), nameof(AirPodsName), nameof(AirPodsModelName),
            nameof(AirPodsLeftBatteryText), nameof(AirPodsRightBatteryText), nameof(AirPodsCaseBatteryText), nameof(AirPodsBatterySummary),
            nameof(AirPodsCompactBatteryText),
            nameof(ShowAirPodsLeft), nameof(ShowAirPodsRight), nameof(ShowAirPodsCase),
            nameof(AirPodsLeftCharging), nameof(AirPodsRightCharging), nameof(AirPodsCaseCharging),
            nameof(AirPodsStatusText), nameof(BannerApp), nameof(BannerTitle), nameof(BannerBody),
            nameof(BluetoothAvailable), nameof(BluetoothStatusText), nameof(BluetoothStatusBrush));
        RaiseLiveWidgetLayoutProperties();
    }

    private void OnAirPodsChanged(object? sender, AirPodsState value) => OnUi(() =>
    {
        var previous = _airPods;
        if (previous.Equals(value)) return;
        _airPods = value;
        RaiseAirPodsProperties();

        if (ShouldTriggerAirPodsBanner(previous, value))
            TriggerAirPodsBanner();
        else if (!value.IsConnected)
        {
            // Clear transient banner if AirPods disconnected
            if (_airPodsBannerActive)
            {
                _airPodsBannerActive = false;
                _airPodsBannerTimer.Stop();
                RaiseMany(nameof(ShowBanner), nameof(IsAirPodsBannerActive), nameof(BannerApp), nameof(BannerTitle), nameof(BannerBody));
            }
        }
    });

    private bool ShouldTriggerAirPodsBanner(AirPodsState previous, AirPodsState next)
    {
        if (!AirPodsConnectionPolicy.IsNewConnection(previous, next)) return false;
        if (FocusModeEnabled) return false;
        // Lower priority than urgent activities
        if (PrimaryActivity is IslandActivity.Alarm or IslandActivity.Timer or IslandActivity.Q) return false;
        // Battery, charging and case metadata can fluctuate between continuity packets.
        // They update the visible card but never replay the connection banner.
        return true;
    }

    private void TriggerAirPodsBanner()
    {
        var wasActive = _airPodsBannerActive;
        _airPodsBannerActive = true;
        _airPodsBannerTimer.Stop();
        _airPodsBannerTimer.Start();
        if (wasActive)
        {
            RaiseMany(nameof(BannerApp), nameof(BannerTitle), nameof(BannerBody));
            return;
        }
        _bannerSeq++;
        RaiseMany(nameof(ShowBanner), nameof(BannerApp), nameof(BannerTitle), nameof(BannerBody), nameof(BannerSeq), nameof(IsAirPodsBannerActive));
    }

    private void RaiseLayoutProperties()
    {
        RaiseMany(nameof(IsCompact), nameof(IsAppleStyle), nameof(IsStatsStyle), nameof(PinExpanded), nameof(IsEdgePill), nameof(IsHolePunchMode),
            nameof(IslandCornerRadius), nameof(IslandInnerCornerRadius),
            nameof(CompactAlbumSize), nameof(ExpandedAlbumSize), nameof(PreviewCompactAlbumSize), nameof(PreviewExpandedAlbumSize),
            nameof(CompactAlbumRadius), nameof(ExpandedAlbumRadius), nameof(PreviewCompactAlbumRadius), nameof(PreviewExpandedAlbumRadius),
            nameof(CompactIconCorner), nameof(ExpandedIconCorner), nameof(PreviewCompactIconCorner), nameof(PreviewExpandedIconCorner),
            nameof(CompactRingGeometry), nameof(ExpandedRingGeometry), nameof(CompactRingPerimeterUnits), nameof(ExpandedRingPerimeterUnits),
            nameof(AlbumScale), nameof(ExpandedAlbumScale), nameof(PreviewIslandWidth), nameof(PreviewIslandHeight),
            nameof(MediaColumn), nameof(VolumeColumn), nameof(StatusColumn), nameof(InterfaceScaleFactor),
            nameof(ClockFontSize), nameof(DateFontSize), nameof(BatteryGlyphFontSize), nameof(BatteryTextFontSize),
            nameof(ChargingGlyphFontSize), nameof(ChargingTextFontSize), nameof(CompactChargingTextFontSize),
            nameof(MediaTitleFontSize), nameof(MediaArtistFontSize), nameof(VolumeFontSize),
            nameof(CompactGlyphFontSize), nameof(CompactPrimaryFontSize), nameof(CompactSecondaryFontSize), nameof(CompactClockFontSize),
            nameof(ExpandedMediaTitleFontSize), nameof(ExpandedMediaArtistFontSize));
    }

    private void RaiseSettingsChanged()
    {
        // Settings affect many subsystems — raise grouped notifications but keep structural
        // visual-mode notifications precise (handled via layout/theme groups).
        RaiseLayoutProperties();
        RaiseThemeProperties();
        RaiseMediaPresentationChanged();
        RaiseAudioProperties();
        RaiseBatteryProperties();
        RaiseFocusModeProperties();
        RaiseMany(nameof(ShowClock), nameof(ShowDate), nameof(ShowTimerAlarm), nameof(DebugOverlay), nameof(IsReducedMotion),
            nameof(ShowCameraInUse), nameof(ShowMicInUse), nameof(ShowPrivacyInUse), nameof(PrivacyActivityText), nameof(PrivacyActivityGlyph),
            nameof(PrivacyIndicatorBrush), nameof(PrivacySourceText), nameof(PrivacyDetailText), nameof(HasPrivacySource),
            nameof(PrivacyCameraApps), nameof(PrivacyMicApps), nameof(PrivacySourceNames),
            nameof(ShowWeather), nameof(WeatherGlyph), nameof(WeatherTempText), nameof(WeatherDescText), nameof(WeatherCityText),
            nameof(ShowSystemMonitor), nameof(ShowCompactRam), nameof(CpuText), nameof(RamText), nameof(NetText),
            nameof(RamPercentValue), nameof(NetSparkline), nameof(ShowCountdown), nameof(CountdownText),
            nameof(ShowWorldClocks), nameof(WorldClocks), nameof(ShowQuotes), nameof(ShowQuoteInCompact), nameof(ShowQuoteInExpanded),
            nameof(QuoteText), nameof(QuoteAuthor), nameof(QuoteAuthorDisplay), nameof(ShowQuoteAuthor),
            nameof(QuoteTextFontSize), nameof(QuoteAuthorFontSize), nameof(ShowStocks), nameof(Stocks),
            nameof(ShowNextMeeting), nameof(MeetingTitle), nameof(MeetingWhen), nameof(HasMeetingJoin),
            nameof(ShowBatteryTime), nameof(BatteryTimeText), nameof(ShowQuickLaunch), nameof(LaunchItems),
            nameof(ShowNotification), nameof(ShowBanner), nameof(IsAirPodsBannerActive),
            nameof(BannerApp), nameof(BannerTitle), nameof(BannerBody),
            nameof(ShowClipboard), nameof(ShowWidgetsPanel), nameof(LiveWidgetRailWidth), nameof(ShowStatusExtras),
            nameof(UseRealSpectrum), nameof(ShowAnimatedWave), nameof(ShowMusicVisualizer), nameof(ShowConnectivity), nameof(PinExpanded), nameof(ScrollTitles));
        RaiseLiveWidgetLayoutProperties();
        RaiseMediaCommandsCanExecute();
        (ToggleMuteCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RaiseComputed()
    {
        // Legacy broad refresh — preserved for compatibility but delegates to grouped helpers
        // to keep notification semantics consistent. Prefer targeted helpers for runtime events.
        RaiseSettingsChanged();
    }

    private static MediaBrush FrozenBrush(string value) => BrushCache.GetOrAdd(value, static color =>
    {
        var brush = (MediaBrush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    });

    public void RefreshLaunchAndZones()
    {
        UpdateCachedSettings();
        RaiseMany(nameof(ShowQuickLaunch), nameof(LaunchItems), nameof(ShowWorldClocks), nameof(WorldClocks),
            nameof(ShowWidgetsPanel), nameof(LiveWidgetRailWidth));
        RaiseLiveWidgetLayoutProperties();

    }

    private void UpdateCachedSettings()
    {
        _expandedOrder = (Settings.ExpandedOrder ?? "media,volume,status")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (_expandedOrder.Length == 0) _expandedOrder = ["media", "volume", "status"];

        var quotes = new List<QuoteItem>();
        foreach (var line in (Settings.QuotesText ?? "")
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', 2);
            var text = parts[0].Trim();
            if (text.Length == 0) continue;
            quotes.Add(new QuoteItem(text, parts.Length > 1 ? parts[1].Trim() : ""));
        }
        _quotes = quotes.ToArray();
        _quoteIndex = _quoteIndex % Math.Max(1, _quotes.Length);
        ConfigureQuoteRotation();
        RaiseMany(nameof(ShowQuotes), nameof(ShowQuoteInCompact), nameof(ShowQuoteInExpanded),
            nameof(QuoteText), nameof(QuoteAuthor), nameof(QuoteAuthorDisplay), nameof(ShowQuoteAuthor),
            nameof(CompactGlyph), nameof(CompactPrimaryText), nameof(CompactSecondaryText));

        var launchItems = new List<LaunchEntry>();
        foreach (var line in (Settings.QuickLaunchItems ?? "")
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', 2);
            var name = parts[0].Trim();
            var path = parts.Length > 1 ? parts[1].Trim() : parts[0].Trim();
            if (path.Length > 0)
                launchItems.Add(new LaunchEntry(name, path,
                    string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant()));
        }
        _launchItems = launchItems;

        var config = Settings.WorldClockZones ?? string.Empty;
        if (!string.Equals(config, _worldClockConfig, StringComparison.Ordinal))
        {
            _worldClockConfig = config;
            var zones = new List<(string Label, TimeZoneInfo Zone)>();
            foreach (var id in config.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try { zones.Add((ShortZone(id), TimeZoneInfo.FindSystemTimeZoneById(id))); }
                catch { }
            }
            _worldClockZones = zones.ToArray();
        }
        UpdateWorldClockValues();
    }

    private void UpdateWorldClockValues()
    {
        if (_worldClockZones.Length == 0)
        {
            _worldClocks = [];
            return;
        }

        var format = Settings.Use24HourClock ? "HH:mm" : "h:mm tt";
        _worldClocks = _worldClockZones
            .Select(zone => new WorldClock(zone.Label, TimeZoneInfo.ConvertTime(_now, zone.Zone).ToString(format)))
            .ToArray();
    }

    private static double RoundedSquarePerimeter(double size, double radius)
    {
        radius = Math.Clamp(radius, 0, size / 2);
        return 4 * (size - 2 * radius) + 2 * Math.PI * radius;
    }

    // Builds a rounded-square outline starting at top-centre (so the progress dash begins at the top).
    private static Geometry RoundedSquare(double s, double r)
    {
        r = Math.Clamp(r, 0.0001, s / 2);
        return RingGeometryCache.GetOrAdd((s, r), static key => BuildRoundedSquare(key.Size, key.Radius));
    }

    private static Geometry BuildRoundedSquare(double s, double r)
    {
        var figure = new PathFigure { StartPoint = new System.Windows.Point(s / 2, 0), IsClosed = true };
        var size = new System.Windows.Size(r, r);
        void Line(double x, double y) => figure.Segments.Add(new LineSegment(new System.Windows.Point(x, y), true));
        void Arc(double x, double y) => figure.Segments.Add(
            new ArcSegment(new System.Windows.Point(x, y), size, 0, false, SweepDirection.Clockwise, true));
        Line(s - r, 0); Arc(s, r);
        Line(s, s - r); Arc(s - r, s);
        Line(r, s); Arc(0, s - r);
        Line(0, r); Arc(r, 0);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private void UpdateArtwork(byte[]? bytes)
    {
        if (ReferenceEquals(bytes, _artworkBytes)) return;
        var oldAdaptive = _adaptiveAccent;
        _artworkBytes = bytes;
        _adaptiveAccent = Settings.AdaptiveAccent ? Infrastructure.ImageColor.Dominant(bytes) : null;
        var accentChanged = !string.Equals(oldAdaptive, _adaptiveAccent, StringComparison.OrdinalIgnoreCase);
        _artwork = null;
        if (bytes is not null)
        {
            try
            {
                var image = new BitmapImage();
                using var stream = new MemoryStream(bytes);
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.DecodePixelWidth = 128;
                image.EndInit();
                image.Freeze();
                _artwork = image;
            }
            catch { }
        }
        RaisePropertyChanged(nameof(Artwork));
        RaisePropertyChanged(nameof(HasArtwork));
        // In-place artwork update must not trigger structural layout transitions.
        RaisePropertyChanged(nameof(ShowCompactArt));
        RaisePropertyChanged(nameof(ShowCompactMediaRing));
        RaisePropertyChanged(nameof(ShowExpandedMediaRing));
        RaisePropertyChanged(nameof(ShowCompactRingTrack));
        if (accentChanged && Settings.AdaptiveAccent)
            RaiseMany(nameof(AccentBrush), nameof(AccentSoftBrush), nameof(AccentTextBrush));
    }

    private static void OnUi(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app is null) { action(); return; }
        var dispatcher = app.Dispatcher;
        if (dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action);
    }

    private static Task OnUiAsync(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app is null || app.Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return app.Dispatcher.InvokeAsync(action).Task;
    }

    public void Dispose()
    {
        _mediaService.Changed -= OnMediaChanged;
        _audioService.Changed -= OnAudioChanged;
        _weatherService.Changed -= OnWeatherChanged;
        _systemMonitorService.Changed -= OnSysStatsChanged;
        _spectrumService.BandsChanged -= OnSpectrumChanged;
        _stocksService.Changed -= OnStocksChanged;
        _calendarService.Changed -= OnMeetingChanged;
        _notificationService.BatchReceived -= OnNotificationBatch;
        _calendarService.StatusChanged -= OnIntegrationStatusChanged;
        _notificationService.StatusChanged -= OnIntegrationStatusChanged;
        if (_clipboardIntegration is not null) _clipboardIntegration.StatusChanged -= OnIntegrationStatusChanged;
        _privacyService.Changed -= OnPrivacyChanged;
        _notificationTimer.Stop();
        _volumeWarningTimer.Stop();
        _airPodsBannerTimer.Stop();
        _quoteTimer.Stop();
        _visualizerTimer.Stop();
        _connectivityTimer.Stop();
        _qAutoCloseTimer.Stop();
        _batteryService.Changed -= OnBatteryChanged;
        _clockService.Tick -= OnClockTick;
        _timerAlarmService.Changed -= OnTimerAlarmChanged;
        _themeService.SystemThemeChanged -= OnSystemThemeChanged;
        _qSession.Changed -= OnQChanged;
        if (_codexAccount is not null) _codexAccount.Changed -= OnCodexAccountChanged;
        _qSession.Clear();
        if (_airPodsService != null) _airPodsService.Changed -= OnAirPodsChanged;
    }
}

public sealed record WorldClock(string Label, string Time);
public sealed record LaunchEntry(string Name, string Path, string Glyph);
public sealed record OutputDeviceItem(string Id, string Name, bool IsCurrent);
public sealed record StockTile(string Symbol, string PriceText, string ChangeText, bool Up, System.Windows.Media.PointCollection Spark);
public sealed record QuoteItem(string Text, string Author);
