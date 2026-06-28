using System.Threading;
using System.Windows;
using DynamicIsland.Windows.Models;
using DynamicIsland.Windows.Services;
using DynamicIsland.Windows.Services.Vision;
using DynamicIsland.Windows.ViewModels;
using DynamicIsland.Windows.Views;
using System.Windows.Media;

namespace DynamicIsland.Windows;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private LoggingService? _log;
    private SettingsService? _settingsService;
    private StartupService? _startupService;
    private MediaSessionService? _media;
    private AudioSessionService? _audio;
    private BatteryService? _battery;
    private ClockService? _clock;
    private TimerAlarmService? _timerAlarm;
    private ThemeService? _theme;
    private VisionModelManager? _visionModels;
    private VisionService? _vision;
    private WeatherService? _weather;
    private SystemMonitorService? _sysMon;
    private AudioSpectrumService? _spectrum;
    private StocksService? _stocks;
    private CalendarService? _calendar;
    private NotificationListenerService? _notifications;
    private ClipboardService? _clipboard;
    private WindowPositionService? _position;
    private IslandViewModel? _islandViewModel;
    private SettingsViewModel? _settingsViewModel;
    private IslandWindow? _islandWindow;
    private SettingsWindow? _settingsWindow;
    private VisionSettingsWindow? _visionWindow;
    private TimerAlarmWindow? _timerWindow;
    private TimerAlarmViewModel? _timerViewModel;
    private TrayService? _tray;
    private AppSettings? _settings;
    private bool _isShuttingDown;
    private System.Windows.Threading.DispatcherTimer? _autoLockTimer;
    private bool _autoPausedMedia;
    private Window? _privacyBlur;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, "Local\\DynamicIsland.Windows.SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            Shutdown();
            return;
        }

        _log = new LoggingService();
        _settingsService = new SettingsService(_log);
        _settings = await _settingsService.LoadAsync();
        _log.SetDebugEnabled(_settings.DebugLogging);
        _log.Info("Application starting");

        DispatcherUnhandledException += (_, args) =>
        {
            _log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _log.Error("Unhandled application exception", args.ExceptionObject as Exception);

        _startupService = new StartupService(_log);
        if (_settings.LaunchOnStartup != _startupService.IsEnabled())
            _startupService.SetEnabled(_settings.LaunchOnStartup);

        _media = new MediaSessionService(_log);
        _audio = new AudioSessionService(_log);
        _battery = new BatteryService();
        _clock = new ClockService();
        _timerAlarm = new TimerAlarmService(_log);
        _theme = new ThemeService();
        ApplyGlobalTheme();
        _theme.SystemThemeChanged += (_, _) => Dispatcher.BeginInvoke(ApplyGlobalTheme);
        _visionModels = new VisionModelManager(_log);
        _vision = new VisionService(_log, _visionModels);
        _weather = new WeatherService(_log);
        _sysMon = new SystemMonitorService();
        _spectrum = new AudioSpectrumService(_log);
        _stocks = new StocksService(_log);
        _calendar = new CalendarService(_log);
        _notifications = new NotificationListenerService(_log);
        _clipboard = new ClipboardService(_log);
        _vision.Changed += (_, state) => Dispatcher.BeginInvoke(() => HandleVisionAutomations(state));
        _battery.LowBattery += (_, pct) => Dispatcher.BeginInvoke(() =>
            _tray?.ShowNotification("Battery low", $"{pct}% remaining — plug in soon."));
        _position = new WindowPositionService();
        _islandViewModel = new IslandViewModel(_settings, _media, _audio, _battery, _clock, _timerAlarm, _theme,
            _vision, _weather, _sysMon, _spectrum, _stocks, _calendar, _notifications);
        _islandWindow = new IslandWindow(_islandViewModel, _position, _settingsService);
        _islandWindow.OpenSettingsRequested += (_, _) => ShowSettings();
        _islandWindow.OpenTimerRequested += (_, _) => ShowTimerAlarm();
        _islandWindow.OpenVisionRequested += (_, _) => ShowVisionSettings();
        _islandWindow.OpenClipboardRequested += (_, _) => _ = ShowClipboardAsync();
        _islandWindow.RecenterRequested += (_, _) => Recenter();
        _islandWindow.Closed += (_, _) => { if (!_isShuttingDown) ShutdownApplication(); };

        _settingsViewModel = new SettingsViewModel(_settings, _settingsService, _startupService,
            _vision, _visionModels, ApplySettings, Recenter, () => _settingsWindow?.Hide());
        _settingsViewModel.OpenVisionPage = ShowVisionSettings;
        _media.AvailableAppsChanged += (_, apps) => Dispatcher.BeginInvoke(() =>
            _settingsViewModel.SetAvailableApps(apps));

        _tray = new TrayService(_settings, ShowSettings, Recenter, SaveAndApplyAsync, ShutdownApplication);
        _timerAlarm.EventRaised += (_, args) => Dispatcher.BeginInvoke(() =>
            _tray.ShowNotification(args.Title, args.Message));

        _islandWindow.Show();
        _clock.Start();
        _battery.Start();
        _audio.Start();
        _timerAlarm.Start();
        ApplyVisionSettings();
        _weather.Start();
        _stocks.Start();
        ApplyLiveActivitySettings();
        await _media.StartAsync();

        // First-run onboarding: open the (new) settings so people can explore what's customisable.
        if (!_settings.HasOnboarded)
        {
            _settings.HasOnboarded = true;
            await _settingsService.SaveAsync(_settings);
            ShowSettings();
        }
    }

    private void ShowSettings()
    {
        if (_settingsViewModel is null) return;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow { DataContext = _settingsViewModel };
            _settingsWindow.IsVisibleChanged += (_, _) => UpdateKeepExpanded();
            _settingsWindow.Closing += (_, args) =>
            {
                if (_isShuttingDown) return;
                args.Cancel = true;
                _ = _settingsViewModel.SaveAsync();
                _settingsWindow.Hide();
            };
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
        FadeIn(_settingsWindow);
    }

    private void ShowVisionSettings()
    {
        if (_settingsViewModel is null) return;
        if (_visionWindow is null)
        {
            _visionWindow = new VisionSettingsWindow(_settingsViewModel);
            _visionWindow.IsVisibleChanged += (_, _) => UpdateKeepExpanded();
            _visionWindow.Closing += (_, args) =>
            {
                if (_isShuttingDown) return;
                args.Cancel = true;
                _ = _settingsViewModel.SaveAsync(false);
                _visionWindow.Hide();
            };
        }
        _visionWindow.Show();
        _visionWindow.Activate();
        FadeIn(_visionWindow);
    }

    private static void FadeIn(Window window)
    {
        window.Opacity = 0;
        window.BeginAnimation(Window.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(
            1, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        });
    }

    private void ShowTimerAlarm()
    {
        if (_timerAlarm is null || _settings is null || _islandWindow is null) return;
        if (_timerWindow is null)
        {
            _timerViewModel = new TimerAlarmViewModel(_timerAlarm, _settings.Use24HourClock);
            _timerWindow = new TimerAlarmWindow(_islandWindow) { DataContext = _timerViewModel };
            _timerWindow.Closed += (_, _) => { _timerViewModel.Dispose(); _timerViewModel = null; _timerWindow = null; };
        }
        _timerWindow.PositionBelowOwner();
        _timerWindow.Show();
        _timerWindow.Activate();
        FadeIn(_timerWindow);
    }

    private async Task SaveAndApplyAsync()
    {
        if (_settingsService is null || _settings is null) return;
        await _settingsService.SaveAsync(_settings);
        ApplySettings();
    }

    private void ApplySettings()
    {
        if (_settings is null) return;
        _log?.SetDebugEnabled(_settings.DebugLogging);
        ApplyGlobalTheme();
        _islandViewModel?.ApplySettings();
        _islandWindow?.ApplySettings();
        ApplyVisionSettings();
        ApplyLiveActivitySettings();
        _tray?.SyncChecks();
    }

    // Transition-based: only opens/closes the camera when the enabled flag actually flips, so unrelated
    // settings changes don't restart the webcam loop.
    private void ApplyVisionSettings()
    {
        if (_vision is null || _settings is null) return;
        _vision.Configure(_settings.VisionPrivacyMode, _settings.VisionTargetFps,
            _settings.VisionCameraIndex, _settings.VisionFaceMatchThreshold);
        if (_settings.VisionEnabled && !_vision.IsRunning) _vision.Start();
        else if (!_settings.VisionEnabled && _vision.IsRunning) _vision.Stop();
    }

    private bool _calendarStarted, _notificationsStarted;
    private void ApplyLiveActivitySettings()
    {
        if (_settings is null) return;
        _weather?.Configure(_settings.WeatherLocation, _settings.WeatherFahrenheit);
        _stocks?.Configure(_settings.StockSymbols);
        if (_battery is not null) { _battery.LowThreshold = _settings.LowBatteryThreshold; _battery.WarningsEnabled = _settings.LowBatteryWarning; }
        if (_settings.ShowSystemMonitor || _settings.ShowRamInCompact) _sysMon?.Start();
        if (_settings.RealAudioSpectrum) _spectrum?.Start();
        if (_settings.ShowNextMeeting && !_calendarStarted) { _calendarStarted = true; _ = _calendar!.StartAsync(); }
        if (_settings.ShowNotifications && !_notificationsStarted) { _notificationsStarted = true; _ = _notifications!.StartAsync(); }
    }

    // Camera-driven automations: lock on unknown, presence-aware media, privacy blur.
    private void HandleVisionAutomations(Models.VisionState s)
    {
        if (_settings is null) return;
        var running = s.Availability == VisionAvailability.Running;
        var unknown = running && s.PrivacyOn && s.Enrolled && s.Alert;

        if (_settings.AutoLockOnUnknown && unknown)
        {
            if (_autoLockTimer is null)
            {
                _autoLockTimer = new System.Windows.Threading.DispatcherTimer();
                _autoLockTimer.Tick += (_, _) => { _autoLockTimer!.Stop(); Interop.NativeMethods.LockWorkStation(); };
            }
            if (!_autoLockTimer.IsEnabled)
            {
                _autoLockTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.AutoLockDelaySeconds, 2, 60));
                _autoLockTimer.Start();
            }
        }
        else _autoLockTimer?.Stop();

        if (_settings.PrivacyAutoBlur && unknown) ShowPrivacyBlur(); else HidePrivacyBlur();

        if (_settings.PresenceAwareMedia && running && _media is not null)
        {
            var nobody = s.PeopleCount == 0;
            if (nobody && !_autoPausedMedia && _media.Current.IsPlaying)
            { _ = _media.TogglePlayPauseAsync(); _autoPausedMedia = true; }
            else if (!nobody && _autoPausedMedia)
            { if (!_media.Current.IsPlaying) _ = _media.TogglePlayPauseAsync(); _autoPausedMedia = false; }
        }
    }

    private void ShowPrivacyBlur()
    {
        if (_privacyBlur is null)
        {
            _privacyBlur = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, WindowState = WindowState.Maximized,
                Topmost = true, ShowInTaskbar = false, AllowsTransparency = true,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(235, 8, 10, 16)),
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "Privacy mode\nUnknown person detected — click to dismiss",
                    Foreground = System.Windows.Media.Brushes.White, FontSize = 22,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                }
            };
            _privacyBlur.MouseDown += (_, _) => _privacyBlur!.Hide();
        }
        if (!_privacyBlur.IsVisible) _privacyBlur.Show();
    }

    private void HidePrivacyBlur() { if (_privacyBlur?.IsVisible == true) _privacyBlur.Hide(); }

    // Clipboard history flyout: list of recent text items; click to copy back.
    private async Task ShowClipboardAsync()
    {
        if (_clipboard is null || _islandWindow is null) return;
        var items = await _clipboard.GetRecentTextAsync(8);
        var panel = new System.Windows.Controls.StackPanel();
        Window? win = null;
        if (items.Count == 0)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Clipboard history is empty or off.\nTurn it on with Win+V.",
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0xA8, 0xBB)),
                FontSize = 12, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap, MaxWidth = 300
            });
        }
        else
        {
            foreach (var item in items)
            {
                var captured = item;
                var oneLine = item.Replace("\r", " ").Replace("\n", " ");
                if (oneLine.Length > 70) oneLine = oneLine[..70] + "…";
                var btn = new System.Windows.Controls.Button
                {
                    Content = oneLine, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                    Padding = new Thickness(11, 8, 11, 8), Margin = new Thickness(4, 2, 4, 2),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x2D, 0x3A)),
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0xF5, 0xFB)),
                    BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, MaxWidth = 360
                };
                btn.Click += (_, _) => { _clipboard!.CopyText(captured); win?.Close(); };
                panel.Children.Add(btn);
            }
        }

        var card = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1B, 0x22, 0x2D)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(6),
            Child = panel
        };
        win = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent, SizeToContent = SizeToContent.WidthAndHeight,
            Topmost = true, ShowInTaskbar = false, Content = card,
            Left = _islandWindow.Left + _islandWindow.Width / 2 - 200, Top = _islandWindow.Top + 70
        };
        win.Deactivated += (_, _) => win.Close();
        win.Show();
        win.Activate();
    }

    // Pin the island open while any settings window is visible so live size/appearance edits are visible.
    private void UpdateKeepExpanded()
    {
        if (_islandViewModel is null) return;
        var keep = (_settingsWindow?.IsVisible == true) || (_visionWindow?.IsVisible == true);
        _islandViewModel.KeepExpanded = keep;
        if (!keep) _islandViewModel.IsExpanded = false;
    }

    private void Recenter()
    {
        if (_islandWindow is null || _position is null || _settings is null) return;
        _islandWindow.ForceShow();
        _position.Recenter(_islandWindow, _settings);
        _ = _settingsService?.SaveAsync(_settings);
    }

    private void ApplyGlobalTheme()
    {
        if (_theme is null || _settings is null) return;
        var dark = _theme.IsDark(_settings.Theme);
        SetBrush("SettingsBackgroundBrush", dark ? "#11151C" : "#F3F5F9");
        SetBrush("SettingsHeaderBrush", dark ? "#181D26" : "#F9FBFF");
        SetBrush("SettingsCardBrush", dark ? "#1B222D" : "#FFFFFFFF");
        SetBrush("SettingsCardBorderBrush", dark ? "#354052" : "#16000000");
        SetBrush("SettingsTextBrush", dark ? "#F1F5FB" : "#172033");
        SetBrush("SettingsMutedBrush", dark ? "#9AA8BB" : "#68758A");
        SetBrush("SettingsInputBrush", dark ? "#242D3A" : "#FFFFFFFF");
        SetBrush("SettingsSubtleButtonBrush", dark ? "#303A49" : "#E5EAF1");
    }

    private void SetBrush(string key, string color)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Resources[key] = brush;
    }

    private void ShutdownApplication()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;
        _log?.Info("Application shutting down");
        _autoLockTimer?.Stop();
        _privacyBlur?.Close();
        _tray?.Dispose();
        _timerWindow?.Close();
        _visionWindow?.Close();
        _settingsWindow?.Close();
        _islandViewModel?.Dispose();
        _vision?.Dispose();
        _weather?.Dispose();
        _sysMon?.Dispose();
        _spectrum?.Dispose();
        _stocks?.Dispose();
        _calendar?.Dispose();
        _notifications?.Dispose();
        _media?.Dispose();
        _audio?.Dispose();
        _battery?.Dispose();
        _clock?.Dispose();
        _timerAlarm?.Dispose();
        _theme?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        Shutdown();
    }
}
