using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using DynamicIsland.Windows.Infrastructure;
using DynamicIsland.Windows.Models;
using DynamicIsland.Windows.Services;
using DynamicIsland.Windows.Services.Q;
using DynamicIsland.Q.Core;

namespace DynamicIsland.Windows.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly StartupService _startupService;
    private readonly Action _apply;
    private readonly Action _recenter;
    private readonly Action _close;
    private readonly IQSecretStore _qSecrets;
    private readonly IQProviderRegistry _qProviders;
    private readonly CodexAccountCoordinator _codexAccount;
    private IReadOnlyList<CodexModel> _codexModels = [];
    private readonly Dictionary<string, IReadOnlyList<QModelInfo>> _providerModels = new(StringComparer.OrdinalIgnoreCase);
    private string _qConnectionStatus = "Not tested";
    public SettingsViewModel(AppSettings settings, SettingsService settingsService,
        StartupService startupService, Action apply, Action recenter, Action close,
        IQSecretStore qSecrets, IQProviderRegistry qProviders,
        CodexAccountCoordinator codexAccount)
    {
        _settings = settings;
        _settingsService = settingsService;
        _startupService = startupService;
        _apply = apply;
        _recenter = recenter;
        _close = close;
        _qSecrets = qSecrets;
        _qProviders = qProviders;
        _codexAccount = codexAccount;
        _codexModels = codexAccount.Snapshot.Models ?? [];
        _codexAccount.Changed += OnCodexAccountChanged;
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        RecenterCommand = new RelayCommand(() => { _recenter(); _ = SaveAsync(false); });
        CloseCommand = new RelayCommand(() => { _ = SaveAsync(); _close(); });
        ResetCommand = new RelayCommand(ResetToDefaults);
        ImportCommand = new RelayCommand(Import);
        ExportCommand = new RelayCommand(Export);
        SavePresetCommand = new RelayCommand(() => _ = SavePresetAsync());
        ApplyPresetCommand = new RelayCommand(() => _ = ApplyPresetAsync());
        DeletePresetCommand = new RelayCommand(DeletePreset);
        ApplyIslandPresetCommand = new RelayCommand<string>(ApplyIslandPreset);
        PickAccentCommand = new RelayCommand<string>(hex => { if (hex is not null) AccentColorHex = hex; });
        MoveModuleUpCommand = new RelayCommand<ModuleItem>(m => MoveModule(m, -1));
        MoveModuleDownCommand = new RelayCommand<ModuleItem>(m => MoveModule(m, +1));
        SelectSectionCommand = new RelayCommand<string>(k => { if (k is not null) SelectedSectionKey = k; });
        TestQCommand = new RelayCommand(() => _ = TestQAsync());
        SignInCodexCommand = new RelayCommand(() => _ = SignInCodexAsync());
        RefreshCodexCommand = new RelayCommand(() => _ = _codexAccount.RefreshAsync());
        CancelCodexLoginCommand = new RelayCommand(_codexAccount.CancelLogin);
        SignOutCodexCommand = new RelayCommand(() => _ = SignOutCodexAsync());
        ResetQPromptsCommand = new RelayCommand(ResetQPrompts);
        AddQShortcutCommand = new RelayCommand(AddQShortcut);
        RemoveQShortcutCommand = new RelayCommand<QShortcutItem>(RemoveQShortcut);
        MoveQShortcutUpCommand = new RelayCommand<QShortcutItem>(item => MoveQShortcut(item, -1));
        MoveQShortcutDownCommand = new RelayCommand<QShortcutItem>(item => MoveQShortcut(item, 1));
        AddQuickLaunchCommand = new RelayCommand(AddQuickLaunch);
        RemoveQuickLaunchCommand = new RelayCommand<LaunchListItem>(RemoveQuickLaunch);
        BrowseQuickLaunchCommand = new RelayCommand<LaunchListItem>(BrowseQuickLaunch);
        MoveQuickLaunchUpCommand = new RelayCommand<LaunchListItem>(i => MoveQuickLaunch(i, -1));
        MoveQuickLaunchDownCommand = new RelayCommand<LaunchListItem>(i => MoveQuickLaunch(i, +1));
        InitLists();
        RebuildQShortcuts();
    }

    public ObservableCollection<string> AvailableMediaApps { get; } = ["Automatic"];
    public Array ThemeOptions => Enum.GetValues<ThemeMode>();
    public Array SizeOptions => Enum.GetValues<IslandSize>();
    public Array VisualModeOptions => Enum.GetValues<IslandVisualMode>();
    public Array AnimationOptions => Enum.GetValues<AnimationIntensity>();
    public IReadOnlyList<PositionChoice> PositionOptions =>
    [
        new(PositionMode.TopLeft, "Top left"), new(PositionMode.TopCenter, "Top center"), new(PositionMode.TopRight, "Top right"),
        new(PositionMode.MiddleLeft, "Middle left"), new(PositionMode.Center, "Center"), new(PositionMode.MiddleRight, "Middle right"),
        new(PositionMode.BottomLeft, "Bottom left"), new(PositionMode.BottomCenter, "Bottom center"), new(PositionMode.BottomRight, "Bottom right"),
        new(PositionMode.Manual, "Custom (dragged)")
    ];

    public bool LaunchOnStartup { get => _settings.LaunchOnStartup; set => Set(v => _settings.LaunchOnStartup = v, value); }
    public bool AlwaysOnTop { get => _settings.AlwaysOnTop; set => Set(v => _settings.AlwaysOnTop = v, value); }
    public bool LockPosition { get => _settings.LockPosition; set => Set(v => _settings.LockPosition = v, value); }
    public bool ClickThroughWhenCompact { get => _settings.ClickThroughWhenCompact; set => Set(v => _settings.ClickThroughWhenCompact = v, value); }
    public bool ExpandOnHover { get => _settings.ExpandOnHover; set => Set(v => _settings.ExpandOnHover = v, value); }
    public bool ShowMedia { get => _settings.ShowMedia; set => Set(v => _settings.ShowMedia = v, value); }
    public bool ShowAlbumArtInCompact { get => _settings.ShowAlbumArtInCompact; set => Set(v => _settings.ShowAlbumArtInCompact = v, value); }
    public int AlbumArtSize
    {
        get => _settings.AlbumArtScale;
        set { _settings.AlbumArtScale = Math.Clamp(value, 70, 130); RaisePropertyChanged(); _apply(); }
    }
    public int ExpandedAlbumArtSize
    {
        get => _settings.ExpandedAlbumArtSize;
        set { _settings.ExpandedAlbumArtSize = Math.Clamp(value, 40, 160); RaisePropertyChanged(); _apply(); }
    }
    public int AlbumCornerRadius
    {
        get => _settings.AlbumCornerRadius;
        set { _settings.AlbumCornerRadius = Math.Clamp(value, 0, 30); RaisePropertyChanged(); _apply(); }
    }
    public bool ShowMediaProgressRing { get => _settings.ShowMediaProgressRing; set { Set(v => _settings.ShowMediaProgressRing = v, value); _apply(); } }
    public bool ShowSongTimeRemaining { get => _settings.ShowSongTimeRemaining; set { Set(v => _settings.ShowSongTimeRemaining = v, value); _apply(); } }
    public bool ShowTimerRing { get => _settings.ShowTimerRing; set { Set(v => _settings.ShowTimerRing = v, value); _apply(); } }
    public bool ShowVolume { get => _settings.ShowVolume; set => Set(v => _settings.ShowVolume = v, value); }
    public bool ShowBattery { get => _settings.ShowBattery; set => Set(v => _settings.ShowBattery = v, value); }
    public bool ShowClock { get => _settings.ShowClock; set => Set(v => _settings.ShowClock = v, value); }
    public bool ShowDate { get => _settings.ShowDate; set => Set(v => _settings.ShowDate = v, value); }
    public bool ShowTimerAlarm { get => _settings.ShowTimerAlarm; set => Set(v => _settings.ShowTimerAlarm = v, value); }
    public bool FocusModeEnabled { get => _settings.FocusModeEnabled; set { Set(v => _settings.FocusModeEnabled = v, value); RaisePropertyChanged(nameof(FocusModeLabel)); _apply(); } }
    public string FocusModeLabel => FocusModeEnabled ? "Focus mode" : "All widgets";
    public bool NotificationHistoryEnabled { get => _settings.NotificationHistoryEnabled; set { Set(v => _settings.NotificationHistoryEnabled = v, value); _apply(); } }
    public bool Use24HourClock { get => _settings.Use24HourClock; set => Set(v => _settings.Use24HourClock = v, value); }
    public bool ShowSeconds { get => _settings.ShowSeconds; set => Set(v => _settings.ShowSeconds = v, value); }
    public bool DebugOverlay { get => _settings.DebugOverlay; set => Set(v => _settings.DebugOverlay = v, value); }
    public bool DebugLogging { get => _settings.DebugLogging; set => Set(v => _settings.DebugLogging = v, value); }
    public bool ShowIslandInScreenshots { get => _settings.ShowIslandInScreenshots; set { Set(v => _settings.ShowIslandInScreenshots = v, value); _apply(); } }
    public bool ShowInAltTab { get => _settings.ShowInAltTab; set => Set(v => _settings.ShowInAltTab = v, value); }
    public ThemeMode Theme
    {
        get => _settings.Theme;
        set { Set(v => _settings.Theme = v, value); RaisePropertyChanged(nameof(IsCustomTheme)); }
    }
    public bool IsCustomTheme => Theme == ThemeMode.Custom;
    public string CustomThemeColorHex
    {
        get => _settings.CustomThemeColorHex;
        set
        {
            if (!ThemeColor.TryParse(value, out var color)) { RaisePropertyChanged(); return; }
            _settings.CustomThemeColorHex = color.Hex;
            RaisePropertyChanged(); RaisePropertyChanged(nameof(CustomThemeColorBrush)); _apply();
        }
    }
    public System.Windows.Media.Brush CustomThemeColorBrush => new System.Windows.Media.SolidColorBrush(
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(CustomThemeColorHex));
    public IslandVisualMode IslandVisualMode { get => _settings.IslandVisualMode; set => Set(v => _settings.IslandVisualMode = v, value); }
    public IslandSize IslandSize
    {
        get => _settings.IslandSize;
        set
        {
            _settings.IslandSize = value;
            var (width, height) = value switch
            {
                IslandSize.Compact => (210, 54),
                IslandSize.Large => (260, 70),
                _ => (230, 62)
            };
            _settings.IslandWidth = width;
            _settings.IslandHeight = height;
            _settings.AutoGrowPill = false;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IslandWidth));
            RaisePropertyChanged(nameof(IslandHeight));
            RaisePropertyChanged(nameof(AutoGrowPill));
            _apply();
        }
    }
    public int IslandWidth
    {
        get => Math.Clamp(_settings.IslandWidth, 72, 360);
        set { _settings.IslandWidth = Math.Clamp(value, 72, 360); _settings.AutoGrowPill = false; RaisePropertyChanged(); RaisePropertyChanged(nameof(AutoGrowPill)); _apply(); }
    }
    public int IslandHeight
    {
        get => Math.Clamp(_settings.IslandHeight, 38, 90);
        set { _settings.IslandHeight = Math.Clamp(value, 38, 90); _settings.AutoGrowPill = false; RaisePropertyChanged(); RaisePropertyChanged(nameof(AutoGrowPill)); _apply(); }
    }
    public int IslandCornerRadius
    {
        get => _settings.IslandCornerRadius;
        set
        {
            _settings.IslandCornerRadius = Math.Clamp(value, 0, 48);
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(PreviewExpandedCorner));
            RaisePropertyChanged(nameof(PreviewMiniCorner));
            _apply();
        }
    }
    // Corner radius for the settings preview shapes. The same DIP radius reads differently on the tall
    // expanded pill vs the short mini pill, so each is scaled by (preview height / real pill height).
    private int ClampedIslandRadius => Math.Clamp(_settings.IslandCornerRadius, 0, 48);
    public System.Windows.CornerRadius PreviewExpandedCorner => new(ClampedIslandRadius * 0.23);
    public System.Windows.CornerRadius PreviewMiniCorner => new(ClampedIslandRadius * 0.5);
    public bool ScrollLongTitles { get => _settings.ScrollLongTitles; set { Set(v => _settings.ScrollLongTitles = v, value); _apply(); } }
    public AnimationIntensity AnimationIntensity { get => _settings.AnimationIntensity; set => Set(v => _settings.AnimationIntensity = v, value); }
    public PositionMode DefaultPosition
    {
        get => _settings.DefaultPosition;
        set
        {
            if (_settings.DefaultPosition == value) return;
            _settings.DefaultPosition = value;
            _settings.SideOffset = 0;
            _settings.TopOffset = 0;
            RefreshPositionControls();
            _apply();
            _ = _settingsService.SaveAsync(_settings);
        }
    }
    public int HorizontalOffsetMaximum => Math.Max(1000, System.Windows.Forms.Screen.AllScreens.Max(s => s.Bounds.Width));
    public int HorizontalOffsetMinimum => -HorizontalOffsetMaximum;
    public int VerticalOffsetMaximum => Math.Max(1000, System.Windows.Forms.Screen.AllScreens.Max(s => s.Bounds.Height));
    public int VerticalOffsetMinimum => -VerticalOffsetMaximum;
    public string PositionOffsetHelp => "Offsets are relative to the preset: positive values move inward from the right/bottom edges, or right/down from the center. Zero reaches the edge. Bottom presets sit above the taskbar.";
    public int SideOffset
    {
        get => _settings.SideOffset;
        set { _settings.SideOffset = Math.Clamp(value, HorizontalOffsetMinimum, HorizontalOffsetMaximum); RaisePropertyChanged(); _apply(); }
    }
    public int TopOffset
    {
        get => _settings.TopOffset;
        set { _settings.TopOffset = Math.Clamp(value, VerticalOffsetMinimum, VerticalOffsetMaximum); RaisePropertyChanged(); _apply(); }
    }
    public void RefreshPositionControls()
    {
        RaisePropertyChanged(nameof(DefaultPosition));
        RaisePropertyChanged(nameof(SideOffset));
        RaisePropertyChanged(nameof(TopOffset));
        RaisePropertyChanged(nameof(HorizontalOffsetMaximum));
        RaisePropertyChanged(nameof(HorizontalOffsetMinimum));
        RaisePropertyChanged(nameof(VerticalOffsetMaximum));
        RaisePropertyChanged(nameof(VerticalOffsetMinimum));
    }
    public string SelectedMediaApp { get => _settings.SelectedMediaApp; set => Set(v => _settings.SelectedMediaApp = v, value); }

    // ===== Per-element sizes (live) =====
    private void SetSize(Action<int> setter, int value, int min, int max)
    {
        setter(Math.Clamp(value, min, max));
        RaisePropertyChanged();
        _apply();
    }
    public int InterfaceScale { get => _settings.InterfaceScale; set => SetSize(v => _settings.InterfaceScale = v, value, 70, 150); }
    public int ClockSize { get => _settings.ClockSize; set => SetSize(v => _settings.ClockSize = v, value, 60, 160); }
    public int DateSize { get => _settings.DateSize; set => SetSize(v => _settings.DateSize = v, value, 60, 160); }
    public int BatterySize { get => _settings.BatterySize; set => SetSize(v => _settings.BatterySize = v, value, 60, 160); }
    public int MediaTitleSize { get => _settings.MediaTitleSize; set => SetSize(v => _settings.MediaTitleSize = v, value, 60, 160); }
    public int MediaArtistSize { get => _settings.MediaArtistSize; set => SetSize(v => _settings.MediaArtistSize = v, value, 60, 160); }
    public int VolumeSize { get => _settings.VolumeSize; set => SetSize(v => _settings.VolumeSize = v, value, 60, 160); }
    public int CompactTextSize { get => _settings.CompactTextSize; set => SetSize(v => _settings.CompactTextSize = v, value, 60, 160); }

    // ===== Colours & font =====
    public bool UseCustomColors { get => _settings.UseCustomColors; set { Set(v => _settings.UseCustomColors = v, value); _apply(); } }
    public bool AdaptiveAccent { get => _settings.AdaptiveAccent; set { Set(v => _settings.AdaptiveAccent = v, value); _apply(); } }
    public string AccentColorHex
    {
        get => _settings.AccentColorHex;
        set { if (IsHex(value)) { _settings.AccentColorHex = value; RaisePropertyChanged(); _apply(); } else { RaisePropertyChanged(); } }
    }
    public string TextColorHex
    {
        get => _settings.TextColorHex;
        set { if (string.IsNullOrWhiteSpace(value) || IsHex(value)) { _settings.TextColorHex = value ?? ""; RaisePropertyChanged(); _apply(); } }
    }
    public string FontFamilyName { get => _settings.FontFamilyName; set { Set(v => _settings.FontFamilyName = v, value); _apply(); } }
    public ObservableCollection<string> FontOptions { get; } = [];
    public string[] AccentSwatches { get; } =
        ["#5AA7FF", "#30D158", "#FF375F", "#BF5AF2", "#FF9F0A", "#64D2FF", "#FFD60A", "#FF6482"];

    // ===== Behaviour =====
    public bool AlwaysExpanded { get => _settings.AlwaysExpanded; set { Set(v => _settings.AlwaysExpanded = v, value); _apply(); } }
    public bool AutoGrowPill { get => _settings.AutoGrowPill; set { Set(v => _settings.AutoGrowPill = v, value); _apply(); } }
    public bool IdleDimming { get => _settings.IdleDimming; set { Set(v => _settings.IdleDimming = v, value); _apply(); } }
    public int IdleOpacityPercent { get => _settings.IdleOpacityPercent; set => SetSize(v => _settings.IdleOpacityPercent = v, value, 20, 100); }
    public bool AutoHideFullscreen { get => _settings.AutoHideFullscreen; set { Set(v => _settings.AutoHideFullscreen = v, value); _apply(); } }

    // ===== Position / monitor =====
    public ObservableCollection<string> MonitorOptions { get; } = [];
    public string PreferredMonitor { get => _settings.PreferredMonitor; set { Set(v => _settings.PreferredMonitor = v ?? "", value); _apply(); } }
    public bool FollowActiveScreen { get => _settings.FollowActiveScreen; set { Set(v => _settings.FollowActiveScreen = v, value); _apply(); } }

    // ===== Media =====
    public bool ClickArtOpensApp { get => _settings.ClickArtOpensApp; set => Set(v => _settings.ClickArtOpensApp = v, value); }

    // ===== Live activities =====
    public bool ShowWeather { get => _settings.ShowWeather; set { Set(v => _settings.ShowWeather = v, value); _apply(); } }
    public string WeatherLocation { get => _settings.WeatherLocation; set { Set(v => _settings.WeatherLocation = v ?? "", value); _apply(); } }
    public bool WeatherFahrenheit { get => _settings.WeatherFahrenheit; set { Set(v => _settings.WeatherFahrenheit = v, value); _apply(); } }
    public bool ShowSystemMonitor { get => _settings.ShowSystemMonitor; set { Set(v => _settings.ShowSystemMonitor = v, value); _apply(); } }
    public bool ShowRamInCompact { get => _settings.ShowRamInCompact; set { Set(v => _settings.ShowRamInCompact = v, value); _apply(); } }
    public bool RealAudioSpectrum { get => _settings.RealAudioSpectrum; set { Set(v => _settings.RealAudioSpectrum = v, value); _apply(); } }
    public bool ShowMusicVisualizer { get => _settings.ShowMusicVisualizer; set { Set(v => _settings.ShowMusicVisualizer = v, value); _apply(); } }
    public bool ShowConnectivity { get => _settings.ShowConnectivity; set { Set(v => _settings.ShowConnectivity = v, value); _apply(); } }

    // ===== Widgets / live activities (new) =====
    public bool ShowQuickLaunch { get => _settings.ShowQuickLaunch; set { Set(v => _settings.ShowQuickLaunch = v, value); _apply(); } }
    public string QuickLaunchItems { get => _settings.QuickLaunchItems; set { Set(v => _settings.QuickLaunchItems = v ?? "", value); _apply(); } }

    // ===== Quick-launch list editor =====
    public ObservableCollection<LaunchListItem> QuickLaunchList { get; } = [];
    public ICommand AddQuickLaunchCommand { get; private set; } = null!;
    public ICommand RemoveQuickLaunchCommand { get; private set; } = null!;
    public ICommand BrowseQuickLaunchCommand { get; private set; } = null!;
    public ICommand MoveQuickLaunchUpCommand { get; private set; } = null!;
    public ICommand MoveQuickLaunchDownCommand { get; private set; } = null!;

    private void RebuildQuickLaunch()
    {
        QuickLaunchList.Clear();
        foreach (var line in (_settings.QuickLaunchItems ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', 2);
            var name = parts[0].Trim();
            var path = parts.Length > 1 ? parts[1].Trim() : name;
            QuickLaunchList.Add(new LaunchListItem(name, path, SyncQuickLaunch));
        }
    }

    private void SyncQuickLaunch()
    {
        _settings.QuickLaunchItems = string.Join('\n', QuickLaunchList
            .Where(i => !string.IsNullOrWhiteSpace(i.Path))
            .Select(i => $"{(string.IsNullOrWhiteSpace(i.Name) ? System.IO.Path.GetFileNameWithoutExtension(i.Path) : i.Name)}|{i.Path}"));
        RaisePropertyChanged(nameof(QuickLaunchItems));
        _apply();
    }

    private void AddQuickLaunch()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        { Filter = "Apps and shortcuts (*.exe;*.lnk;*.bat;*.url)|*.exe;*.lnk;*.bat;*.url|All files (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;
        var name = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
        QuickLaunchList.Add(new LaunchListItem(name, dialog.FileName, SyncQuickLaunch));
        SyncQuickLaunch();
    }

    private void BrowseQuickLaunch(LaunchListItem? item)
    {
        if (item is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        { Filter = "Apps and shortcuts (*.exe;*.lnk;*.bat;*.url)|*.exe;*.lnk;*.bat;*.url|All files (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;
        if (string.IsNullOrWhiteSpace(item.Name)) item.Name = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
        item.Path = dialog.FileName; // setter calls SyncQuickLaunch
    }

    private void RemoveQuickLaunch(LaunchListItem? item)
    {
        if (item is null || !QuickLaunchList.Remove(item)) return;
        SyncQuickLaunch();
    }

    private void MoveQuickLaunch(LaunchListItem? item, int direction)
    {
        if (item is null) return;
        var i = QuickLaunchList.IndexOf(item);
        var j = i + direction;
        if (i < 0 || j < 0 || j >= QuickLaunchList.Count) return;
        QuickLaunchList.Move(i, j);
        SyncQuickLaunch();
    }
    public bool ShowCountdown { get => _settings.ShowCountdown; set { Set(v => _settings.ShowCountdown = v, value); _apply(); } }
    public string CountdownLabel { get => _settings.CountdownLabel; set { Set(v => _settings.CountdownLabel = v ?? "", value); _apply(); } }
    public string CountdownDate { get => _settings.CountdownDate; set { Set(v => _settings.CountdownDate = v ?? "", value); _apply(); } }

    // ===== Quotes =====
    public Array QuotePlacementOptions => Enum.GetValues<QuotePlacement>();
    public Array QuoteRotationOptions => Enum.GetValues<QuoteRotation>();
    public QuotePlacement QuotePlacement { get => _settings.QuotePlacement; set { Set(v => _settings.QuotePlacement = v, value); _apply(); } }
    public QuoteRotation QuoteRotation { get => _settings.QuoteRotation; set { Set(v => _settings.QuoteRotation = v, value); _apply(); } }
    public string QuotesText { get => _settings.QuotesText; set { Set(v => _settings.QuotesText = v ?? "", value); _apply(); } }
    public int QuoteSize { get => _settings.QuoteSize; set => SetSize(v => _settings.QuoteSize = v, value, 60, 160); }
    public bool ShowStocks { get => _settings.ShowStocks; set { Set(v => _settings.ShowStocks = v, value); _apply(); } }
    public string StockSymbols { get => _settings.StockSymbols; set { Set(v => _settings.StockSymbols = v ?? "", value); _apply(); } }
    public bool ShowWorldClocks { get => _settings.ShowWorldClocks; set { Set(v => _settings.ShowWorldClocks = v, value); _apply(); } }
    public string WorldClockZones { get => _settings.WorldClockZones; set { Set(v => _settings.WorldClockZones = v ?? "", value); _apply(); } }
    public bool ShowNextMeeting { get => _settings.ShowNextMeeting; set { Set(v => _settings.ShowNextMeeting = v, value); _apply(); } }
    public bool ShowNotifications { get => _settings.ShowNotifications; set { Set(v => _settings.ShowNotifications = v, value); _apply(); } }
    public Array NotificationFilterOptions => Enum.GetValues<NotificationFilter>();
    public NotificationFilter NotificationFilterMode { get => _settings.NotificationFilterMode; set { Set(v => _settings.NotificationFilterMode = v, value); _apply(); } }
    public string NotificationAppFilter { get => _settings.NotificationAppFilter; set { Set(v => _settings.NotificationAppFilter = v ?? "", value); _apply(); } }
    public bool ShowPrivacyIndicators { get => _settings.ShowPrivacyIndicators; set { Set(v => _settings.ShowPrivacyIndicators = v, value); _apply(); } }
    public bool QEnabled { get => _settings.QEnabled; set { Set(v => _settings.QEnabled = v, value); _apply(); } }
    private QActivationShortcuts ActivationKeys => QActivationPolicy.Resolve(_settings.QActivationKeys, _settings.QActivationHotkey);
    public bool QCtrlAltQ { get => ActivationKeys.HasFlag(QActivationShortcuts.CtrlAltQ); set => SetActivation(QActivationShortcuts.CtrlAltQ, value); }
    public bool QShiftA { get => ActivationKeys.HasFlag(QActivationShortcuts.ShiftA); set => SetActivation(QActivationShortcuts.ShiftA, value); }
    public bool QVarSequence { get => ActivationKeys.HasFlag(QActivationShortcuts.VarSequence); set => SetActivation(QActivationShortcuts.VarSequence, value); }
    public bool QShiftComma { get => ActivationKeys.HasFlag(QActivationShortcuts.ShiftComma); set => SetActivation(QActivationShortcuts.ShiftComma, value); }
    public bool QShiftPeriod { get => ActivationKeys.HasFlag(QActivationShortcuts.ShiftPeriod); set => SetActivation(QActivationShortcuts.ShiftPeriod, value); }
    private string _qActivationStatus = "";
    public string QActivationStatus => _qActivationStatus;
    public void SetQActivationStatus(string status)
    {
        if (_qActivationStatus == status) return;
        _qActivationStatus = status;
        RaisePropertyChanged(nameof(QActivationStatus));
    }
    private void SetActivation(QActivationShortcuts flag, bool enabled,
        [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        var choices = enabled ? ActivationKeys | flag : ActivationKeys & ~flag;
        if (choices == ActivationKeys) return;
        _settings.QActivationKeys = choices;
        RaisePropertyChanged(property);
        _apply();
        _ = _settingsService.SaveAsync(_settings);
    }
    public bool QAutoExpandIsland { get => _settings.QAutoExpandIsland; set { Set(v => _settings.QAutoExpandIsland = v, value); _apply(); } }
    public bool QAutoCloseAfterResponse { get => _settings.QAutoCloseAfterResponse; set { Set(v => _settings.QAutoCloseAfterResponse = v, value); _apply(); } }
    public int QAutoCloseDelaySeconds { get => _settings.QAutoCloseDelaySeconds; set => SetSize(v => _settings.QAutoCloseDelaySeconds = v, value, 1, 300); }
    public string QSelectedProvider
    {
        get => _settings.QSelectedProvider;
        set
        {
            if (string.IsNullOrWhiteSpace(value)
                || string.Equals(_settings.QSelectedProvider, value, StringComparison.OrdinalIgnoreCase)
                || _qProviders.Find(value) is not { } provider) return;
            var selected = QProviderSelection.Switch(_settings.QProviderPreferences ??= new(),
                _settings.QSelectedProvider, _settings.QSelectedModel, _settings.QReasoningEffort,
                provider.Info.Id, provider.Info.DefaultModel);
            _settings.QSelectedProvider = provider.Info.Id;
            _settings.QSelectedModel = selected.Model;
            _settings.QReasoningEffort = selected.ReasoningEffort;
            NormalizeProviderEffort();
            RefreshQProviderControls();
            _apply();
            _ = _settingsService.SaveAsync(_settings);
        }
    }
    public string QSelectedModel
    {
        get => _settings.QSelectedModel;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _settings.QSelectedModel) return;
            Set(v => _settings.QSelectedModel = v.Trim(), value);
            NormalizeProviderEffort();
            RaisePropertyChanged(nameof(QReasoningEffortOptions));
            _apply();
        }
    }
    public string QApiKey
    {
        get => _qSecrets.Get(_settings.QSelectedProvider) ?? "";
        set
        {
            _qSecrets.Set(_settings.QSelectedProvider, value.Trim());
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(QApiKeyStatus));
            RaisePropertyChanged(nameof(QSavedKeyProviders));
        }
    }
    public IReadOnlyList<QProviderChoice> QProviderOptions => _qProviders.Providers
        .Select(provider => new QProviderChoice(provider.Info.Id, provider.Info.DisplayName)).ToArray();
    public string QApiKeyStatus => string.IsNullOrWhiteSpace(QApiKey)
        ? "No key saved for this provider." : "Key saved for this provider. Paste a new key below to replace it.";
    public string QSavedKeyProviders
    {
        get
        {
            var saved = QProviderOptions.Where(p => !string.IsNullOrWhiteSpace(_qSecrets.Get(p.Id))).Select(p => p.Name).ToArray();
            return saved.Length == 0 ? "No API keys saved yet." : "Saved keys: " + string.Join(", ", saved);
        }
    }
    public void RefreshQProviderControls()
    {
        foreach (var property in new[] { nameof(QSelectedProvider), nameof(QModelOptions), nameof(QSelectedModel),
            nameof(QReasoningEffortOptions), nameof(QReasoningEffort), nameof(QApiKey), nameof(QApiKeyStatus),
            nameof(QSavedKeyProviders), nameof(QIsCodexSelected), nameof(QShowApiKey) })
            RaisePropertyChanged(property);
        QConnectionStatus = "Not tested";
    }
    public bool QIsCodexSelected => string.Equals(_settings.QSelectedProvider, "codex", StringComparison.OrdinalIgnoreCase);
    public bool QShowApiKey => !QIsCodexSelected && !string.Equals(_settings.QSelectedProvider, "ollama", StringComparison.OrdinalIgnoreCase);
    private CodexModel? SelectedCodexModel => _codexModels.FirstOrDefault(model => string.Equals(model.Id, _settings.QSelectedModel, StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<string> QModelOptions => QIsCodexSelected && _codexModels.Count > 0
        ? _codexModels.Select(model => model.Id).ToArray()
        : _providerModels.TryGetValue(_settings.QSelectedProvider, out var discovered) && discovered.Count > 0
            ? new[] { _settings.QSelectedModel }.Concat(discovered.Select(model => model.Id))
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()!
            : QProviderPolicy.ModelSuggestions(_settings.QSelectedProvider, _settings.QSelectedModel);
    public Array QCaptureModeOptions => Enum.GetValues<Models.QCaptureMode>();
    public Models.QCaptureMode QCaptureMode { get => _settings.QCaptureMode; set { Set(v => _settings.QCaptureMode = v, value); _apply(); } }
    public bool QIncludeScreenImage { get => _settings.QIncludeScreenImage; set { Set(v => _settings.QIncludeScreenImage = v, value); _apply(); } }
    public string QOllamaBaseUrl { get => _settings.QOllamaBaseUrl; set { Set(v => _settings.QOllamaBaseUrl = v ?? "http://localhost:11434", value); _apply(); } }
    public int QTimeoutSeconds { get => _settings.QTimeoutSeconds; set => SetSize(v => _settings.QTimeoutSeconds = v, value, 10, 300); }
    public int QMaxResponseTokens { get => _settings.QMaxResponseTokens; set => SetSize(v => _settings.QMaxResponseTokens = v, value, 2048, 32768); }
    public string QReasoningEffort
    {
        get => _settings.QReasoningEffort;
        set { if (string.IsNullOrWhiteSpace(value)) return; Set(v => _settings.QReasoningEffort = v, value); NormalizeProviderEffort(); _apply(); }
    }
    public IReadOnlyList<string> QReasoningEffortOptions => QIsCodexSelected
        ? CodexModelSelectionPolicy.EffortOptions(SelectedCodexModel)
        : QProviderPolicy.EffortOptions(_settings.QSelectedProvider, _settings.QSelectedModel);
    public string QAskSystemPrompt { get => _settings.QAskSystemPrompt; set { Set(v => _settings.QAskSystemPrompt = v ?? "", value); _apply(); } }
    public string QSaySystemPrompt { get => _settings.QSaySystemPrompt; set { Set(v => _settings.QSaySystemPrompt = v ?? "", value); _apply(); } }
    public string QHotkeyShortcut
    {
        get => string.IsNullOrWhiteSpace(_settings.QHotkeyShortcut) ? "None" : _settings.QHotkeyShortcut;
        set { Set(v => _settings.QHotkeyShortcut = string.Equals(v, "None", StringComparison.Ordinal) ? "" : v ?? "", value); _apply(); }
    }
    public IReadOnlyList<string> QHotkeyShortcutOptions => ["None", .. QShortcutList.Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name))];
    public string QConnectionStatus { get => _qConnectionStatus; private set => SetProperty(ref _qConnectionStatus, value); }
    public ICommand TestQCommand { get; }
    public ICommand SignInCodexCommand { get; }
    public ICommand RefreshCodexCommand { get; }
    public ICommand CancelCodexLoginCommand { get; }
    public ICommand SignOutCodexCommand { get; }
    public bool QCodexIsConnected => _codexAccount.Snapshot.IsConnected;
    public bool QCodexLoginPending => _codexAccount.Snapshot.State == CodexAccountState.LoginPending;
    public string QCodexActionLabel => QCodexIsConnected ? "Refresh" : QCodexLoginPending ? "Open sign-in" : "Sign in with ChatGPT";
    public string QCodexAccountStatus => _codexAccount.Snapshot switch
    {
        { State: CodexAccountState.Connected, Account: { } account } => $"{account.Email ?? "ChatGPT account"} · {account.PlanType ?? "plan unavailable"}",
        { State: CodexAccountState.LimitReached, Account: { } account } => $"{account.Email ?? "ChatGPT account"} · usage limit reached",
        { State: CodexAccountState.LoginPending, PendingLogin: { } login } => $"Enter code {login.UserCode} at {login.VerificationUrl}",
        { State: CodexAccountState.Checking } => "Checking account…",
        { State: CodexAccountState.RuntimeUnavailable, Error: { } error } => error,
        { State: CodexAccountState.Error, Error: { } error } => error,
        _ => "Not signed in"
    };
    public string QCodexUsageStatus
    {
        get
        {
            var snapshot = _codexAccount.Snapshot;
            var usage = snapshot.UsedPercent is int used ? $"{used}% used" : "Usage unavailable";
            var reset = snapshot.RateLimit?.ResetsAt is { } at ? $" · resets {at.LocalDateTime:g}" : string.Empty;
            var runtime = snapshot.Runtime is { } info ? $" · Codex {info.Version ?? "unknown"} ({info.Source})" : string.Empty;
            return usage + reset + runtime;
        }
    }
    public ICommand ResetQPromptsCommand { get; }
    public ObservableCollection<QShortcutItem> QShortcutList { get; } = [];
    public ICommand AddQShortcutCommand { get; }
    public ICommand RemoveQShortcutCommand { get; }
    public ICommand MoveQShortcutUpCommand { get; }
    public ICommand MoveQShortcutDownCommand { get; }

    private void ResetQPrompts()
    {
        QAskSystemPrompt = string.Empty;
        QSaySystemPrompt = string.Empty;
    }

    private void RebuildQShortcuts()
    {
        QShortcutList.Clear();
        foreach (var shortcut in _settings.QShortcuts ?? [])
            QShortcutList.Add(new QShortcutItem(shortcut.Name ?? "", shortcut.Prompt ?? "", SyncQShortcuts));
    }

    private void SyncQShortcuts()
    {
        _settings.QShortcuts = QShortcutList
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Prompt))
            .Select(item => new QShortcut { Name = item.Name.Trim(), Prompt = item.Prompt.Trim() })
            .ToList();
        if (!string.IsNullOrWhiteSpace(_settings.QHotkeyShortcut) &&
            !_settings.QShortcuts.Any(shortcut => string.Equals(shortcut.Name, _settings.QHotkeyShortcut, StringComparison.OrdinalIgnoreCase)))
            _settings.QHotkeyShortcut = "";
        RaisePropertyChanged(nameof(QHotkeyShortcut));
        RaisePropertyChanged(nameof(QHotkeyShortcutOptions));
        _apply();
    }

    private void AddQShortcut()
    {
        QShortcutList.Add(new QShortcutItem("New shortcut", "", SyncQShortcuts));
        RaisePropertyChanged(nameof(QHotkeyShortcutOptions));
        _apply();
    }

    private void RemoveQShortcut(QShortcutItem? item)
    {
        if (item is null || !QShortcutList.Remove(item)) return;
        SyncQShortcuts();
    }

    private void MoveQShortcut(QShortcutItem? item, int direction)
    {
        if (item is null) return;
        var index = QShortcutList.IndexOf(item);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= QShortcutList.Count) return;
        QShortcutList.Move(index, target);
        SyncQShortcuts();
    }

    private async Task TestQAsync()
    {
        var providerId = _settings.QSelectedProvider;
        var provider = _qProviders.Find(providerId);
        if (provider is null) { QConnectionStatus = "Provider unavailable"; return; }
        if (string.Equals(_settings.QSelectedProvider, "codex", StringComparison.OrdinalIgnoreCase))
        {
            await _codexAccount.RefreshAsync();
            if (_settings.QSelectedProvider != providerId) return;
            var snapshot = _codexAccount.Snapshot;
            QConnectionStatus = snapshot.IsConnected
                ? $"Connected · {(snapshot.Models?.Count ?? 0)} Codex models available" + (snapshot.UsedPercent is int used ? $" · {used}% used" : "")
                : snapshot.Error ?? "Sign in with ChatGPT/Codex first";
            return;
        }
        if (!string.Equals(_settings.QSelectedProvider, "ollama", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(_qSecrets.Get(_settings.QSelectedProvider)))
        {
            QConnectionStatus = "Add an API key first";
            return;
        }
        try
        {
            var baseUrl = string.Equals(_settings.QSelectedProvider, "ollama", StringComparison.OrdinalIgnoreCase)
                ? _settings.QOllamaBaseUrl : null;
            var models = await provider.GetModelsAsync(_qSecrets.Get(providerId), CancellationToken.None, baseUrl);
            _providerModels[providerId] = models;
            if (_settings.QSelectedProvider != providerId) return;
            RaisePropertyChanged(nameof(QModelOptions));
            QConnectionStatus = $"Connected · {models.Count} model{(models.Count == 1 ? "" : "s")} available";
        }
        catch (Exception ex) { if (_settings.QSelectedProvider == providerId) QConnectionStatus = ex.Message.Length > 120 ? ex.Message[..120] : ex.Message; }
    }

    private async Task SignInCodexAsync()
    {
        try
        {
            if (_codexAccount.Snapshot.IsConnected) { await _codexAccount.RefreshAsync().ConfigureAwait(false); return; }
            QConnectionStatus = "Starting ChatGPT sign-in…";
            var login = _codexAccount.Snapshot.PendingLogin ?? await _codexAccount.StartLoginAsync(CancellationToken.None).ConfigureAwait(false);
            Process.Start(new ProcessStartInfo { FileName = login.VerificationUrl, UseShellExecute = true });
            QConnectionStatus = "Waiting for ChatGPT sign-in…";
            await _codexAccount.CompleteLoginAsync(login).ConfigureAwait(false);
            QConnectionStatus = "ChatGPT/Codex connected";
        }
        catch (OperationCanceledException) { QConnectionStatus = "ChatGPT sign-in cancelled"; }
        catch (Exception ex) { QConnectionStatus = ex.Message.Length > 160 ? ex.Message[..160] : ex.Message; }
    }

    private async Task SignOutCodexAsync()
    {
        var choice = System.Windows.MessageBox.Show(
            "Signing out here also signs out official Codex apps using this Windows profile. Continue?",
            "Sign out of ChatGPT / Codex", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (choice == System.Windows.MessageBoxResult.Yes) await _codexAccount.LogoutAsync().ConfigureAwait(false);
    }

    private void NormalizeProviderEffort()
    {
        var normalized = QIsCodexSelected
            ? CodexModelSelectionPolicy.NormalizeEffort(SelectedCodexModel, _settings.QReasoningEffort)
            : QProviderPolicy.NormalizeEffort(_settings.QSelectedProvider, _settings.QSelectedModel, _settings.QReasoningEffort);
        if (string.Equals(normalized, _settings.QReasoningEffort, StringComparison.OrdinalIgnoreCase)) return;
        _settings.QReasoningEffort = normalized;
        RaisePropertyChanged(nameof(QReasoningEffort));
    }

    private void OnCodexAccountChanged(CodexAccountSnapshot snapshot)
    {
        void Apply()
        {
            _codexModels = snapshot.Models ?? _codexModels;
            NormalizeProviderEffort();
            RaisePropertyChanged(nameof(QCodexIsConnected));
            RaisePropertyChanged(nameof(QCodexLoginPending));
            RaisePropertyChanged(nameof(QCodexActionLabel));
            RaisePropertyChanged(nameof(QCodexAccountStatus));
            RaisePropertyChanged(nameof(QCodexUsageStatus));
            RaisePropertyChanged(nameof(QModelOptions));
            RaisePropertyChanged(nameof(QReasoningEffortOptions));
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply(); else dispatcher.BeginInvoke(Apply);
    }
    public bool ShowClipboard { get => _settings.ShowClipboard; set { Set(v => _settings.ShowClipboard = v, value); _apply(); } }
    public bool ShowBatteryTime { get => _settings.ShowBatteryTime; set { Set(v => _settings.ShowBatteryTime = v, value); _apply(); } }
    public bool LowBatteryWarning { get => _settings.LowBatteryWarning; set { Set(v => _settings.LowBatteryWarning = v, value); _apply(); } }
    public int LowBatteryThreshold { get => _settings.LowBatteryThreshold; set => SetSize(v => _settings.LowBatteryThreshold = v, value, 5, 50); }

    // ===== Volume warning =====
    public bool VolumeWarningEnabled { get => _settings.VolumeWarningEnabled; set { Set(v => _settings.VolumeWarningEnabled = v, value); _apply(); } }
    public int VolumeWarningThreshold { get => _settings.VolumeWarningThreshold; set => SetSize(v => _settings.VolumeWarningThreshold = v, value, 10, 100); }

    // ===== Module order =====
    public ObservableCollection<ModuleItem> ModuleOrder { get; } = [];

    // ===== Presets =====
    public ObservableCollection<string> Presets { get; } = [];
    private string _selectedPreset = "";
    public string SelectedPreset { get => _selectedPreset; set => SetProperty(ref _selectedPreset, value); }
    private string _newPresetName = "";
    public string NewPresetName { get => _newPresetName; set => SetProperty(ref _newPresetName, value); }

    // ===== Sidebar navigation + search =====
    public ObservableCollection<SettingsSection> Sections { get; } = [];
    private string _selectedSectionKey = "content";
    public string SelectedSectionKey { get => _selectedSectionKey; set => SetProperty(ref _selectedSectionKey, value); }
    private string _searchText = "";
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) RebuildSections(); } }

    public ICommand ImportCommand { get; } = null!;
    public ICommand ExportCommand { get; } = null!;
    public ICommand SavePresetCommand { get; } = null!;
    public ICommand ApplyPresetCommand { get; } = null!;
    public ICommand DeletePresetCommand { get; } = null!;
    public ICommand ApplyIslandPresetCommand { get; } = null!;
    public ICommand PickAccentCommand { get; } = null!;
    public ICommand MoveModuleUpCommand { get; } = null!;
    public ICommand MoveModuleDownCommand { get; } = null!;
    public ICommand SelectSectionCommand { get; } = null!;

    private static bool IsHex(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.StartsWith('#') && (s.Length == 7 || s.Length == 9)
        && s[1..].All(Uri.IsHexDigit);

    private static readonly (string Key, string Name)[] AllModules =
        [("media", "Now playing"), ("volume", "Volume"), ("status", "Clock & status")];

    private void InitLists()
    {
        foreach (var f in new[] { "Segoe UI Variable Text", "Segoe UI", "Segoe UI Variable Display",
            "Cascadia Code", "Consolas", "Arial", "Calibri", "Georgia", "Verdana", "Comic Sans MS" })
            FontOptions.Add(f);

        MonitorOptions.Add("Primary");
        MonitorOptions.Add("Active (follow cursor)");
        try { foreach (var s in System.Windows.Forms.Screen.AllScreens) MonitorOptions.Add(s.DeviceName); } catch { }

        RebuildModuleOrder();
        RebuildQuickLaunch();
        RefreshPresets();
        RebuildSections();
    }

    private void RebuildModuleOrder()
    {
        ModuleOrder.Clear();
        var order = (_settings.ExpandedOrder ?? "media,volume,status")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var key in order)
        {
            var match = AllModules.FirstOrDefault(m => m.Key == key);
            if (match.Key is not null) ModuleOrder.Add(new ModuleItem(match.Key, match.Name));
        }
        foreach (var m in AllModules) // append any missing
            if (ModuleOrder.All(x => x.Key != m.Key)) ModuleOrder.Add(new ModuleItem(m.Key, m.Name));
    }

    private void MoveModule(ModuleItem? item, int direction)
    {
        if (item is null) return;
        var i = ModuleOrder.IndexOf(item);
        var j = i + direction;
        if (i < 0 || j < 0 || j >= ModuleOrder.Count) return;
        ModuleOrder.Move(i, j);
        _settings.ExpandedOrder = string.Join(',', ModuleOrder.Select(m => m.Key));
        _apply();
    }

    private void RebuildSections()
    {
        var all = new[]
        {
            new SettingsSection("content", "Island Content", Glyph(0xE713)),
            new SettingsSection("appearance", "Appearance", Glyph(0xE790)),
            new SettingsSection("position", "Position", Glyph(0xE809)),
            new SettingsSection("activities", "Live Activities", Glyph(0xE753)),
            new SettingsSection("q", "Q Assistant", Glyph(0xE720)),
            new SettingsSection("advanced", "Advanced", Glyph(0xE9F5)),
        };
        var q = _searchText?.Trim() ?? "";
        Sections.Clear();
        foreach (var s in all)
            if (q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                Sections.Add(s);
        if (Sections.Count > 0 && Sections.All(s => s.Key != _selectedSectionKey))
            SelectedSectionKey = Sections[0].Key;
    }

    private static string Glyph(int codePoint) => char.ConvertFromUtf32(codePoint);

    private void RefreshPresets()
    {
        Presets.Clear();
        foreach (var p in _settingsService.ListPresets()) Presets.Add(p);
    }

    private async Task SavePresetAsync()
    {
        var name = string.IsNullOrWhiteSpace(NewPresetName) ? $"Preset {Presets.Count + 1}" : NewPresetName.Trim();
        await _settingsService.SavePresetAsync(_settings, name);
        NewPresetName = "";
        RefreshPresets();
        SelectedPreset = name;
    }

    private async Task ApplyPresetAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedPreset)) return;
        var loaded = await _settingsService.LoadPresetAsync(SelectedPreset);
        if (loaded is null) return;
        _settings.CopyFrom(loaded);
        RefreshAllAndApply();
    }

    private void DeletePreset()
    {
        if (string.IsNullOrWhiteSpace(SelectedPreset)) return;
        _settingsService.DeletePreset(SelectedPreset);
        RefreshPresets();
    }

    private void ApplyIslandPreset(string? preset)
    {
        var (size, width, height, radius) = preset switch
        {
            "Minimal" => (IslandSize.Compact, 210, 54, 20),
            "Roomy" => (IslandSize.Large, 260, 70, 26),
            _ => (IslandSize.Normal, 230, 62, 22)
        };
        _settings.IslandSize = size;
        _settings.IslandWidth = width;
        _settings.IslandHeight = height;
        _settings.IslandCornerRadius = radius;
        _settings.AutoGrowPill = false;
        RaisePropertyChanged(nameof(IslandSize));
        RaisePropertyChanged(nameof(IslandWidth));
        RaisePropertyChanged(nameof(IslandHeight));
        RaisePropertyChanged(nameof(IslandCornerRadius));
        RaisePropertyChanged(nameof(AutoGrowPill));
        RaisePropertyChanged(nameof(PreviewExpandedCorner));
        RaisePropertyChanged(nameof(PreviewMiniCorner));
        _apply();
    }

    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        { Filter = "Dynamic Island settings (*.json)|*.json", FileName = "dynamic-island-settings.json" };
        if (dialog.ShowDialog() == true) _ = _settingsService.ExportAsync(_settings, dialog.FileName);
    }

    private async void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Dynamic Island settings (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        var loaded = await _settingsService.ImportAsync(dialog.FileName);
        if (loaded is null) return;
        _settings.CopyFrom(loaded);
        RefreshAllAndApply();
    }

    private void RefreshAllAndApply()
    {
        RebuildModuleOrder();
        RebuildQuickLaunch();
        RaisePropertyChanged(string.Empty);
        _ = SaveAsync();
    }

    public ICommand SaveCommand { get; }
    public ICommand RecenterCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ResetCommand { get; }
    public void SetAvailableApps(IEnumerable<string> apps)
    {
        var values = new[] { "Automatic" }.Concat(apps).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        AvailableMediaApps.Clear();
        foreach (var value in values) AvailableMediaApps.Add(value);
        RaisePropertyChanged(nameof(AvailableMediaApps));
    }

    private void ResetToDefaults()
    {
        var choice = System.Windows.MessageBox.Show(
            "Reset all Dynamic Island settings back to their defaults?\n\nThis affects appearance, sizes, " +
            "position, activities, and integrations.",
            "Reset to defaults", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (choice != System.Windows.MessageBoxResult.OK) return;

        _settings.ResetToDefaults();
        RebuildModuleOrder();
        RebuildQuickLaunch();
        RebuildQShortcuts();
        RaisePropertyChanged(string.Empty); // refresh every bound control in the settings windows
        _ = SaveAsync(); // persists + re-applies to the island (and syncs the startup registry)
    }

    public async Task SaveAsync(bool apply = true)
    {
        if (_settings.LaunchOnStartup != _startupService.IsEnabled())
            _settings.LaunchOnStartup = _startupService.SetEnabled(_settings.LaunchOnStartup);
        await _settingsService.SaveAsync(_settings);
        if (apply) _apply();
    }

    private void Set<T>(Action<T> setter, T value, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        setter(value);
        RaisePropertyChanged(property);
        // Every simple settings binding flows through this helper. Previously these controls only
        // changed the in-memory model, so most toggles/combo boxes appeared inert until the window
        // was closed and saved. Apply immediately to keep the preview and real island in sync.
        _apply();
    }
}

public sealed record ModuleItem(string Key, string Name);
public sealed record PositionChoice(PositionMode Value, string Name)
{
    public override string ToString() => Name;
}
public sealed record SettingsSection(string Key, string Name, string Glyph);

public sealed class LaunchListItem : ObservableObject
{
    private readonly Action _changed;
    private string _name;
    private string _path;
    public LaunchListItem(string name, string path, Action changed)
    {
        _name = name;
        _path = path;
        _changed = changed;
    }
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) _changed(); } }
    public string Path { get => _path; set { if (SetProperty(ref _path, value)) _changed(); } }
}
