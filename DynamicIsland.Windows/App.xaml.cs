using System.Threading;
using System.Windows;
using DynamicIsland.Windows.Models;
using DynamicIsland.Windows.Services;
using DynamicIsland.Windows.ViewModels;
using DynamicIsland.Windows.Views;
using DynamicIsland.Windows.Services.Q;
using DynamicIsland.Q.Core;
using System.Windows.Media;

namespace DynamicIsland.Windows;

public partial class App : System.Windows.Application
{
    private static string ShowSettingsSignalName => "Local\\DynamicIsland.Windows.ShowSettings" + Infrastructure.AppDataPaths.InstanceSuffix;
    private Mutex? _singleInstance;
    private EventWaitHandle? _showSettingsSignal;
    private LoggingService? _log;
    private SettingsService? _settingsService;
    private StartupService? _startupService;
    private MediaSessionService? _media;
    private AudioSessionService? _audio;
    private BatteryService? _battery;
    private ClockService? _clock;
    private TimerAlarmService? _timerAlarm;
    private ThemeService? _theme;
    private AirPodsService? _airPods;
    private WeatherService? _weather;
    private SystemMonitorService? _sysMon;
    private AudioSpectrumService? _spectrum;
    private StocksService? _stocks;
    private CalendarService? _calendar;
    private NotificationListenerService? _notifications;
    private NotificationHistoryService? _notificationHistory;
    private GlobalHotkeyService? _hotkeys;
    private PrivacySensorService? _privacy;
    private ClipboardService? _clipboard;
    private WindowPositionService? _position;
    private IslandViewModel? _islandViewModel;
    private SettingsViewModel? _settingsViewModel;
    private IslandWindow? _islandWindow;
    private SettingsWindow? _settingsWindow;
    private TimerAlarmViewModel? _timerViewModel;
    private TrayService? _tray;
    private AppSettings? _settings;
    private bool _isShuttingDown;
    private ScreenContextService? _qScreen;
    private SpeechInputService? _qSpeech;
    private DpapiSecretStore? _qSecrets;
    private CodexAppServerClient? _codexClient;
    private CodexAccountCoordinator? _codexAccount;
    private IQSessionController? _qSession;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--verify-upgrades"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (!Infrastructure.AppDataPaths.IsPreview) { Shutdown(2); return; }
            try { await Infrastructure.UpgradeVerification.RunAsync(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Infrastructure.AppDataPaths.Root); File.WriteAllText(Path.Combine(Infrastructure.AppDataPaths.Root, "verification-error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        var openSettingsFromCommandLine = e.Args
            .Any(arg => string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase));
        _singleInstance = new Mutex(true, "Local\\DynamicIsland.Windows.SingleInstance" + Infrastructure.AppDataPaths.InstanceSuffix, out var firstInstance);
        if (!firstInstance)
        {
            if (openSettingsFromCommandLine)
            {
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(ShowSettingsSignalName);
                    signal.Set();
                }
                catch (WaitHandleCannotBeOpenedException) { }
            }
            Shutdown();
            return;
        }
        _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsSignalName);
        _ = Task.Run(() =>
        {
            while (!_isShuttingDown && _showSettingsSignal.WaitOne())
            {
                if (!_isShuttingDown)
                    Dispatcher.BeginInvoke(ShowSettings);
            }
        });

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
        {
            _log.Error("Unhandled application exception", args.ExceptionObject as Exception);
        };

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
        _airPods = new AirPodsService(_log);
        _weather = new WeatherService(_log);
        _sysMon = new SystemMonitorService();
        _spectrum = new AudioSpectrumService(_log);
        _stocks = new StocksService(_log);
        _calendar = new CalendarService(_log);
        _notifications = new NotificationListenerService(_log);
        _notificationHistory = new NotificationHistoryService(_log);
        _privacy = new PrivacySensorService(_log);
        _clipboard = new ClipboardService(_log);
        _qScreen = new ScreenContextService(_log);
        _qSpeech = new SpeechInputService(_log);
        _qSecrets = new DpapiSecretStore(_log);
        _codexClient = new CodexAppServerClient("1.0.6", log: _log);
        _codexAccount = new CodexAccountCoordinator(_codexClient, _log);
        var providers = new QProviderRegistry([
            new OpenAiQProvider(), new AnthropicQProvider(), new GeminiQProvider(), new GroqQProvider(),
            new XaiQProvider(), new OpenRouterQProvider(), new DeepSeekQProvider(), new OllamaQProvider(),
            new CodexQProvider(_codexClient)
        ]);
        _qSession = new QSessionController(providers);
        _battery.LowBattery += (_, pct) => Dispatcher.BeginInvoke(() =>
            _tray?.ShowNotification("Battery low", $"{pct}% remaining — plug in soon."));
        _position = new WindowPositionService();
        _islandViewModel = new IslandViewModel(_settings, _media, _audio, _battery, _clock, _timerAlarm, _theme,
            _weather, _sysMon, _spectrum, _stocks, _calendar, _notifications, _privacy, _notificationHistory,
            _qSession, _qScreen, _qSpeech, _qSecrets, _codexAccount, _airPods, _settingsService);
        _islandViewModel.AttachClipboard(_clipboard);
        _timerViewModel = new TimerAlarmViewModel(_timerAlarm, _settings.Use24HourClock);
        _islandWindow = new IslandWindow(_islandViewModel, _timerViewModel, _position, _settingsService, _log, _qScreen);
        _islandWindow.OpenSettingsRequested += (_, _) => ShowSettings();
        _islandWindow.OpenClipboardRequested += (_, _) => _ = ShowClipboardAsync();
        _islandWindow.RecenterRequested += (_, _) => Recenter();
        _islandWindow.Closed += (_, _) => { if (!_isShuttingDown) ShutdownApplication(); };

        _settingsViewModel = new SettingsViewModel(_settings, _settingsService, _startupService,
            ApplySettings, Recenter, () => _settingsWindow?.Hide(), _qSecrets, providers, _codexAccount);
        _media.AvailableAppsChanged += (_, apps) => Dispatcher.BeginInvoke(() =>
            _settingsViewModel.SetAvailableApps(apps));

        _tray = new TrayService(_settings, ShowSettings, () => _ = _islandViewModel!.StartQAsync(_qScreen!.LastForegroundTarget), Recenter, SaveAndApplyAsync, ShutdownApplication,
            ToggleFocus, () => _settings.FocusModeEnabled);
        _timerAlarm.EventRaised += (_, args) => Dispatcher.BeginInvoke(() =>
            _tray.ShowNotification(args.Title, args.Message));

        _islandWindow.Show();
        if (!Infrastructure.AppDataPaths.IsPreview) _ = _codexAccount.RefreshAsync();
        _hotkeys = new GlobalHotkeyService(_log);
        _hotkeys.Attach(_islandWindow);
        _hotkeys.Register("Expand/collapse", Interop.NativeMethods.HotkeyModifierControl | Interop.NativeMethods.HotkeyModifierAlt, 0x20,
            () => _islandViewModel.ToggleExpandedCommand.Execute(null));
        _hotkeys.Register("Mute", Interop.NativeMethods.HotkeyModifierControl | Interop.NativeMethods.HotkeyModifierAlt, (uint)'M',
            () => _islandViewModel.ToggleMuteCommand.Execute(null));
        _hotkeys.Register("Timer", Interop.NativeMethods.HotkeyModifierControl | Interop.NativeMethods.HotkeyModifierAlt, (uint)'T',
            ToggleDefaultTimer);
        _hotkeys.Register("Focus mode", Interop.NativeMethods.HotkeyModifierControl | Interop.NativeMethods.HotkeyModifierAlt, (uint)'F',
            ToggleFocus);
        _hotkeys.Register("Open settings", Interop.NativeMethods.HotkeyModifierControl | Interop.NativeMethods.HotkeyModifierAlt, (uint)'S',
            ShowSettings);
        _hotkeys.Register("Open Q", Interop.NativeMethods.HotkeyModifierControl | Interop.NativeMethods.HotkeyModifierAlt, (uint)'Q',
            () => _ = _islandViewModel.StartQAsync(_qScreen.LastForegroundTarget, _settings.QHotkeyShortcut));
        _clock.Start();
        _battery.Start();
        _audio.Start();
        _timerAlarm.Start();
        _privacy.Start();
        _airPods?.Start();
        _weather.Start();
        _stocks.Start();
        ApplyLiveActivitySettings();
        await _media.StartAsync();
        // First-run onboarding: open the (new) settings so people can explore what's customisable.
        if (!_settings.HasOnboarded || openSettingsFromCommandLine)
        {
            if (!_settings.HasOnboarded)
            {
                _settings.HasOnboarded = true;
                await _settingsService.SaveAsync(_settings);
            }
            ShowSettings();
        }
    }

    private void ShowSettings()
    {
        if (_settingsViewModel is null) return;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settingsViewModel, _islandViewModel!);
            _settingsWindow.OpenTimerRequested += (_, _) => ShowTimerAlarm();
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
        _islandWindow?.ShowTimerPanel();
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
        ApplyLiveActivitySettings();
        _tray?.SyncChecks();
    }

    private bool _liveSettingsApplied, _clipboardEnabled;
    private void ApplyLiveActivitySettings()
    {
        if (_settings is null) return;
        _weather?.Configure(_settings.WeatherLocation, _settings.WeatherFahrenheit);
        _stocks?.Configure(_settings.StockSymbols);
        if (_battery is not null) { _battery.LowThreshold = _settings.LowBatteryThreshold; _battery.WarningsEnabled = _settings.LowBatteryWarning; }
        if (_settings.ShowSystemMonitor || _settings.ShowRamInCompact) _sysMon?.Start();
        else _sysMon?.Stop();
        if (_settings.RealAudioSpectrum) _spectrum?.Start();
        else _spectrum?.Stop();
        if (_settings.ShowNextMeeting) _ = _calendar!.StartAsync(_liveSettingsApplied); else _calendar!.Stop();
        if (_settings.ShowNotifications) _ = _notifications!.StartAsync(_liveSettingsApplied); else _notifications!.Stop();
        if (!_liveSettingsApplied || _clipboardEnabled != _settings.ShowClipboard) { _clipboardEnabled = _settings.ShowClipboard; _clipboard!.Configure(_clipboardEnabled); }
        _liveSettingsApplied = true;
    }

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
                Text = _clipboard.Status.Message,
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

        if (items.Count == 0)
        {
            var retry = new System.Windows.Controls.Button { Content = "Retry", Margin = new Thickness(8), Padding = new Thickness(8) };
            retry.Click += async (_, _) => { win?.Close(); await ShowClipboardAsync(); }; panel.Children.Add(retry);
            if (_clipboard.Status.SettingsUri is string uri)
            {
                var setup = new System.Windows.Controls.Button { Content = "Open Windows Settings", Margin = new Thickness(8), Padding = new Thickness(8) };
                setup.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); panel.Children.Add(setup);
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
        var keep = _settingsWindow?.IsVisible == true;
        _islandViewModel.KeepExpanded = keep;
        _islandWindow?.SetSettingsWindowOpen(keep);
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
        _tray?.Dispose();
        _timerViewModel?.Dispose();
        _settingsWindow?.Close();
        _islandViewModel?.Dispose();
        _qSession?.Dispose();
        _codexClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _qSpeech?.Dispose();
        _weather?.Dispose();
        _sysMon?.Dispose();
        _spectrum?.Dispose();
        _stocks?.Dispose();
        _calendar?.Dispose();
        _notifications?.Dispose();
        _hotkeys?.Dispose();
        _showSettingsSignal?.Set();
        _showSettingsSignal?.Dispose();
        _airPods?.Dispose();
        _privacy?.Dispose();
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

    private void ToggleDefaultTimer()
    {
        if (_timerAlarm is null) return;
        var displayed = _islandViewModel?.DisplayTimer;
        if (displayed is { Phase: TimerPhase.Running }) _timerAlarm.PauseTimer(displayed.Id);
        else if (displayed is { Phase: TimerPhase.Paused }) _timerAlarm.ResumeTimer(displayed.Id);
        else _timerAlarm.StartTimer(TimeSpan.FromMinutes(10), "Shortcut timer");
    }

    private void ToggleFocus()
    {
        _islandViewModel?.ToggleFocusCommand.Execute(null);
        ApplySettings();
    }
}
