using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Windows.Infrastructure;
using DynamicIsland.Windows.Models;
using DynamicIsland.Windows.Services;
using MediaBrush = System.Windows.Media.Brush;

namespace DynamicIsland.Windows.ViewModels;

public sealed class IslandViewModel : ObservableObject, IDisposable
{
    private readonly MediaSessionService _mediaService;
    private readonly AudioSessionService _audioService;
    private readonly BatteryService _batteryService;
    private readonly ClockService _clockService;
    private readonly TimerAlarmService _timerAlarmService;
    private readonly ThemeService _themeService;
    private readonly Services.Vision.VisionService _visionService;
    private readonly WeatherService _weatherService;
    private readonly SystemMonitorService _systemMonitorService;
    private readonly AudioSpectrumService _spectrumService;
    private readonly StocksService _stocksService;
    private readonly CalendarService _calendarService;
    private readonly NotificationListenerService _notificationService;
    private readonly System.Windows.Threading.DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private IReadOnlyList<StockQuote> _stocks = [];
    private MeetingInfo? _meeting;
    private NotificationInfo? _notification;
    private int _notificationSeq;
    private MediaInfo _media = MediaInfo.Empty;
    private AudioState _audio = AudioState.Unknown;
    private VisionState _vision = VisionState.Disabled;
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
    private BitmapImage? _cameraPreview;
    private bool _previewRetained;

    public IslandViewModel(AppSettings settings, MediaSessionService mediaService,
        AudioSessionService audioService, BatteryService batteryService, ClockService clockService,
        TimerAlarmService timerAlarmService, ThemeService themeService,
        Services.Vision.VisionService visionService, WeatherService weatherService,
        SystemMonitorService systemMonitorService, AudioSpectrumService spectrumService,
        StocksService stocksService, CalendarService calendarService,
        NotificationListenerService notificationService)
    {
        Settings = settings;
        _mediaService = mediaService;
        _audioService = audioService;
        _batteryService = batteryService;
        _clockService = clockService;
        _timerAlarmService = timerAlarmService;
        _themeService = themeService;
        _visionService = visionService;
        _weatherService = weatherService;
        _systemMonitorService = systemMonitorService;
        _spectrumService = spectrumService;
        _stocksService = stocksService;
        _calendarService = calendarService;
        _notificationService = notificationService;

        PreviousCommand = new RelayCommand(() => _ = _mediaService.PreviousAsync(), () => Media.CanPrevious);
        PlayPauseCommand = new RelayCommand(() => _ = _mediaService.TogglePlayPauseAsync(), () => Media.CanPlayPause);
        NextCommand = new RelayCommand(() => _ = _mediaService.NextAsync(), () => Media.CanNext);
        SeekBackCommand = new RelayCommand(() => _ = _mediaService.SeekByAsync(TimeSpan.FromSeconds(-SeekStepSeconds)), () => CanSeek);
        SeekForwardCommand = new RelayCommand(() => _ = _mediaService.SeekByAsync(TimeSpan.FromSeconds(SeekStepSeconds)), () => CanSeek);
        ToggleMuteCommand = new RelayCommand(() => _audioService.SetMuted(!Audio.SystemMuted),
            () => Audio.Availability == AudioAvailability.Available);
        AdjustVolumeCommand = new RelayCommand<string>(delta =>
        {
            if (int.TryParse(delta, out var amount))
                _audioService.SetMasterVolume(Audio.MasterVolumePercent + amount);
        });
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);

        _mediaService.Changed += OnMediaChanged;
        _audioService.Changed += OnAudioChanged;
        _visionService.Changed += OnVisionChanged;
        _visionService.FrameReady += OnVisionFrame;
        _weatherService.Changed += OnWeatherChanged;
        _systemMonitorService.Changed += OnSysStatsChanged;
        _spectrumService.BandsChanged += OnSpectrumChanged;
        _stocksService.Changed += OnStocksChanged;
        _calendarService.Changed += OnMeetingChanged;
        _notificationService.Notified += OnNotified;
        _notificationTimer.Tick += (_, _) => { _notificationTimer.Stop(); _notification = null; RaiseMany(nameof(ShowNotification)); };
        LaunchCommand = new RelayCommand<string>(LaunchApp);
        OpenMeetingCommand = new RelayCommand(() => { if (!string.IsNullOrWhiteSpace(_meeting?.JoinUrl)) OpenUrl(_meeting!.JoinUrl); });
        SeekCommand = new RelayCommand<double>(f => _ = _mediaService.SeekFractionAsync(f));
        OpenMediaAppCommand = new RelayCommand(() => { if (Settings.ClickArtOpensApp) _mediaService.LaunchSource(); });
        ToggleFavoriteCommand = new RelayCommand(() => IsFavorite = !IsFavorite);
        _batteryService.Changed += OnBatteryChanged;
        _clockService.Tick += OnClockTick;
        _timerAlarmService.Changed += OnTimerAlarmChanged;
        _themeService.SystemThemeChanged += OnSystemThemeChanged;
        ApplySettings();
    }

    public AppSettings Settings { get; }
    public MediaInfo Media { get => _media; private set { if (SetProperty(ref _media, value)) UpdateArtwork(value.Artwork); } }
    public AudioState Audio { get => _audio; private set => SetProperty(ref _audio, value); }
    public BatteryState Battery { get => _battery; private set => SetProperty(ref _battery, value); }
    public bool IsExpanded { get => _isExpanded; set { if (SetProperty(ref _isExpanded, value)) { UpdatePreviewRetention(); RaiseComputed(); } } }
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
    public MediaBrush GlassBrush => Settings.UseCustomColors && !string.IsNullOrWhiteSpace(Settings.GlassColorHex)
        ? FrozenBrush(Settings.GlassColorHex)
        : (IsDarkTheme ? FrozenBrush("#FF313539") : FrozenBrush("#FFEAECEF"));
    public System.Windows.Media.FontFamily UiFontFamily
    {
        get { try { return new System.Windows.Media.FontFamily(Settings.FontFamilyName); } catch { return new System.Windows.Media.FontFamily("Segoe UI Variable Text"); } }
    }
    public MediaBrush PanelBrush => IsDarkTheme ? FrozenBrush("#16FFFFFF") : FrozenBrush("#B8FFFFFF");
    public MediaBrush PanelBorderBrush => IsDarkTheme ? FrozenBrush("#28FFFFFF") : FrozenBrush("#280C213B");
    public ImageSource? Artwork => _artwork;
    public bool HasArtwork => _artwork is not null;
    public bool IsLiquidGlass => Settings.LiquidGlass;
    public double GlassBackgroundOpacity => Settings.LiquidGlass ? Math.Clamp(Settings.GlassOpacity / 100.0, 0.2, 1.0) : 1.0;
    public bool ShowCompactArt => Settings.ShowAlbumArtInCompact && HasArtwork && PrimaryActivity == IslandActivity.Media;
    public double AlbumScale => Math.Clamp(Settings.AlbumArtScale, 70, 130) / 100.0;
    public double ExpandedAlbumScale => AlbumScale * Math.Clamp(Settings.ExpandedAlbumArtSize, 40, 160) / 100.0;
    // Album / icon corner radius: 0 = square, ~30% = squircle, 50% = circle. The ring geometry is
    // regenerated to match so the progress ring always traces the same shape as the cover.
    public double AlbumRadiusFraction => Math.Clamp(Settings.AlbumCornerRadius, 0, 50) / 100.0;
    public double CompactAlbumRadius => 28 * AlbumRadiusFraction;
    public double ExpandedAlbumRadius => 76 * AlbumRadiusFraction; // expanded album art is 76px in the redesign
    public CornerRadius CompactIconCorner => new(CompactAlbumRadius);
    public CornerRadius ExpandedIconCorner => new(ExpandedAlbumRadius);
    public Geometry CompactRingGeometry => RoundedSquare(32, 32 * AlbumRadiusFraction);
    public Geometry ExpandedRingGeometry => RoundedSquare(66, 66 * AlbumRadiusFraction);
    public double CompactRingPerimeterUnits => RoundedSquarePerimeter(32, 32 * AlbumRadiusFraction) / 2.5;
    public double ExpandedRingPerimeterUnits => RoundedSquarePerimeter(66, 66 * AlbumRadiusFraction) / 3.0;
    private bool HasMediaDuration => Media.HasSession && Media.Duration.TotalSeconds > 0;
    public bool ShowCompactMediaRing => ShowCompactArt && Settings.ShowMediaProgressRing && HasMediaDuration;
    public bool ShowCompactTimerRing => PrimaryActivity == IslandActivity.Timer && Settings.ShowTimerRing;
    public bool ShowCompactRingTrack => ShowCompactMediaRing || ShowCompactTimerRing;
    public bool ShowExpandedMediaRing => Settings.ShowMedia && HasArtwork && Settings.ShowMediaProgressRing && HasMediaDuration;
    public bool ShowMedia => Settings.ShowMedia;
    public bool ScrollTitles => Settings.ScrollLongTitles;
    public bool ShowVolume => Settings.ShowVolume && Audio.Availability == AudioAvailability.Available;
    public bool ShowBattery => Settings.ShowBattery && Battery.IsAvailable;
    public bool IsCharging => ShowBattery && Battery.IsCharging;
    public bool ShowBatteryLevel => ShowBattery && !Battery.IsCharging;
    public bool ShowClock => Settings.ShowClock;
    public bool ShowTimerAlarm => Settings.ShowTimerAlarm;
    public bool DebugOverlay => Settings.DebugOverlay;
    public bool IsReducedMotion => Settings.AnimationIntensity == AnimationIntensity.Reduced;
    public bool IsPlaying => Media.IsPlaying;
    public bool IsMuted => Audio.SystemMuted;
    public bool IsAudioActive => Audio.Availability == AudioAvailability.Available && Audio.ActiveAudioOutput && !Audio.SystemMuted;
    public bool ShowAudioStatusText => !IsAudioActive;
    public bool ShowDate => Settings.ShowDate;
    public VisionState Vision { get => _vision; private set => SetProperty(ref _vision, value); }
    // Secondary status dot (same idiom as the audio dot) — deliberately not part of PrimaryActivity.
    public bool ShowVisionStatus => Settings.ShowVisionStatus && Settings.VisionEnabled
        && Vision.Availability == VisionAvailability.Running;
    public string VisionStatusText => Vision.StatusText;
    public MediaBrush VisionDotBrush => FrozenBrush(Vision.ColorHex);
    public bool VisionAlert => ShowVisionStatus && Vision.Alert;
    public bool ShowVisionButton => Settings.VisionEnabled;

    // ===== Per-element font sizes (each = default × element% × interface%) =====
    private double Scaled(int elementPercent, double baseSize) =>
        baseSize * Math.Clamp(elementPercent, 60, 160) / 100.0 * Math.Clamp(Settings.InterfaceScale, 70, 150) / 100.0;
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
    public double VisionTextFontSize => Scaled(Settings.VisionTextSize, 10);
    public double CompactGlyphFontSize => Scaled(Settings.CompactTextSize, 12);
    public double CompactPrimaryFontSize => Scaled(Settings.CompactTextSize, 13);
    public double CompactSecondaryFontSize => Scaled(Settings.CompactTextSize, 11);

    // ===== Weather =====
    public bool ShowWeather => Settings.ShowWeather && _weather is not null;
    public string WeatherGlyph => _weather?.Glyph ?? string.Empty;
    public string WeatherTempText => _weather?.TempText ?? string.Empty;
    public string WeatherDescText => _weather?.Description ?? string.Empty;

    // ===== System monitor =====
    public bool ShowSystemMonitor => Settings.ShowSystemMonitor;
    public bool ShowCompactRam => Settings.ShowRamInCompact;
    public string CpuText => $"{_sysStats.CpuPercent}%";
    public string RamText => $"{_sysStats.RamPercent}%";
    public string NetText => _sysStats.NetworkText;
    // Numeric RAM load (0–100) for the mini-card progress bar in the redesigned status panel.
    public double RamPercentValue => Math.Clamp(_sysStats.RamPercent, 0, 100);
    // Rolling network-throughput sparkline (last N samples, normalised to a 46×16 box).
    private const int NetHistoryLength = 18;
    private readonly Queue<double> _netHistory = new();
    public PointCollection NetSparkline
    {
        get
        {
            const double w = 46, h = 16;
            var pts = new PointCollection();
            if (_netHistory.Count == 0) { pts.Add(new System.Windows.Point(0, h)); pts.Add(new System.Windows.Point(w, h)); return pts; }
            var samples = _netHistory.ToArray();
            var max = Math.Max(1.0, samples.Max());
            var step = samples.Length > 1 ? w / (samples.Length - 1) : w;
            for (int i = 0; i < samples.Length; i++)
            {
                var x = i * step;
                var y = h - Math.Clamp(samples[i] / max, 0, 1) * (h - 1) - 0.5;
                pts.Add(new System.Windows.Point(x, y));
            }
            return pts;
        }
    }

    // ===== Countdown =====
    public bool ShowCountdown => Settings.ShowCountdown && !string.IsNullOrWhiteSpace(Settings.CountdownDate);
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

    // ===== World clocks =====
    public bool ShowWorldClocks => Settings.ShowWorldClocks && WorldClocks.Count > 0;
    public IReadOnlyList<WorldClock> WorldClocks
    {
        get
        {
            var list = new List<WorldClock>();
            foreach (var id in (Settings.WorldClockZones ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    var tz = TimeZoneInfo.FindSystemTimeZoneById(id);
                    var t = TimeZoneInfo.ConvertTime(_now, tz);
                    var fmt = Settings.Use24HourClock ? "HH:mm" : "h:mm tt";
                    list.Add(new WorldClock(ShortZone(id), t.ToString(fmt)));
                }
                catch { }
            }
            return list;
        }
    }
    private static string ShortZone(string id)
    {
        var t = id.Replace(" Standard Time", "").Replace(" Daylight Time", "");
        var slash = t.LastIndexOf('/');
        return (slash >= 0 ? t[(slash + 1)..] : t).Replace('_', ' ');
    }

    // ===== Stocks =====
    public bool ShowStocks => Settings.ShowStocks && _stocks.Count > 0;
    public IReadOnlyList<StockQuote> Stocks => _stocks;

    // ===== Next meeting =====
    public bool ShowNextMeeting => Settings.ShowNextMeeting && _meeting is not null;
    public string MeetingTitle => _meeting?.Title ?? string.Empty;
    public string MeetingWhen => _meeting?.CountdownText ?? string.Empty;
    public bool HasMeetingJoin => !string.IsNullOrWhiteSpace(_meeting?.JoinUrl);

    // ===== Battery time =====
    public bool ShowBatteryTime => Settings.ShowBatteryTime && Battery.IsAvailable && !Battery.IsCharging && Battery.MinutesRemaining > 0;
    public string BatteryTimeText => Battery.TimeRemainingText;

    // ===== Quick launch =====
    public bool ShowQuickLaunch => Settings.ShowQuickLaunch && LaunchItems.Count > 0;
    public IReadOnlyList<LaunchEntry> LaunchItems
    {
        get
        {
            var list = new List<LaunchEntry>();
            foreach (var line in (Settings.QuickLaunchItems ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('|', 2);
                var name = parts[0].Trim();
                var path = parts.Length > 1 ? parts[1].Trim() : parts[0].Trim();
                if (path.Length > 0) list.Add(new LaunchEntry(name, path, string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant()));
            }
            return list;
        }
    }

    public bool ShowClipboard => Settings.ShowClipboard;
    // True when any of the grouped live-activity widgets is enabled (so the panel can be shown/hidden cleanly).
    public bool ShowWidgetsPanel => ShowWeather || ShowStocks || ShowCountdown || ShowNextMeeting
        || ShowWorldClocks || ShowSystemMonitor || ShowBatteryTime;
    // Secondary widgets surfaced under the status panel in the redesigned expanded island (weather and the
    // system monitor get their own cards, so they're excluded here). Collapses the strip when nothing's on.
    public bool ShowStatusExtras => ShowBatteryLevel || IsCharging || ShowVisionStatus || ShowStocks
        || ShowCountdown || ShowNextMeeting || ShowWorldClocks || ShowBatteryTime;

    // ===== Outer island corner radius (user-chosen via the slider, 0–48 DIP) =====
    private double ClampedCornerRadius => Math.Clamp(Settings.IslandCornerRadius, 0, 48);
    public CornerRadius IslandCornerRadius => new(ClampedCornerRadius);
    public CornerRadius IslandInnerCornerRadius => new(Math.Max(0, ClampedCornerRadius - 1));

    // ===== Notification banner (transient) =====
    public bool ShowNotification => _notification is not null && Settings.ShowNotifications;
    public string NotificationApp => _notification?.App ?? string.Empty;
    public string NotificationTitle => _notification?.Title ?? string.Empty;
    public string NotificationBody => _notification?.Body ?? string.Empty;
    // Increments once per genuinely new notification. The view animates off this (not ShowNotification,
    // which routine refreshes re-raise) so the entrance plays exactly once instead of restarting per tick.
    public int NotificationSeq => _notificationSeq;

    // ===== Audio spectrum (real, from loopback) =====
    public bool UseRealSpectrum => Settings.RealAudioSpectrum && _spectrumService.IsActive && IsAudioActive;
    public bool ShowAnimatedWave => IsAudioActive && !UseRealSpectrum;
    public double SpectrumBand0 => Band(0);
    public double SpectrumBand1 => Band(1);
    public double SpectrumBand2 => Band(2);
    public double SpectrumBand3 => Band(3);
    public double SpectrumBand4 => Band(4);
    public double SpectrumBand5 => Band(5);
    public double SpectrumBand6 => Band(6);
    private double Band(int i) => i < _spectrum.Length ? Math.Clamp(_spectrum[i], 0.08, 1.0) : 0.1;

    // ===== Expanded module order (album is fixed at column 0; these occupy 1..3) =====
    private string[] Order => (Settings.ExpandedOrder ?? "media,volume,status")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public int MediaColumn => ColumnOf("media", 1);
    public int VolumeColumn => ColumnOf("volume", 2);
    public int StatusColumn => ColumnOf("status", 3);
    private int ColumnOf(string key, int fallback)
    {
        var i = Array.IndexOf(Order, key);
        return i < 0 ? fallback : i + 1;
    }

    // ===== Behaviour =====
    public bool PinExpanded => KeepExpanded || Settings.AlwaysExpanded;
    // Live webcam preview shown in the expanded island when the camera is on.
    public ImageSource? CameraPreview => _cameraPreview;
    public bool ShowCameraPreview => Settings.VisionEnabled
        && Vision.Availability == VisionAvailability.Running && _cameraPreview is not null;
    public string MediaTitle => Media.HasSession ? (Media.DisplayTitle ?? "Media") : "No media playing";
    public string MediaArtist => Media.HasSession
        ? string.Join("  |  ", new[] { Media.Artist, Media.SourceAppName }.Where(x => !string.IsNullOrWhiteSpace(x)))
        : "Start playback in any supported app";
    public string PlayPauseGlyph => Media.IsPlaying ? "\uE769" : "\uE768";
    public double MediaProgress => Media.Duration.TotalSeconds <= 0 ? 0
        : Math.Clamp(Media.Position.TotalSeconds / Media.Duration.TotalSeconds * 100, 0, 100);
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
    // 10-second rewind / fast-forward controls. Enabled only when the active session reports it can
    // change position AND exposes a duration; otherwise the buttons disable and explain why via tooltip.
    private const int SeekStepSeconds = 10;
    public bool CanSeek => Media.CanSeek && Media.Duration.TotalSeconds > 0;
    public string SeekBackTooltip => CanSeek ? "Back 10 seconds" : "This source doesn't support seeking";
    public string SeekForwardTooltip => CanSeek ? "Forward 10 seconds" : "This source doesn't support seeking";
    public string VolumeText => $"{Audio.MasterVolumePercent}%";
    public string VolumeGlyph => Audio.SystemMuted ? "\uE74F" : Audio.MasterVolumePercent == 0 ? "\uE992" : "\uE767";
    public string AudioStatusText => Audio.StatusText;
    public string OutputDeviceText => string.IsNullOrWhiteSpace(Audio.OutputDeviceName) ? "System default" : Audio.OutputDeviceName;
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
    public string ChargingText => Battery.IsCharging ? $"{Battery.Percentage}%" : string.Empty;
    public string BatteryGlyph => Battery.IsCharging ? "\uE83E" : "\uE850";
    public string TimerText => TimerAlarmService.FormatDuration(_timerAlarmService.TimerRemaining);
    public double TimerProgress => _timerAlarmService.TimerProgress * 100;
    public string AlarmText => _timerAlarmService.State.Alarm.Phase switch
    {
        AlarmPhase.Ringing => "Alarm ringing",
        AlarmPhase.Snoozed => $"Snoozed until {_timerAlarmService.State.Alarm.SnoozeUntil:t}",
        AlarmPhase.Scheduled => $"Alarm {TimerAlarmService.FormatAlarmTime(_timerAlarmService.State.Alarm)}",
        _ => "No alarm"
    };

    public string CompactGlyph => PrimaryActivity switch
    {
        IslandActivity.Alarm => "\uEA8F",
        IslandActivity.Timer => "\uE916",
        IslandActivity.Muted => "\uE74F",
        IslandActivity.Audio => "\uE995",
        IslandActivity.Charging => "\uE83E",
        IslandActivity.Media => Media.IsPlaying ? "\uE768" : "\uE769",
        _ => "\uE121"
    };

    public string CompactPrimaryText => PrimaryActivity switch
    {
        IslandActivity.Alarm => _timerAlarmService.State.Alarm.Phase == AlarmPhase.Ringing
            ? "Alarm" : TimerAlarmService.FormatAlarmTime(_timerAlarmService.State.Alarm),
        IslandActivity.Timer => _timerAlarmService.State.Timer.Phase == TimerPhase.Completed
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
            if (PrimaryActivity != IslandActivity.None && Settings.ShowClock) values.Add(ClockText);
            if (Settings.ShowDate) values.Add(_now.ToString("MMM d"));
            if (ShowBattery && PrimaryActivity != IslandActivity.Charging) values.Add($"{Battery.Percentage}%");
            return string.Join("  |  ", values);
        }
    }

    public IslandActivity PrimaryActivity
    {
        get
        {
            var timer = _timerAlarmService.State.Timer;
            var alarm = _timerAlarmService.State.Alarm;
            if (alarm.Phase is AlarmPhase.Ringing or AlarmPhase.Snoozed or AlarmPhase.Scheduled) return IslandActivity.Alarm;
            if (timer.Phase is TimerPhase.Running or TimerPhase.Paused ||
                timer.Phase == TimerPhase.Completed && !timer.CompletionAcknowledged) return IslandActivity.Timer;
            if (Audio.SystemMuted) return IslandActivity.Muted;
            if (Settings.ShowMedia && Media.HasSession) return IslandActivity.Media;
            if (Audio.ActiveAudioOutput) return IslandActivity.Audio;
            if (Battery.IsCharging) return IslandActivity.Charging;
            return IslandActivity.None;
        }
    }

    public ICommand PreviousCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand SeekBackCommand { get; }
    public ICommand SeekForwardCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand AdjustVolumeCommand { get; }
    public ICommand ToggleExpandedCommand { get; }
    public ICommand SeekCommand { get; private set; } = null!;
    public ICommand OpenMediaAppCommand { get; private set; } = null!;
    public ICommand LaunchCommand { get; private set; } = null!;
    public ICommand OpenMeetingCommand { get; private set; } = null!;
    public ICommand ToggleFavoriteCommand { get; private set; } = null!;

    public void ApplySettings()
    {
        _mediaService.SetPreferredApp(Settings.SelectedMediaApp);
        IsDarkTheme = _themeService.IsDark(Settings.Theme);
        if (Settings.AlwaysExpanded) IsExpanded = true;
        UpdatePreviewRetention();
        RaiseComputed();
        RaisePropertyChanged(nameof(PinExpanded));
        RaisePropertyChanged(nameof(UiFontFamily));
    }

    private void OnMediaChanged(object? sender, MediaInfo value) => OnUi(() => { Media = value; RaiseComputed(); });
    private void OnAudioChanged(object? sender, AudioState value) => OnUi(() => { Audio = value; RaiseComputed(); });
    private void OnVisionChanged(object? sender, VisionState value) => OnUi(() =>
    {
        Vision = value;
        if (value.Availability != VisionAvailability.Running) ClearPreview();
        UpdatePreviewRetention();
        RaiseComputed();
    });

    private void OnVisionFrame(object? sender, byte[] jpeg) => OnUi(() =>
    {
        if (!_isExpanded || !Settings.VisionEnabled) return;
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
        RaisePropertyChanged(nameof(ShowCameraPreview));
    });

    private void OnWeatherChanged(object? sender, WeatherInfo? value) => OnUi(() =>
    {
        _weather = value;
        RaiseMany(nameof(ShowWeather), nameof(WeatherGlyph), nameof(WeatherTempText), nameof(WeatherDescText));
    });
    private void OnSysStatsChanged(object? sender, SystemStats value) => OnUi(() =>
    {
        _sysStats = value;
        _netHistory.Enqueue(value.NetBytesPerSec);
        while (_netHistory.Count > NetHistoryLength) _netHistory.Dequeue();
        RaiseMany(nameof(ShowSystemMonitor), nameof(ShowCompactRam), nameof(CpuText), nameof(RamText), nameof(NetText),
            nameof(RamPercentValue), nameof(NetSparkline));
    });
    private void OnStocksChanged(object? sender, IReadOnlyList<StockQuote> value) => OnUi(() =>
    {
        _stocks = value;
        RaiseMany(nameof(ShowStocks), nameof(Stocks));
    });
    private void OnMeetingChanged(object? sender, MeetingInfo? value) => OnUi(() =>
    {
        _meeting = value;
        RaiseMany(nameof(ShowNextMeeting), nameof(MeetingTitle), nameof(MeetingWhen), nameof(HasMeetingJoin));
    });
    private void OnNotified(object? sender, NotificationInfo value) => OnUi(() =>
    {
        if (!Settings.ShowNotifications) return;
        _notification = value;
        _notificationSeq++;
        RaiseMany(nameof(ShowNotification), nameof(NotificationApp), nameof(NotificationTitle), nameof(NotificationBody), nameof(NotificationSeq));
        _notificationTimer.Stop();
        _notificationTimer.Start();
    });

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
            nameof(UseRealSpectrum), nameof(ShowAnimatedWave)
        }) RaisePropertyChanged(name);
    });

    private static string WithAlpha(string hex, byte alpha)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 8) h = h[2..];
        return h.Length == 6 ? $"#{alpha:X2}{h}" : hex;
    }

    // Ask the service for preview frames only while the expanded island can actually show them.
    private void UpdatePreviewRetention()
    {
        var want = _isExpanded && Settings.VisionEnabled && Vision.Availability == VisionAvailability.Running;
        if (want == _previewRetained) return;
        _previewRetained = want;
        if (want) _visionService.RetainPreview(); else { _visionService.ReleasePreview(); ClearPreview(); }
    }

    private void ClearPreview()
    {
        if (_cameraPreview is null) return;
        _cameraPreview = null;
        RaisePropertyChanged(nameof(CameraPreview));
        RaisePropertyChanged(nameof(ShowCameraPreview));
    }
    private void OnBatteryChanged(object? sender, BatteryState value) => OnUi(() => { Battery = value; RaiseComputed(); });
    // Only the time strings change each second — re-raising everything (brushes/geometries) every tick
    // was needless CPU churn (which the system monitor then read back as inflated usage).
    private void OnClockTick(object? sender, DateTimeOffset value) => OnUi(() =>
    {
        _now = value;
        RaiseMany(nameof(ClockText), nameof(ClockTimeText), nameof(ClockAmPm), nameof(DateText), nameof(DateLongText),
            nameof(CompactPrimaryText), nameof(CompactSecondaryText),
            nameof(CountdownText), nameof(WorldClocks), nameof(MeetingWhen));
    });
    private void RaiseMany(params string[] names) { foreach (var n in names) RaisePropertyChanged(n); }
    private void OnTimerAlarmChanged(object? sender, EventArgs e) => OnUi(RaiseComputed);
    private void OnSystemThemeChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        IsDarkTheme = _themeService.IsDark(Settings.Theme);
        RaiseComputed();
    });

    private void RaiseComputed()
    {
        foreach (var property in new[]
        {
            nameof(IsCompact), nameof(ShowMedia), nameof(ShowVolume), nameof(ShowBattery), nameof(IsCharging),
            nameof(ShowBatteryLevel), nameof(ShowClock),
            nameof(ShowTimerAlarm), nameof(DebugOverlay), nameof(IsReducedMotion), nameof(IsPlaying), nameof(IsMuted),
            nameof(IsAudioActive), nameof(ShowAudioStatusText), nameof(ShowDate),
            nameof(Vision), nameof(ShowVisionStatus), nameof(VisionStatusText), nameof(VisionDotBrush), nameof(VisionAlert),
            nameof(CameraPreview), nameof(ShowCameraPreview), nameof(ShowVisionButton),
            nameof(ClockFontSize), nameof(DateFontSize), nameof(BatteryGlyphFontSize), nameof(BatteryTextFontSize),
            nameof(ChargingGlyphFontSize), nameof(ChargingTextFontSize), nameof(CompactChargingTextFontSize),
            nameof(MediaTitleFontSize), nameof(MediaArtistFontSize), nameof(VolumeFontSize), nameof(VisionTextFontSize),
            nameof(CompactGlyphFontSize), nameof(CompactPrimaryFontSize), nameof(CompactSecondaryFontSize),
            nameof(MediaTitle), nameof(MediaArtist),
            nameof(PlayPauseGlyph), nameof(MediaProgress), nameof(ShowMediaTimes), nameof(MediaElapsedText), nameof(MediaTotalText),
            nameof(MediaTimeRemaining), nameof(MediaTrailingTimeText), nameof(ShowNowPlaying), nameof(ShowExplicitBadge), nameof(FavoriteGlyph),
            nameof(CanSeek), nameof(SeekBackTooltip), nameof(SeekForwardTooltip),
            nameof(VolumeText), nameof(VolumeGlyph), nameof(OutputDeviceText), nameof(AudioStatusText),
            nameof(ClockText), nameof(ClockTimeText), nameof(ClockAmPm), nameof(DateText), nameof(DateLongText),
            nameof(BatteryText), nameof(ChargingText), nameof(BatteryGlyph), nameof(TimerText),
            nameof(TimerProgress), nameof(AlarmText), nameof(CompactGlyph), nameof(CompactPrimaryText),
            nameof(CompactSecondaryText), nameof(PrimaryActivity), nameof(Artwork), nameof(HasArtwork),
            nameof(IsLiquidGlass), nameof(GlassBackgroundOpacity), nameof(ShowCompactArt),
            nameof(ShowCompactMediaRing), nameof(ShowCompactTimerRing), nameof(ShowCompactRingTrack), nameof(ShowExpandedMediaRing), nameof(ScrollTitles),
            nameof(AlbumScale), nameof(ExpandedAlbumScale), nameof(CompactAlbumRadius), nameof(ExpandedAlbumRadius), nameof(CompactIconCorner), nameof(ExpandedIconCorner),
            nameof(CompactRingGeometry), nameof(ExpandedRingGeometry), nameof(CompactRingPerimeterUnits), nameof(ExpandedRingPerimeterUnits),
            nameof(PrimaryTextBrush), nameof(SecondaryTextBrush), nameof(AccentTextBrush), nameof(PanelBrush),
            nameof(PanelBorderBrush), nameof(AccentBrush), nameof(AccentSoftBrush), nameof(GlassBrush), nameof(UiFontFamily),
            nameof(ShowWeather), nameof(WeatherGlyph), nameof(WeatherTempText), nameof(WeatherDescText),
            nameof(ShowSystemMonitor), nameof(ShowCompactRam), nameof(CpuText), nameof(RamText), nameof(NetText),
            nameof(RamPercentValue), nameof(NetSparkline),
            nameof(ShowCountdown), nameof(CountdownText), nameof(ShowWorldClocks), nameof(WorldClocks),
            nameof(ShowStocks), nameof(Stocks), nameof(ShowNextMeeting), nameof(MeetingTitle), nameof(MeetingWhen), nameof(HasMeetingJoin),
            nameof(ShowBatteryTime), nameof(BatteryTimeText), nameof(ShowQuickLaunch), nameof(LaunchItems), nameof(ShowNotification), nameof(ShowClipboard), nameof(ShowWidgetsPanel), nameof(ShowStatusExtras),
            nameof(IslandCornerRadius), nameof(IslandInnerCornerRadius),
            nameof(UseRealSpectrum), nameof(ShowAnimatedWave), nameof(PinExpanded),
            nameof(MediaColumn), nameof(VolumeColumn), nameof(StatusColumn)
        }) RaisePropertyChanged(property);
        (PreviousCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PlayPauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NextCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SeekBackCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SeekForwardCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleMuteCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static MediaBrush FrozenBrush(string value)
    {
        var brush = (MediaBrush)new System.Windows.Media.BrushConverter().ConvertFromString(value)!;
        brush.Freeze();
        return brush;
    }

    public void RefreshLaunchAndZones() => RaiseMany(nameof(ShowQuickLaunch), nameof(LaunchItems), nameof(ShowWorldClocks), nameof(WorldClocks));

    private static double RoundedSquarePerimeter(double size, double radius)
    {
        radius = Math.Clamp(radius, 0, size / 2);
        return 4 * (size - 2 * radius) + 2 * Math.PI * radius;
    }

    // Builds a rounded-square outline starting at top-centre (so the progress dash begins at the top).
    private static Geometry RoundedSquare(double s, double r)
    {
        r = Math.Clamp(r, 0.0001, s / 2);
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
        _adaptiveAccent = Settings.AdaptiveAccent ? Infrastructure.ImageColor.Dominant(bytes) : null;
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
    }

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        if (dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        _mediaService.Changed -= OnMediaChanged;
        _audioService.Changed -= OnAudioChanged;
        _visionService.Changed -= OnVisionChanged;
        _visionService.FrameReady -= OnVisionFrame;
        _weatherService.Changed -= OnWeatherChanged;
        _systemMonitorService.Changed -= OnSysStatsChanged;
        _spectrumService.BandsChanged -= OnSpectrumChanged;
        _stocksService.Changed -= OnStocksChanged;
        _calendarService.Changed -= OnMeetingChanged;
        _notificationService.Notified -= OnNotified;
        _notificationTimer.Stop();
        if (_previewRetained) { _visionService.ReleasePreview(); _previewRetained = false; }
        _batteryService.Changed -= OnBatteryChanged;
        _clockService.Tick -= OnClockTick;
        _timerAlarmService.Changed -= OnTimerAlarmChanged;
        _themeService.SystemThemeChanged -= OnSystemThemeChanged;
    }
}

public sealed record WorldClock(string Label, string Time);
public sealed record LaunchEntry(string Name, string Path, string Glyph);
