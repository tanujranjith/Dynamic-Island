using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Q.Core;
using DynamicIsland.Windows.Models;
using DynamicIsland.Windows.Services;
using DynamicIsland.Windows.Services.Q;
using DynamicIsland.Windows.ViewModels;
using DynamicIsland.Windows.Views;

namespace DynamicIsland.Windows.Infrastructure;

// Opt-in native fixture harness. No media, Bluetooth, microphone, screen capture or provider service is started.
internal static class UpgradeVerification
{
    public static async Task RunAsync()
    {
        if (!AppDataPaths.IsPreview) throw new InvalidOperationException("Verification requires ISLAND_PREVIEW=1.");
        Directory.CreateDirectory(AppDataPaths.Root);
        var output = Path.Combine(AppDataPaths.Root, "captures"); Directory.CreateDirectory(output);
        using var bindingLog = new TextWriterTraceListener(Path.Combine(output, "bindings.log"));
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingLog);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var log = new LoggingService(); var settingsService = new SettingsService(log);
        var settings = new AppSettings { HasOnboarded = true, AlwaysOnTop = false, ShowConnectivity = false,
            ShowWeather = true, ShowCountdown = true, CountdownLabel = "Project launch", CountdownDate = "2026-12-15", ShowNotifications = true,
            AnimationIntensity = AnimationIntensity.Reduced, ShowIslandInScreenshots = true, QDisclosureAccepted = true };
        using var media = new MediaSessionService(log); using var audio = new AudioSessionService(log);
        using var battery = new BatteryService(); using var clock = new ClockService(); using var timers = new TimerAlarmService(log);
        using var theme = new ThemeService(); using var weather = new WeatherService(log); using var monitor = new SystemMonitorService();
        using var spectrum = new AudioSpectrumService(log); using var stocks = new StocksService(log); using var calendar = new CalendarService(log);
        using var notifications = new NotificationListenerService(log); using var privacy = new PrivacySensorService(log);
        var history = new NotificationHistoryService(log); using var q = new QSessionController(new QProviderRegistry([]));
        var screen = new ScreenContextService(log);
        using var vm = new IslandViewModel(settings, media, audio, battery, clock, timers, theme, weather, monitor, spectrum, stocks,
            calendar, notifications, privacy, history, q, new FakeScreen(), new FakeSpeech(), new FakeSecrets());
        using var timerVm = new TimerAlarmViewModel(timers, false);
        var position = new WindowPositionService();
        var window = new IslandWindow(vm, timerVm, position, settingsService, log, screen)
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        var shell = (FrameworkElement)window.FindName("GlassShell");
        var report = new List<string>();
        void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); report.Add("PASS " + description); }
        var migrationPath = Path.Combine(AppDataPaths.Root, "migration.json");
        const string legacy = """{"Timer":{"Label":"Legacy tea","Phase":2,"TotalSeconds":300,"PausedRemainingSeconds":120},"Alarm":{"Phase":1,"Label":"Legacy wake","Hour":8,"TargetAt":"2099-01-01T08:00:00+00:00"}}""";
        File.WriteAllText(migrationPath, legacy);
        using (var migration = new TimerAlarmService(log, migrationPath))
        {
            Check(migration.State.Timer.Label == "Legacy tea" && migration.TimerRemaining.TotalSeconds == 120, "Persisted legacy timer migrates without losing remaining time");
            Check(File.ReadAllText(migrationPath + ".pre-v2.bak") == legacy, "Migration retains an exact pre-upgrade backup");
        }
        const string future = "{\"Version\":99,\"FutureData\":\"preserve\"}";
        File.WriteAllText(migrationPath, future);
        using (var futureService = new TimerAlarmService(log, migrationPath))
        { futureService.StartTimer(TimeSpan.FromMinutes(1), "Temporary"); Check(futureService.StorageWarning.Length > 0, "Future storage exposes a visible warning"); }
        Check(File.ReadAllText(migrationPath) == future, "Newer-version data is never overwritten");
        void Invoke(string name, params object?[] args) => typeof(IslandViewModel).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, args);
        async Task Capture(string name)
        {
            window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Check(shell.ActualWidth <= window.ActualWidth, name + ": shell fits window width");
            foreach (var scale in new[] { 1d, 1.5d, 2d })
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render((Visual)window.Content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"{name}-{scale * 100:0}.png")); encoder.Save(file);
            }
        }
        Invoke("OnMediaChanged", null, new MediaInfo { Title = "Midnight Drive — an intentionally long track title for layout verification", Artist = "Island Studio", SourceAppName = "Fixture player", PlaybackState = MediaPlaybackState.Playing, Duration = TimeSpan.FromMinutes(4), Position = TimeSpan.FromSeconds(90), CanPlayPause = true, CanSeek = true });
        Invoke("OnWeatherChanged", null, new WeatherInfo("72°", "\uE706", "Clear skies", "Indianapolis"));
        Invoke("OnPrivacyChanged", null, new PrivacySensorState(["Fixture camera"], ["Fixture microphone"]));
        timers.StartTimer(TimeSpan.FromMinutes(5), "Tea"); var first = timers.State.Timer.Id;
        timers.StartTimer(TimeSpan.FromMinutes(25), "Focus"); var second = timers.State.Timer.Id;
        Check(timerVm.Timers.Count == 2, "Both timer choices visible");
        timerVm.SelectedTimerId = first; timerVm.TimerPrimaryCommand.Execute(null);
        Check(timers.State.Timers.Single(t => t.Id == first).Phase == TimerPhase.Paused && timers.State.Timers.Single(t => t.Id == second).Phase == TimerPhase.Running, "Selected timer controls leave other timer running");
        timers.SetAlarm(8, 0, false, "Tomorrow", AlarmRepeat.Daily);
        Check(vm.PrimaryActivity == IslandActivity.Media, "Scheduled alarm does not replace music");
        vm.PinTimerCommand.Execute(null); Check(vm.PrimaryActivity == IslandActivity.Timer, "Selected timer pin takes priority");
        vm.UnpinActivityCommand.Execute(null);
        // Exercise real motion too: reduced-motion snapshots cannot detect transient scrollbars
        // or an expanded subtree that gets resized on every shell animation frame.
        var viewport = (ScrollViewer)window.FindName("ExpandedViewport");
        var expandedContent = (FrameworkElement)window.FindName("ExpandedContent");
        settings.AnimationIntensity = AnimationIntensity.Expressive;
        vm.IsExpanded = false; window.ApplySettings();
        vm.IsExpanded = true; window.UpdateLayout();
        var stableViewport = viewport.RenderSize;
        var contentResizes = 0;
        SizeChangedEventHandler countResize = (_, _) => contentResizes++;
        expandedContent.SizeChanged += countResize;
        for (var frame = 0; frame < 5; frame++)
        {
            await Task.Delay(50); window.UpdateLayout();
            Check(viewport.ComputedVerticalScrollBarVisibility != Visibility.Visible && viewport.ComputedHorizontalScrollBarVisibility != Visibility.Visible,
                $"Expansion frame {frame}: no outer scrollbar");
            Check(viewport.RenderSize == stableViewport, $"Expansion frame {frame}: stable content viewport");
        }
        await Task.Delay(180); window.UpdateLayout();
        expandedContent.SizeChanged -= countResize;
        Check(contentResizes <= 1, "Expanded content avoids per-frame layout resizing");
        Check(Math.Abs(shell.ActualWidth - viewport.ActualWidth) < 1 && Math.Abs(shell.ActualHeight - viewport.ActualHeight) < 1,
            "Expansion lands on the content viewport bounds");
        await Capture("expansion-settled");
        vm.IsExpanded = false; await Task.Delay(50); vm.IsExpanded = true;
        await Task.Delay(450); window.UpdateLayout();
        Check(Math.Abs(shell.ActualWidth - viewport.ActualWidth) < 1, "Interrupted morph returns to the expanded bounds");
        settings.AnimationIntensity = AnimationIntensity.Reduced;
        vm.IsExpanded = true; window.ApplySettings(); await Capture("apple-no-airpods");
        var widgets = (ScrollViewer)window.FindName("LiveWidgetsScroller");
        report.Add($"Widgets: viewport={widgets.ViewportWidth}, extent={widgets.ExtentWidth}, weather={vm.WeatherWidgetWidth}, countdown={vm.CountdownWidgetWidth}, rail={vm.LiveWidgetRailWidth}");
        Check(widgets.ScrollableWidth < 1, "Two widgets fill the lane without false overflow");
        Invoke("OnAirPodsChanged", null, new AirPodsState { IsAvailable = true, IsConnected = true, ModelName = "AirPods Pro", DeviceName = "Fixture AirPods", LeftBatteryPercent = 90, RightBatteryPercent = 80, CaseBatteryPercent = 70, LastUpdated = DateTimeOffset.Now });
        await Capture("apple-airpods");
        settings.IslandVisualMode = IslandVisualMode.Stats; vm.ApplySettings(); window.ApplySettings(); await Capture("stats");
        settings.IslandVisualMode = IslandVisualMode.Apple; settings.InterfaceScale = 150; settings.MediaTitleSize = 160; vm.ApplySettings(); window.ApplySettings(); await Capture("large-text");
        window.ShowTimerPanel(); await Capture("timers");
        var timerHost = (Grid)window.FindName("TimerTabContent");
        var timerPanel = (TimerListPanel)timerHost.Children[0];
        var scroller = (ScrollViewer)timerPanel.FindName("EditorScroller");
        report.Add($"Timer scrolling: viewport={scroller.ViewportHeight}, extent={scroller.ExtentHeight}, height={scroller.ActualHeight}");
        Check(scroller.ScrollableHeight > 0, "Timer editor scrolls overflow instead of clipping controls");
        scroller.ScrollToEnd(); await Capture("timers-scrolled");
        typeof(IslandWindow).GetMethod("AlarmTab_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [null, new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)]);
        await Capture("alarms");
        var alarmPanel = (AlarmListPanel)((Grid)window.FindName("AlarmTabContent")).Children[0];
        ((ScrollViewer)alarmPanel.FindName("EditorScroller")).ScrollToEnd(); await Capture("alarms-scrolled");
        window.CloseTimerPanel();
        await q.BeginAsync(QMode.Ask, "fixture", "fixture", null);
        var streaming = new QSessionSnapshot(QRunState.Streaming, QMode.Ask, "Fixture question", "First response chunk", "Responding…", null, null, "fixture", "fixture");
        Invoke("OnQChanged", streaming);
        vm.IsExpanded = true; window.ApplySettings();
        var prompt = (System.Windows.Controls.TextBox)window.FindName("QPromptBox"); prompt.Text = "Keep this unfinished question";
        var alarm = timers.State.Alarms[0]; alarm.Phase = AlarmPhase.Ringing; alarm.RingStartedAt = DateTimeOffset.Now;
        timers.SelectAlarm(alarm.Id);
        Check(vm.ShowQSurface && vm.HasUrgentAlert, "Q stays open while an alarm rings");
        Check(prompt.Text == "Keep this unfinished question", "Q draft survives alarm arrival");
        Invoke("OnQChanged", streaming with { Response = "First response chunk, followed by another chunk" });
        Check(vm.QResponse.EndsWith("another chunk") && vm.ShowQSurface, "Q response continues updating while an alarm is visible");
        await Capture("q-with-alert"); vm.DismissUrgentCommand.Execute(null); q.Clear();
        vm.InteractionProtected = true;
        Invoke("OnNotificationBatch", null, new NotificationInfo[] { new("Mail", "First", "One", 1, DateTimeOffset.Now, "fixture.mail"), new("Mail", "Second", "Two", 2, DateTimeOffset.Now, "fixture.mail") });
        Check(!vm.ShowNotification && history.Items.Count == 2, "Protected interaction queues notifications and saves both items");
        vm.InteractionProtected = false; Invoke("PumpNotifications");
        Check(vm.NotificationTitle.Contains("(+1)"), "Notification burst has grouped banner");
        var sequence = vm.BannerSeq;
        Invoke("PumpNotifications");
        Check(vm.BannerSeq == sequence, "Polling does not replay the banner animation");
        await Capture("notification-burst"); vm.DismissCurrentNotificationCommand.Execute(null);
        Check(history.Items.Count == 0, "Grouped dismissal targets displayed items");
        settings.ShowNotifications = false; Invoke("PumpNotifications");
        Check(!vm.ShowNotification, "Disabling notifications clears the banner");
        settings.ShowNotifications = true; settings.FocusModeEnabled = true;
        settings.NotificationFilterMode = NotificationFilter.Allowlist; settings.NotificationAppFilter = "Mail";
        Invoke("OnNotificationBatch", null, new NotificationInfo[] { new("Mail", "Allowed", "", 3, DateTimeOffset.Now, "fixture.mail"), new("Chat", "Blocked", "", 4, DateTimeOffset.Now, "fixture.chat") });
        Check(history.Items.Count == 1 && !vm.ShowNotification, "Focus Mode saves only allowlisted notifications without showing a banner");
        settings.FocusModeEnabled = false; settings.NotificationHistoryEnabled = false; settings.NotificationFilterMode = NotificationFilter.Blocklist;
        Invoke("OnNotificationBatch", null, new NotificationInfo[] { new("Mail", "Blocked", "", 5, DateTimeOffset.Now, "fixture.mail"), new("Chat", "Ephemeral", "", 6, DateTimeOffset.Now, "fixture.chat") });
        Check(history.Items.Count == 1, "History-disabled arrivals do not persist");
        settings.ShowNotifications = false; Invoke("PumpNotifications");
        settings.ShowNextMeeting = true;
        typeof(CalendarService).GetMethod("SetStatus", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(calendar, [IntegrationState.PermissionRequired, "Allow calendar access", "ms-settings:privacy-calendar"]);
        Check(vm.ShowNextMeeting && vm.MeetingTitle == "Allow calendar access" && !vm.HasMeetingJoin, "Calendar permission failure is visible without a misleading Join action");
        calendar.Stop(); Check(calendar.Status.State == IntegrationState.Disabled, "Stopping calendar clears integration status");
        notifications.Stop(); notifications.Stop(); Check(notifications.Status.State == IntegrationState.Disabled, "Repeated notification stop is safe");
        settings.ShowNextMeeting = false;
        settings.ShowWorldClocks = true; settings.WorldClockZones = "UTC,Eastern Standard Time,Pacific Standard Time,India Standard Time";
        vm.ApplySettings(); vm.IsExpanded = true; window.ApplySettings(); await Capture("widget-overflow");
        widgets.ScrollToRightEnd(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(widgets.HorizontalOffset > 0, "Extra widgets are reachable by scrolling");
        settings.ShowWorldClocks = false; vm.ApplySettings(); window.ApplySettings();
        position.VerificationWorkArea = (640, 480);
        window.ApplySettings(); await Capture("narrow-apple");
        Check(shell.ActualWidth <= 616 && shell.ActualHeight <= 404, "Narrow working area bounds both dimensions");
        viewport.ScrollToRightEnd(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(viewport.HorizontalOffset > 0, "Constrained outer viewport still scrolls with hidden chrome");
        viewport.ScrollToLeftEnd();
        window.ShowTimerPanel(); await Capture("narrow-timers"); window.CloseTimerPanel();
        await q.BeginAsync(QMode.Ask, "fixture", "fixture", null); vm.IsExpanded = true; window.ApplySettings(); await Capture("narrow-q");
        var composerPosition = prompt.TranslatePoint(new System.Windows.Point(0, 0), shell);
        Check(composerPosition.Y + prompt.ActualHeight <= shell.ActualHeight + 1, "Q composer stays reachable in a short working area");
        q.Clear(); position.VerificationWorkArea = null;
        // Both settings and standalone timer windows must resolve their real resources and bindings.
        using var codex = new AsyncDisposeAdapter(new CodexAppServerClient(log: log));
        var account = new CodexAccountCoordinator(codex.Client, log);
        var settingsVm = new SettingsViewModel(settings, settingsService, new StartupService(log), () => { }, () => { }, () => { }, new FakeSecrets(), new QProviderRegistry([]), account);
        var settingsWindow = new SettingsWindow(settingsVm, vm) { Opacity = 0, ShowActivated = false }; settingsWindow.Show(); settingsWindow.UpdateLayout(); settingsWindow.Close();
        var timerWindow = new TimerAlarmWindow(window) { DataContext = timerVm, Opacity = 0, ShowActivated = false }; timerWindow.Show(); timerWindow.UpdateLayout(); timerWindow.Close();
        bindingLog.Flush(); File.WriteAllLines(Path.Combine(output, "checks.txt"), report);
        window.Close(); PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingLog);
    }
    private sealed class FakeScreen : IQScreenContextService { public Task<QScreenContext?> CaptureAsync(nint window, DynamicIsland.Q.Core.QCaptureMode mode, CancellationToken token) => Task.FromResult<QScreenContext?>(null); }
    private sealed class FakeSpeech : IQSpeechInputService { public bool IsAvailable => false; public Task<string?> DictateAsync(CancellationToken token) => Task.FromResult<string?>(null); }
    private sealed class FakeSecrets : IQSecretStore { public string? Get(string id) => null; public void Set(string id, string? value) { } public void Remove(string id) { } }
    private sealed class AsyncDisposeAdapter(CodexAppServerClient client) : IDisposable { public CodexAppServerClient Client => client; public void Dispose() => client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}
