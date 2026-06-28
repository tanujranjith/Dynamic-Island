using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Windows.Infrastructure;
using DynamicIsland.Windows.Models;
using DynamicIsland.Windows.Services;
using DynamicIsland.Windows.Services.Vision;

namespace DynamicIsland.Windows.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly StartupService _startupService;
    private readonly VisionService _vision;
    private readonly VisionModelManager _visionModels;
    private readonly Action _apply;
    private readonly Action _recenter;
    private readonly Action _close;
    private string _visionStatusLine = string.Empty;
    private bool _visionBusy;
    private BitmapImage? _cameraPreview;
    private bool _previewActive;
    private bool _isEnrolling;
    private double _enrollProgress;
    private string _enrollTitle = "Set up Face login";
    private string _enrollMessage = string.Empty;
    private bool _enrollSucceeded;
    private bool _enrollFailed;

    public SettingsViewModel(AppSettings settings, SettingsService settingsService,
        StartupService startupService, VisionService vision, VisionModelManager visionModels,
        Action apply, Action recenter, Action close)
    {
        _settings = settings;
        _settingsService = settingsService;
        _startupService = startupService;
        _vision = vision;
        _visionModels = visionModels;
        _apply = apply;
        _recenter = recenter;
        _close = close;
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        RecenterCommand = new RelayCommand(() => { _recenter(); _ = SaveAsync(false); });
        CloseCommand = new RelayCommand(() => { _ = SaveAsync(); _close(); });
        ResetCommand = new RelayCommand(ResetToDefaults);
        EnrollCommand = new RelayCommand(() => _ = StartEnrollAsync(), () => !_visionBusy);
        CancelEnrollCommand = new RelayCommand(CancelEnroll);
        DismissEnrollCommand = new RelayCommand(() => IsEnrolling = false);
        RemoveEnrollmentCommand = new RelayCommand(RemoveEnrollment);
        DownloadModelsCommand = new RelayCommand(() => _ = DownloadModelsAsync(), () => !_visionBusy);
        OpenModelsFolderCommand = new RelayCommand(OpenModelsFolder);
        OpenVisionPageCommand = new RelayCommand(() => OpenVisionPage?.Invoke());
        ImportCommand = new RelayCommand(Import);
        ExportCommand = new RelayCommand(Export);
        SavePresetCommand = new RelayCommand(() => _ = SavePresetAsync());
        ApplyPresetCommand = new RelayCommand(() => _ = ApplyPresetAsync());
        DeletePresetCommand = new RelayCommand(DeletePreset);
        PickAccentCommand = new RelayCommand<string>(hex => { if (hex is not null) AccentColorHex = hex; });
        MoveModuleUpCommand = new RelayCommand<ModuleItem>(m => MoveModule(m, -1));
        MoveModuleDownCommand = new RelayCommand<ModuleItem>(m => MoveModule(m, +1));
        SelectSectionCommand = new RelayCommand<string>(k => { if (k is not null) SelectedSectionKey = k; });
        ApplySkinCommand = new RelayCommand<string>(ApplySkin);
        _vision.FrameReady += OnVisionFrame;
        _vision.EnrollProgressChanged += OnEnrollProgress;
        _visionStatusLine = BuildVisionStatus();
        InitLists();
    }

    // Called by the camera settings window so live preview frames flow only while it is open.
    public void BeginPreview()
    {
        if (_previewActive) return;
        _previewActive = true;
        _vision.RetainPreview();
        RefreshVisionStatus();
    }

    public void EndPreview()
    {
        if (!_previewActive) return;
        _previewActive = false;
        _vision.ReleasePreview();
        _cameraPreview = null;
        RaisePropertyChanged(nameof(CameraPreview));
        RaisePropertyChanged(nameof(HasCameraPreview));
    }

    public ImageSource? CameraPreview => _cameraPreview;
    public bool HasCameraPreview => _cameraPreview is not null;

    private void OnVisionFrame(object? sender, byte[] jpeg)
    {
        if (!_previewActive) return;
        void Apply()
        {
            try
            {
                var image = new BitmapImage();
                using var stream = new MemoryStream(jpeg);
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                _cameraPreview = image;
            }
            catch { return; }
            RaisePropertyChanged(nameof(CameraPreview));
            RaisePropertyChanged(nameof(HasCameraPreview));
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply(); else dispatcher.BeginInvoke(Apply);
    }

    public ObservableCollection<string> AvailableMediaApps { get; } = ["Automatic"];
    public Array ThemeOptions => Enum.GetValues<ThemeMode>();
    public Array SizeOptions => Enum.GetValues<IslandSize>();
    public Array AnimationOptions => Enum.GetValues<AnimationIntensity>();
    public Array PositionOptions => Enum.GetValues<PositionMode>();

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
        set { _settings.AlbumCornerRadius = Math.Clamp(value, 0, 50); RaisePropertyChanged(); _apply(); }
    }
    public bool ShowMediaProgressRing { get => _settings.ShowMediaProgressRing; set { Set(v => _settings.ShowMediaProgressRing = v, value); _apply(); } }
    public bool ShowSongTimeRemaining { get => _settings.ShowSongTimeRemaining; set { Set(v => _settings.ShowSongTimeRemaining = v, value); _apply(); } }
    public bool ShowTimerRing { get => _settings.ShowTimerRing; set { Set(v => _settings.ShowTimerRing = v, value); _apply(); } }
    public bool LiquidGlass { get => _settings.LiquidGlass; set { Set(v => _settings.LiquidGlass = v, value); _apply(); } }
    // Exposed as transparency (0 = opaque, higher = more see-through) to match how users think about it.
    public int GlassTransparency
    {
        get => 100 - _settings.GlassOpacity;
        set { _settings.GlassOpacity = 100 - Math.Clamp(value, 0, 80); RaisePropertyChanged(); _apply(); }
    }
    public bool ShowVolume { get => _settings.ShowVolume; set => Set(v => _settings.ShowVolume = v, value); }
    public bool ShowBattery { get => _settings.ShowBattery; set => Set(v => _settings.ShowBattery = v, value); }
    public bool ShowClock { get => _settings.ShowClock; set => Set(v => _settings.ShowClock = v, value); }
    public bool ShowDate { get => _settings.ShowDate; set => Set(v => _settings.ShowDate = v, value); }
    public bool ShowTimerAlarm { get => _settings.ShowTimerAlarm; set => Set(v => _settings.ShowTimerAlarm = v, value); }
    public bool Use24HourClock { get => _settings.Use24HourClock; set => Set(v => _settings.Use24HourClock = v, value); }
    public bool ShowSeconds { get => _settings.ShowSeconds; set => Set(v => _settings.ShowSeconds = v, value); }
    public bool DebugOverlay { get => _settings.DebugOverlay; set => Set(v => _settings.DebugOverlay = v, value); }
    public bool DebugLogging { get => _settings.DebugLogging; set => Set(v => _settings.DebugLogging = v, value); }
    public bool ShowInAltTab { get => _settings.ShowInAltTab; set => Set(v => _settings.ShowInAltTab = v, value); }
    public ThemeMode Theme { get => _settings.Theme; set => Set(v => _settings.Theme = v, value); }
    public IslandSize IslandSize { get => _settings.IslandSize; set => Set(v => _settings.IslandSize = v, value); }
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
    public PositionMode DefaultPosition { get => _settings.DefaultPosition; set => Set(v => _settings.DefaultPosition = v, value); }
    public int TopOffset
    {
        get => _settings.TopOffset;
        set { _settings.TopOffset = Math.Clamp(value, 0, 120); RaisePropertyChanged(); _apply(); }
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
    public int VisionTextSize { get => _settings.VisionTextSize; set => SetSize(v => _settings.VisionTextSize = v, value, 60, 160); }
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
    public string GlassColorHex
    {
        get => _settings.GlassColorHex;
        set { if (string.IsNullOrWhiteSpace(value) || IsHex(value)) { _settings.GlassColorHex = value ?? ""; RaisePropertyChanged(); _apply(); } }
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

    // ===== Widgets / live activities (new) =====
    public bool ShowQuickLaunch { get => _settings.ShowQuickLaunch; set { Set(v => _settings.ShowQuickLaunch = v, value); _apply(); } }
    public string QuickLaunchItems { get => _settings.QuickLaunchItems; set { Set(v => _settings.QuickLaunchItems = v ?? "", value); _apply(); } }
    public bool ShowCountdown { get => _settings.ShowCountdown; set { Set(v => _settings.ShowCountdown = v, value); _apply(); } }
    public string CountdownLabel { get => _settings.CountdownLabel; set { Set(v => _settings.CountdownLabel = v ?? "", value); _apply(); } }
    public string CountdownDate { get => _settings.CountdownDate; set { Set(v => _settings.CountdownDate = v ?? "", value); _apply(); } }
    public bool ShowStocks { get => _settings.ShowStocks; set { Set(v => _settings.ShowStocks = v, value); _apply(); } }
    public string StockSymbols { get => _settings.StockSymbols; set { Set(v => _settings.StockSymbols = v ?? "", value); _apply(); } }
    public bool ShowWorldClocks { get => _settings.ShowWorldClocks; set { Set(v => _settings.ShowWorldClocks = v, value); _apply(); } }
    public string WorldClockZones { get => _settings.WorldClockZones; set { Set(v => _settings.WorldClockZones = v ?? "", value); _apply(); } }
    public bool ShowNextMeeting { get => _settings.ShowNextMeeting; set { Set(v => _settings.ShowNextMeeting = v, value); _apply(); } }
    public bool ShowNotifications { get => _settings.ShowNotifications; set { Set(v => _settings.ShowNotifications = v, value); _apply(); } }
    public bool ShowClipboard { get => _settings.ShowClipboard; set { Set(v => _settings.ShowClipboard = v, value); _apply(); } }
    public bool ShowBatteryTime { get => _settings.ShowBatteryTime; set { Set(v => _settings.ShowBatteryTime = v, value); _apply(); } }
    public bool LowBatteryWarning { get => _settings.LowBatteryWarning; set { Set(v => _settings.LowBatteryWarning = v, value); _apply(); } }
    public int LowBatteryThreshold { get => _settings.LowBatteryThreshold; set => SetSize(v => _settings.LowBatteryThreshold = v, value, 5, 50); }

    // ===== Theme skins =====
    public string[] ThemeSkins { get; } = ["Default", "Minimal", "Frosted", "Cyberpunk", "Mono", "Sunset"];
    public ICommand ApplySkinCommand { get; private set; } = null!;

    // ===== Camera automations =====
    public bool AutoLockOnUnknown { get => _settings.AutoLockOnUnknown; set { Set(v => _settings.AutoLockOnUnknown = v, value); _apply(); } }
    public int AutoLockDelaySeconds { get => _settings.AutoLockDelaySeconds; set => SetSize(v => _settings.AutoLockDelaySeconds = v, value, 2, 60); }
    public bool PresenceAwareMedia { get => _settings.PresenceAwareMedia; set { Set(v => _settings.PresenceAwareMedia = v, value); _apply(); } }
    public bool PrivacyAutoBlur { get => _settings.PrivacyAutoBlur; set { Set(v => _settings.PrivacyAutoBlur = v, value); _apply(); } }

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
    private string _selectedSectionKey = "general";
    public string SelectedSectionKey { get => _selectedSectionKey; set => SetProperty(ref _selectedSectionKey, value); }
    private string _searchText = "";
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) RebuildSections(); } }

    public ICommand ImportCommand { get; } = null!;
    public ICommand ExportCommand { get; } = null!;
    public ICommand SavePresetCommand { get; } = null!;
    public ICommand ApplyPresetCommand { get; } = null!;
    public ICommand DeletePresetCommand { get; } = null!;
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
            new SettingsSection("general", "General", Glyph(0xE713)),
            new SettingsSection("appearance", "Appearance", Glyph(0xE790)),
            new SettingsSection("sizes", "Text & Sizes", Glyph(0xE8E9)),
            new SettingsSection("layout", "Layout & Position", Glyph(0xE809)),
            new SettingsSection("media", "Media", Glyph(0xE768)),
            new SettingsSection("activities", "Live Activities", Glyph(0xE753)),
            new SettingsSection("widgets", "Widgets", Glyph(0xE8A9)),
            new SettingsSection("camera", "Camera", Glyph(0xE722)),
            new SettingsSection("advanced", "Advanced", Glyph(0xE9F5)),
            new SettingsSection("about", "About", Glyph(0xE946)),
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

    private void ApplySkin(string? name)
    {
        switch (name)
        {
            case "Minimal": SetSkin(true, "#8E8E93", "", "", false, 70, "Segoe UI Variable Text"); break;
            case "Frosted": SetSkin(true, "#64D2FF", "", "#3A4A66", true, 42, "Segoe UI Variable Text"); break;
            case "Cyberpunk": SetSkin(true, "#FF2D95", "#E6FBFF", "#1A0A2E", true, 72, "Consolas"); break;
            case "Mono": SetSkin(true, "#FFFFFF", "#FFFFFF", "#101014", false, 82, "Segoe UI"); break;
            case "Sunset": SetSkin(true, "#FF9F0A", "#FFF1E6", "#2A1622", true, 60, "Georgia"); break;
            default: _settings.UseCustomColors = false; break; // Default = follow theme
        }
        _settings.ThemeSkin = name ?? "";
        RaisePropertyChanged(string.Empty);
        _apply();
        _ = SaveAsync(false);
    }

    private void SetSkin(bool custom, string accent, string text, string glass, bool liquid, int opacity, string font)
    {
        _settings.UseCustomColors = custom;
        _settings.AccentColorHex = accent;
        _settings.TextColorHex = text;
        _settings.GlassColorHex = glass;
        _settings.LiquidGlass = liquid;
        _settings.GlassOpacity = opacity;
        _settings.FontFamilyName = font;
    }

    private void RefreshAllAndApply()
    {
        RebuildModuleOrder();
        RaisePropertyChanged(string.Empty);
        RefreshVisionStatus();
        _ = SaveAsync();
    }

    // ===== Camera presence (vision) =====
    public bool VisionEnabled
    {
        get => _settings.VisionEnabled;
        set { Set(v => _settings.VisionEnabled = v, value); _apply(); RefreshVisionStatus(); }
    }
    public bool VisionPrivacyMode
    {
        get => _settings.VisionPrivacyMode;
        set { Set(v => _settings.VisionPrivacyMode = v, value); _apply(); }
    }
    public bool ShowVisionStatus
    {
        get => _settings.ShowVisionStatus;
        set { Set(v => _settings.ShowVisionStatus = v, value); _apply(); }
    }
    public string VisionStatusLine
    {
        get => _visionStatusLine;
        private set => SetProperty(ref _visionStatusLine, value);
    }

    // ===== Guided face-enrollment overlay =====
    public bool IsEnrolling
    {
        get => _isEnrolling;
        private set { if (SetProperty(ref _isEnrolling, value)) RaisePropertyChanged(nameof(EnrollInProgress)); }
    }
    public double EnrollProgress { get => _enrollProgress; private set => SetProperty(ref _enrollProgress, value); }
    public double EnrollRingPerimeterUnits => 106.8; // circumference / stroke thickness for the 210px ring
    public string EnrollTitle { get => _enrollTitle; private set => SetProperty(ref _enrollTitle, value); }
    public string EnrollMessage { get => _enrollMessage; private set => SetProperty(ref _enrollMessage, value); }
    public bool EnrollSucceeded
    {
        get => _enrollSucceeded;
        private set { if (SetProperty(ref _enrollSucceeded, value)) RaisePropertyChanged(nameof(EnrollInProgress)); }
    }
    public bool EnrollFailed
    {
        get => _enrollFailed;
        private set { if (SetProperty(ref _enrollFailed, value)) RaisePropertyChanged(nameof(EnrollInProgress)); }
    }
    public bool EnrollInProgress => _isEnrolling && !_enrollSucceeded && !_enrollFailed;

    public ICommand SaveCommand { get; }
    public ICommand RecenterCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand EnrollCommand { get; }
    public ICommand CancelEnrollCommand { get; }
    public ICommand DismissEnrollCommand { get; }
    public ICommand RemoveEnrollmentCommand { get; }
    public ICommand DownloadModelsCommand { get; }
    public ICommand OpenModelsFolderCommand { get; }
    public ICommand OpenVisionPageCommand { get; }
    /// <summary>Supplied by the composition root to open the standalone camera settings window.</summary>
    public Action? OpenVisionPage { get; set; }

    private string BuildVisionStatus()
    {
        var paths = _visionModels.Resolve();
        var person = paths.PersonReady ? "person model ready" : "person model missing";
        var face = paths.FaceReady ? "face models ready" : "face models missing";
        var enrolled = _vision.IsEnrolled ? "face enrolled" : "not enrolled";
        return $"{person} · {face} · {enrolled}";
    }

    private void RefreshVisionStatus() => VisionStatusLine = BuildVisionStatus();

    private void SetVisionBusy(bool busy)
    {
        _visionBusy = busy;
        (EnrollCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DownloadModelsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task StartEnrollAsync()
    {
        // Open the overlay immediately so the flow feels responsive.
        EnrollTitle = "Set up Face login";
        EnrollSucceeded = false;
        EnrollFailed = false;
        EnrollProgress = 0;
        IsEnrolling = true;

        // Make sure the camera is actually running so there is something to enroll from.
        if (!_settings.VisionEnabled) VisionEnabled = true;
        if (!_visionModels.Resolve().FaceReady)
        {
            EnrollMessage = "Face models are missing. Download them on this page, then try again.";
            EnrollFailed = true;
            return;
        }

        EnrollMessage = "Center your face in the circle and hold still.";
        SetVisionBusy(true);
        try
        {
            var ok = await _vision.EnrollAsync();
            // Final phase text comes from OnEnrollProgress; this is the fallback if the loop never started.
            if (!ok && !_enrollFailed && !_enrollSucceeded)
            {
                EnrollFailed = true;
                EnrollMessage = "Couldn't start the camera. Make sure it isn't in use by another app.";
            }
            RefreshVisionStatus();
        }
        finally { SetVisionBusy(false); }
    }

    private void CancelEnroll()
    {
        _vision.CancelEnroll();
        IsEnrolling = false;
    }

    private void OnEnrollProgress(object? sender, EnrollProgress e)
    {
        void Apply()
        {
            EnrollProgress = e.Target > 0 ? Math.Min(100, e.Captured * 100.0 / e.Target) : 0;
            switch (e.Phase)
            {
                case EnrollPhase.Searching:
                    EnrollMessage = "Looking for your face… come a little closer.";
                    break;
                case EnrollPhase.Capturing:
                    EnrollMessage = $"Hold still… capturing {e.Captured}/{e.Target}";
                    break;
                case EnrollPhase.Completed:
                    EnrollProgress = 100;
                    EnrollSucceeded = true;
                    EnrollTitle = "You're all set";
                    EnrollMessage = "Face login is ready. Privacy mode can now recognise you.";
                    RefreshVisionStatus();
                    break;
                case EnrollPhase.Failed:
                    EnrollFailed = true;
                    EnrollTitle = "Let's try that again";
                    EnrollMessage = "Couldn't get a clear read. Face the camera in good light and retry.";
                    break;
            }
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply(); else dispatcher.BeginInvoke(Apply);
    }

    private void RemoveEnrollment()
    {
        _vision.RemoveEnrollment();
        RefreshVisionStatus();
    }

    private async Task DownloadModelsAsync()
    {
        if (!_settings.VisionModelsConsented)
        {
            var choice = System.Windows.MessageBox.Show(
                "Download the camera detection models (~25 MB) from public GitHub repositories?\n\n" +
                "They are saved locally and only the model files are downloaded — no images leave your PC.",
                "Download detection models", System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Question);
            if (choice != System.Windows.MessageBoxResult.OK) return;
            Set(v => _settings.VisionModelsConsented = v, true);
        }

        SetVisionBusy(true);
        var progress = new Progress<string>(message => VisionStatusLine = message);
        try
        {
            var ok = await _visionModels.DownloadAsync(progress, CancellationToken.None);
            VisionStatusLine = (ok ? "Models ready. " : "") + BuildVisionStatus();
            // Restart the camera loop so a running detector reloads the freshly downloaded models.
            if (ok && _settings.VisionEnabled) { _vision.Stop(); _apply(); }
        }
        finally { SetVisionBusy(false); }
    }

    private void OpenModelsFolder()
    {
        try
        {
            Directory.CreateDirectory(_visionModels.ModelsDir);
            Process.Start(new ProcessStartInfo { FileName = _visionModels.ModelsDir, UseShellExecute = true });
        }
        catch { /* best effort */ }
    }

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
            "position and camera options. Your enrolled face and downloaded models are kept.",
            "Reset to defaults", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (choice != System.Windows.MessageBoxResult.OK) return;

        _settings.ResetToDefaults();
        RaisePropertyChanged(string.Empty); // refresh every bound control in the settings windows
        RefreshVisionStatus();
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
    }
}

public sealed record ModuleItem(string Key, string Name);
public sealed record SettingsSection(string Key, string Name, string Glyph);
