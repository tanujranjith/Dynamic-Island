namespace DynamicIsland.Windows.Models;

public enum ThemeMode { System, Light, Dark }
public enum IslandSize { Compact, Normal, Large }
public enum AnimationIntensity { Reduced, Normal, Expressive }
public enum PositionMode { TopCenter, TopLeft, Manual }

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool LaunchOnStartup { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public bool LockPosition { get; set; }
    public bool ClickThroughWhenCompact { get; set; }
    public bool ExpandOnHover { get; set; } = true;
    public bool ShowMedia { get; set; } = true;
    public bool ShowAlbumArtInCompact { get; set; }
    public int AlbumArtScale { get; set; } = 100; // % size of the album art / icon (70–130)
    public int ExpandedAlbumArtSize { get; set; } = 100; // % size of the album art in the expanded view (40–160)
    public int AlbumCornerRadius { get; set; } = 30; // corner radius as % of side: 0 = square, 30 = squircle, 50 = circle
    public bool ShowMediaProgressRing { get; set; } = true;
    public bool ShowSongTimeRemaining { get; set; } = true;
    public bool ScrollLongTitles { get; set; } = true; // marquee long media titles that don't fit
    public bool ShowTimerRing { get; set; } = true;
    public bool LiquidGlass { get; set; }
    public int GlassOpacity { get; set; } = 65; // % fill opacity when Liquid glass is on (lower = more transparent)
    public bool ShowVolume { get; set; } = true;
    public bool ShowBattery { get; set; } = true;
    public bool ShowClock { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool ShowTimerAlarm { get; set; } = true;
    public bool Use24HourClock { get; set; }
    public bool ShowSeconds { get; set; }
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public IslandSize IslandSize { get; set; } = IslandSize.Normal;
    public int IslandCornerRadius { get; set; } = 22; // outer island corner radius in DIP (0–48)
    public AnimationIntensity AnimationIntensity { get; set; } = AnimationIntensity.Normal;
    public PositionMode DefaultPosition { get; set; } = PositionMode.TopCenter;
    public string SelectedMediaApp { get; set; } = "Automatic";
    public bool DebugOverlay { get; set; }
    public bool DebugLogging { get; set; }
    public bool ShowInAltTab { get; set; }
    public int CollapseDelayMilliseconds { get; set; } = 400;
    public int TopOffset { get; set; } = 2; // gap (DIP) from the top of the screen to the pill
    // Camera "vision" feature — off by default. Opens the webcam only while enabled.
    public bool VisionEnabled { get; set; }
    public bool VisionPrivacyMode { get; set; } // recognise the enrolled owner ("Just you" vs "Unknown person")
    public bool VisionModelsConsented { get; set; } // user agreed to download the detection models
    public bool ShowVisionStatus { get; set; } = true;
    public int VisionTargetFps { get; set; } = 7;
    public int VisionCameraIndex { get; set; }
    public double VisionFaceMatchThreshold { get; set; } = 0.363; // SFace cosine match threshold
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
    public int VisionTextSize { get; set; } = 100;
    public int CompactTextSize { get; set; } = 100;

    // ===== Colours & font =====
    public bool UseCustomColors { get; set; }
    public string AccentColorHex { get; set; } = "#5AA7FF";
    public string TextColorHex { get; set; } = "";   // empty = follow theme
    public string GlassColorHex { get; set; } = "";  // empty = default grey
    public bool AdaptiveAccent { get; set; }         // pull accent from album art
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

    // ===== Camera automations =====
    public bool AutoLockOnUnknown { get; set; }
    public int AutoLockDelaySeconds { get; set; } = 8;
    public bool PresenceAwareMedia { get; set; }
    public bool PrivacyAutoBlur { get; set; }

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

    // ===== Battery warnings =====
    public bool ShowBatteryTime { get; set; }
    public bool LowBatteryWarning { get; set; } = true;
    public int LowBatteryThreshold { get; set; } = 15;

    // ===== Theme skin (last applied, for reference) =====
    public string ThemeSkin { get; set; } = "";

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
