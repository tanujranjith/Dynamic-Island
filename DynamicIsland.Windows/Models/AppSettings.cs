namespace DynamicIsland.Windows.Models;

public enum ThemeMode { System, Light, Dark, Custom }
public enum NotificationFilter { All, Allowlist, Blocklist }
public enum IslandSize { Compact, Normal, Large }
public enum IslandVisualMode { Apple, Stats }
public enum AnimationIntensity { Reduced, Normal, Expressive }
public enum PositionMode { TopCenter, TopLeft, TopRight, Manual, MiddleLeft, Center, MiddleRight, BottomLeft, BottomCenter, BottomRight }
public enum QuotePlacement { Off, Compact, Expanded, Both }
public enum QuoteRotation { Static, EveryExpand, EveryMinute, Every5Minutes, Every15Minutes, Every30Minutes, EveryHour }
public enum QCaptureMode { ActiveWindow, ActiveMonitor }

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 11;
    public bool LaunchOnStartup { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public bool LockPosition { get; set; }
    public bool ClickThroughWhenCompact { get; set; }
    public bool ExpandOnHover { get; set; } = true;
    public bool ShowMedia { get; set; } = true;
    public bool ShowAlbumArtInCompact { get; set; } = true;
    public int AlbumArtScale { get; set; } = 100; // % size of the album art / icon (70–130)
    public int ExpandedAlbumArtSize { get; set; } = 100; // % size of the album art in the expanded view (40–160)
    public int AlbumCornerRadius { get; set; } = 24; // 0 = square; 24 is the intended Apple-like squircle limit
    public bool ShowMediaProgressRing { get; set; } = true;
    public bool ShowSongTimeRemaining { get; set; } = true;
    public bool ScrollLongTitles { get; set; } = true; // marquee long media titles that don't fit
    public bool ShowTimerRing { get; set; } = true;
    public bool ShowVolume { get; set; } = true;
    public bool ShowBattery { get; set; } = true;
    public bool ShowClock { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool ShowTimerAlarm { get; set; } = true;
    public bool FocusModeEnabled { get; set; }
    public IslandActivity PinnedActivity { get; set; } = IslandActivity.None;
    public Guid? PinnedTimerId { get; set; }
    public bool NotificationHistoryEnabled { get; set; } = true;
    public bool Use24HourClock { get; set; }
    public bool ShowSeconds { get; set; }
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string CustomThemeColorHex { get; set; } = "#241C3C";
    public IslandSize IslandSize { get; set; } = IslandSize.Normal;
    public IslandVisualMode IslandVisualMode { get; set; } = IslandVisualMode.Apple;
    public int IslandCornerRadius { get; set; } = 22; // outer island corner radius in DIP (0–48)
    // Direct compact-island dimensions in DIPs. These supplement the simple size presets.
    public int IslandWidth { get; set; } = 230;
    public int IslandHeight { get; set; } = 62;
    public AnimationIntensity AnimationIntensity { get; set; } = AnimationIntensity.Normal;
    public PositionMode DefaultPosition { get; set; } = PositionMode.TopCenter;
    public string SelectedMediaApp { get; set; } = "Automatic";
    public bool DebugOverlay { get; set; }
    public bool DebugLogging { get; set; }
    // Exclude the Island from supported Windows screenshot and capture APIs by default.
    public bool ShowIslandInScreenshots { get; set; }
    public bool ShowInAltTab { get; set; }
    public int CollapseDelayMilliseconds { get; set; } = 400;
    public int TopOffset { get; set; } = 2; // vertical offset from the selected anchor (DIP)
    public int SideOffset { get; set; } // horizontal offset from the selected anchor (DIP)
    public double? ManualLeftPixels { get; set; }
    public double? ManualTopPixels { get; set; }
    public string? ManualMonitorDeviceName { get; set; }

    // Per-element size customisation. Each is a percentage of the element's default size; InterfaceScale
    // multiplies everything on top. (60–160 per element, 70–150 master — clamped in SettingsService.)
    public int InterfaceScale { get; set; } = 100;
    public int ClockSize { get; set; } = 100;
    public int DateSize { get; set; } = 100;
    public int BatterySize { get; set; } = 100;
    public int MediaTitleSize { get; set; } = 100;
    public int MediaArtistSize { get; set; } = 100;
    public int VolumeSize { get; set; } = 100;
    public int CompactTextSize { get; set; } = 100;

    // ===== Colours & font =====
    public bool UseCustomColors { get; set; }
    public string AccentColorHex { get; set; } = "#5AA7FF";
    public string TextColorHex { get; set; } = "";   // empty = follow theme
    public bool AdaptiveAccent { get; set; } = true; // pull accent from album art
    public string FontFamilyName { get; set; } = "Segoe UI Variable Text";

    // ===== Behaviour =====
    public bool AlwaysExpanded { get; set; }
    public bool IdleDimming { get; set; }
    public int IdleOpacityPercent { get; set; } = 55;
    public bool AutoHideFullscreen { get; set; }
    public bool AutoGrowPill { get; set; } = true;   // pill resizes to fit content (no clipping)

    // ===== Position / monitor =====
    public string PreferredMonitor { get; set; } = ""; // empty = primary; or a device name
    public bool FollowActiveScreen { get; set; }       // jump to the monitor with the foreground window

    // ===== Media =====
    public bool ClickArtOpensApp { get; set; } = true;

    // ===== Live activities =====
    public bool ShowWeather { get; set; }
    public string WeatherLocation { get; set; } = "";
    public bool WeatherFahrenheit { get; set; }
    public bool ShowSystemMonitor { get; set; }
    public bool ShowRamInCompact { get; set; } // show RAM usage on the collapsed (compact) island
    public bool RealAudioSpectrum { get; set; }
    public bool ShowMusicVisualizer { get; set; } = true;
    public bool ShowConnectivity { get; set; } = true;

    // ===== Layout order (csv of: media,volume,status) =====
    public string ExpandedOrder { get; set; } = "media,volume,status";

    // ===== First run =====
    public bool HasOnboarded { get; set; }

    // ===== Quick launcher (newline-separated "Name|Path" entries) =====
    public bool ShowQuickLaunch { get; set; }
    public string QuickLaunchItems { get; set; } = "";

    // ===== Countdown =====
    public bool ShowCountdown { get; set; }
    public string CountdownLabel { get; set; } = "";
    public string CountdownDate { get; set; } = ""; // yyyy-MM-dd

    // ===== Quotes =====
    public QuotePlacement QuotePlacement { get; set; } = QuotePlacement.Off;
    public QuoteRotation QuoteRotation { get; set; } = QuoteRotation.Static;
    public string QuotesText { get; set; } = ""; // one quote per line, optional "| author" suffix
    public int QuoteSize { get; set; } = 100;

    // ===== Stocks / crypto (csv of symbols, e.g. AAPL,MSFT,BTC-USD) =====
    public bool ShowStocks { get; set; }
    public string StockSymbols { get; set; } = "";

    // ===== World clocks (csv of IANA/Windows time-zone ids) =====
    public bool ShowWorldClocks { get; set; }
    public string WorldClockZones { get; set; } = "";

    // ===== Clipboard / notifications / calendar (Windows APIs; may be limited unpackaged) =====
    public bool ShowClipboard { get; set; }
    public bool ShowNotifications { get; set; }
    public bool ShowNextMeeting { get; set; }
    // Notification mirroring filter: All shows every toast; Allowlist shows only listed apps; Blocklist hides them.
    public NotificationFilter NotificationFilterMode { get; set; } = NotificationFilter.All;
    public string NotificationAppFilter { get; set; } = ""; // comma-separated app display names (substring match)

    // ===== Privacy sensors (mic/camera in-use indicator) =====
    public bool ShowPrivacyIndicators { get; set; } = true;

    // ===== Q visual assistant =====
    public bool QEnabled { get; set; } = true;
    public bool QAutoExpandIsland { get; set; } = true;
    public bool QAutoCloseAfterResponse { get; set; }
    public int QAutoCloseDelaySeconds { get; set; } = 10;
    public string QSelectedProvider { get; set; } = "openai";
    public string QSelectedModel { get; set; } = "gpt-4o-mini";
    public Dictionary<string, QProviderPreference> QProviderPreferences { get; set; } = new();
    public bool QCompareEnabled { get; set; }
    public string QCompareProvider { get; set; } = "gemini";
    public string QCompareModel { get; set; } = "";
    public string QCompareReasoningEffort { get; set; } = "auto";
    public QCaptureMode QCaptureMode { get; set; } = QCaptureMode.ActiveWindow;
    public bool QIncludeScreenImage { get; set; } = true;
    public string QOllamaBaseUrl { get; set; } = "http://localhost:11434";
    public int QTimeoutSeconds { get; set; } = 90;
    public int QMaxResponseTokens { get; set; } = 8192;
    public string QReasoningEffort { get; set; } = "auto";
    public string QAskSystemPrompt { get; set; } = "";
    public string QSaySystemPrompt { get; set; } = "";
    // Global shortcut used to open Q. Ctrl+Alt+Q remains the compatibility default.
    public string QActivationHotkey { get; set; } = "Ctrl+Alt+Q";
    // Null migrates the former single choice; zero intentionally disables every shortcut.
    public QActivationShortcuts? QActivationKeys { get; set; }
    public List<QShortcut> QShortcuts { get; set; } = [];
    // Empty means Ctrl+Alt+Q opens Q without automatically submitting a quick shortcut.
    public string QHotkeyShortcut { get; set; } = "";
    public bool QDisclosureAccepted { get; set; }

    // ===== Battery warnings =====
    public bool ShowBatteryTime { get; set; }
    public bool LowBatteryWarning { get; set; } = true;
    public int LowBatteryThreshold { get; set; } = 15;

    // ===== Volume warning =====
    public bool VolumeWarningEnabled { get; set; } = true;
    public int VolumeWarningThreshold { get; set; } = 60;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Resets every setting on this instance back to its default value (in place, so existing
    /// references held by the view-models keep pointing at the same object).</summary>
    public void ResetToDefaults() => CopyFrom(new AppSettings());

    /// <summary>Copies every read/write property from <paramref name="other"/> into this instance.</summary>
    public void CopyFrom(AppSettings other)
    {
        foreach (var property in typeof(AppSettings).GetProperties())
            if (property is { CanRead: true, CanWrite: true })
                property.SetValue(this, property.GetValue(other));
    }
}
