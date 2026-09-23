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
using ThemeMode = DynamicIsland.Windows.Models.ThemeMode;

namespace DynamicIsland.Windows.Infrastructure;

// Opt-in native fixture harness. No media, Bluetooth, microphone, screen capture or provider service is started.
internal static class UpgradeVerification
{
    public static async Task RunAsync(bool providersOnly = false, bool comparisonOnly = false)
    {
        providersOnly |= comparisonOnly;
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
        var fixtures = new[] { "openai", "gemini", "anthropic", "codex", "ollama" }.Select(id => new ProviderFixture(id)).ToArray();
        if (providersOnly) settings.QSelectedModel = "openai-default";
        var registry = new QProviderRegistry(providersOnly ? fixtures : []);
        IQSecretStore secrets = providersOnly ? new DpapiSecretStore(log) : new FakeSecrets();
        var history = new NotificationHistoryService(log); using var q = new QSessionController(registry);
        var screen = new ScreenContextService(log);
        using var vm = new IslandViewModel(settings, media, audio, battery, clock, timers, theme, weather, monitor, spectrum, stocks,
            calendar, notifications, privacy, history, q, new FakeScreen(), new FakeSpeech(), secrets, qProviders: registry);
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
        if (providersOnly)
        {
            if (comparisonOnly) await VerifyComparisonAsync(settings, settingsService, vm, window, q, secrets, position, Check, Capture, output);
            else await VerifyProvidersAsync(settings, settingsService, log, vm, window, q, secrets, registry, position, Check, Capture);
            bindingLog.Flush(); File.WriteAllLines(Path.Combine(output, "checks.txt"), report);
            window.Close(); PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingLog);
            return;
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
        var compactContent = (FrameworkElement)window.FindName("CompactContent");
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
        vm.IsExpanded = false; window.UpdateLayout();
        await Task.Delay(90); window.UpdateLayout();
        Check(expandedContent.Visibility == Visibility.Visible && expandedContent.Opacity > 0.1,
            "Collapse keeps the expanded surface mounted during the shrink");
        Check(viewport.Visibility == Visibility.Visible && shell.ActualWidth > settings.IslandWidth,
            "Collapse uses the expanded layout bounds until the morph completes");
        await Task.Delay(360); window.UpdateLayout();
        Check(Math.Abs(shell.ActualWidth - settings.IslandWidth) < 1 && compactContent.Visibility == Visibility.Visible,
            "Collapse commits the compact layout after the morph");
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
    private sealed class FakeScreen : IQScreenContextService
    {
        public async Task<QScreenContext?> CaptureAsync(nint window, DynamicIsland.Q.Core.QCaptureMode mode, CancellationToken token)
        { await Task.Delay(25, token).ConfigureAwait(false); return null; }
    }
    private sealed class FakeSpeech : IQSpeechInputService { public bool IsAvailable => false; public Task<string?> DictateAsync(CancellationToken token) => Task.FromResult<string?>(null); }
    private static async Task VerifyProvidersAsync(AppSettings settings, SettingsService persistence, LoggingService log,
        IslandViewModel vm, IslandWindow window, QSessionController q, IQSecretStore secrets, QProviderRegistry registry,
        WindowPositionService position, Action<bool, string> check, Func<string, Task> capture)
    {
        using var codex = new AsyncDisposeAdapter(new CodexAppServerClient(log: log));
        var account = new CodexAccountCoordinator(codex.Client, log);
        var editor = new SettingsViewModel(settings, persistence, new StartupService(log), vm.ApplySettings,
            () => { }, () => { }, secrets, registry, account);
        vm.QProviderSelectionChanged += (_, _) => editor.RefreshQProviderControls();
        var settingsWindow = new SettingsWindow(editor, vm) { Opacity = 0, ShowActivated = false };
        settingsWindow.Show(); settingsWindow.OpenQSettings(); settingsWindow.UpdateLayout();
        var previousTheme = settings.Theme;
        editor.Theme = ThemeMode.Custom; editor.CustomThemeColorHex = "#123D38";
        typeof(SettingsWindow).GetMethod("ShowSection", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(settingsWindow, ["appearance"]);
        settingsWindow.UpdateLayout();
        check(editor.IsCustomTheme && ((System.Windows.Controls.Button)settingsWindow.FindName("CustomThemeColorButton")).IsVisible,
            "Custom theme reveals the color picker control in Appearance");
        var appearanceBitmap = new RenderTargetBitmap((int)Math.Ceiling(settingsWindow.ActualWidth), (int)Math.Ceiling(settingsWindow.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        appearanceBitmap.Render((Visual)settingsWindow.Content);
        var appearanceEncoder = new PngBitmapEncoder(); appearanceEncoder.Frames.Add(BitmapFrame.Create(appearanceBitmap));
        using (var file = File.Create(Path.Combine(AppDataPaths.Root, "captures", "custom-theme-settings.png"))) appearanceEncoder.Save(file);
        editor.Theme = previousTheme; settingsWindow.OpenQSettings();
        var keyBox = (PasswordBox)settingsWindow.FindName("QApiKeyBox");
        void ClickKey(string handler) => typeof(SettingsWindow).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(settingsWindow, [settingsWindow, new RoutedEventArgs()]);
        foreach (var id in new[] { "openai", "gemini", "anthropic" })
        {
            editor.QSelectedProvider = id;
            keyBox.Password = "fixture-only-key-" + id;
            ClickKey("SaveQApiKey_Click");
            check(keyBox.Password.Length == 0 && secrets.Get(id) == "fixture-only-key-" + id, id + ": key saved through editor and input cleared");
        }
        foreach (var id in new[] { "openai", "gemini", "anthropic" })
            check(new DpapiSecretStore(log).Get(id) == "fixture-only-key-" + id, id + ": independently encrypted key survives store reload");
        check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(AppDataPaths.Root, "q-secrets.dat"))).Contains("fixture-only-key"),
            "Credentials are not plaintext on disk");
        editor.QSelectedProvider = "openai";
        keyBox.Password = "unsaved-draft";
        editor.QSelectedProvider = "gemini";
        check(keyBox.Password.Length == 0 && secrets.Get("openai") == "fixture-only-key-openai", "Changing provider discards only unsaved input, not saved keys");
        await q.BeginAsync(QMode.Ask, settings.QSelectedProvider, settings.QSelectedModel, null);
        vm.IsExpanded = true; window.ApplySettings(); window.UpdateLayout();
        var selector = (System.Windows.Controls.ComboBox)window.FindName("QProviderSelector");
        var models = (System.Windows.Controls.ComboBox)window.FindName("QModelSelector");
        var prompt = (System.Windows.Controls.TextBox)window.FindName("QPromptBox");
        prompt.Text = "Unsent draft survives provider switches";
        foreach (var id in new[] { "openai", "gemini", "anthropic" })
        {
            selector.SelectedValue = id;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(vm.QSelectedProvider == id && editor.QSelectedProvider == id, id + ": panel selection reaches both view models");
            check(vm.QSelectedModel == id + "-default" && Equals(models.SelectedItem, vm.QSelectedModel), id + ": model dropdown updates to the correct provider");
            vm.QSelectedModel = id + "-custom"; vm.QReasoningEffort = "high";
            await vm.SubmitQAsync("Synthetic fixture question");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var fixture = (ProviderFixture)registry.Find(id)!;
            check(fixture.LastCredential == "fixture-only-key-" + id && fixture.LastModel == id + "-custom", id + ": request uses only that provider's key and model");
        }
        selector.SelectedValue = "gemini";
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(vm.QSelectedModel == "gemini-custom" && vm.QReasoningEffort == "high", "Switching back restores custom model and effort with live bindings");
        check(prompt.Text == "Unsent draft survives provider switches", "Provider switch preserves the unsent prompt");
        await persistence.SaveAsync(settings);
        var reloaded = await persistence.LoadAsync();
        check(reloaded.QSelectedProvider == "gemini" && reloaded.QSelectedModel == "gemini-custom"
            && reloaded.QProviderPreferences["openai"].Model == "openai-custom", "Provider and per-provider models persist across reload");
        check(!File.ReadAllText(persistence.SettingsPath).Contains("fixture-only-key"), "Settings export contains no API keys");
        ClickKey("RemoveQApiKey_Click");
        check(secrets.Get("gemini") is null && secrets.Get("openai") is not null && secrets.Get("anthropic") is not null,
            "Remove key affects only the selected provider");
        editor.QSelectedProvider = "codex"; check(!editor.QShowApiKey, "Codex keeps account sign-in instead of API keys");
        editor.QSelectedProvider = "ollama"; check(!editor.QShowApiKey, "Ollama remains keyless");
        editor.QSelectedProvider = "gemini";
        settings.QShortcuts = [new() { Name = "MCQ/OPEN", Prompt = "Fixture" }, new() { Name = "Explain", Prompt = "Fixture" }, new() { Name = "?", Prompt = "Fixture" }];
        vm.ApplySettings();
        void CheckToolbar()
        {
            var actions = (ScrollViewer)window.FindName("QQuickActions");
            var toolbar = (FrameworkElement)window.FindName("QToolbar");
            var top = actions.TranslatePoint(new System.Windows.Point(), toolbar);
            check(actions.ActualHeight >= 36 && top.Y + actions.ActualHeight <= toolbar.ActualHeight + 1,
                "Entire shortcut row fits inside the toolbar");
            var promptTop = prompt.TranslatePoint(new System.Windows.Point(), toolbar);
            check(top.Y + actions.ActualHeight + 10 <= promptTop.Y, "Shortcut buttons have clearance above the composer");
        }
        position.VerificationWorkArea = (1920, 1080); window.ApplySettings(); await capture("q-provider-selector");
        CheckToolbar();
        selector.IsDropDownOpen = true; window.UpdateLayout(); selector.IsDropDownOpen = false;
        position.VerificationWorkArea = (640, 480); window.ApplySettings(); await capture("q-provider-narrow");
        CheckToolbar();
        var shell = (FrameworkElement)window.FindName("GlassShell");
        var composer = prompt.TranslatePoint(new System.Windows.Point(), shell);
        check(composer.Y + prompt.ActualHeight <= shell.ActualHeight + 1, "Provider controls leave composer reachable on narrow screens");
        check(selector.ActualWidth > 0 && selector.TranslatePoint(new System.Windows.Point(), shell).X >= 0, "Provider selector remains reachable on narrow screens");
        settings.QShortcuts = Enumerable.Range(1, 12).Select(i => new QShortcut { Name = "Quick action " + i, Prompt = "Fixture" }).ToList();
        vm.ApplySettings(); window.UpdateLayout();
        var overflow = (ScrollViewer)window.FindName("QQuickActions");
        overflow.ScrollToRightEnd(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(overflow.HorizontalOffset > 0, "Overflow shortcuts are reachable by horizontal scrolling");
        CheckToolbar();
        settingsWindow.Close();
    }

    private static async Task VerifyComparisonAsync(AppSettings settings, SettingsService persistence, IslandViewModel vm,
        IslandWindow window, QSessionController session, IQSecretStore secrets, WindowPositionService position,
        Action<bool, string> check, Func<string, Task> capture, string output)
    {
        secrets.Set("gemini", "fixture-gemini"); secrets.Set("openai", "fixture-openai");
        settings.QShortcuts = [new() { Name = "Repeat activation", Prompt = "Explain binary search in plain English." }];
        settings.QAutoExpandIsland = false;
        foreach (var compare in new[] { false, true })
        {
            vm.QCompareEnabled = compare;
            vm.QSelectedProvider = "gemini"; vm.QCompareProvider = "openai";
            for (var repeat = 0; repeat < 6; repeat++)
            {
                if (repeat == 2) vm.ClearQ();
                await vm.StartQAsync(default, "Repeat activation");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                check(vm.QState == QRunState.Complete && (compare ? vm.QLeftCanCopy && vm.QRightCanCopy : vm.QCanCopyResponse),
                    $"Shortcut activation {repeat + 1} completes after asynchronous capture (Compare={compare})");
            }
            vm.ClearQ();
            var closedActivation = vm.StartQAsync(default, "Repeat activation");
            vm.ClearQ();
            await closedActivation;
            check(vm.QState == QRunState.Idle, $"Closing during capture does not reopen Q (Compare={compare})");
            var stoppedActivation = vm.StartQAsync(default, "Repeat activation");
            vm.CancelQ();
            await stoppedActivation;
            check(!vm.QCanStop && vm.QState == QRunState.Cancelled, $"Stopping capture exits busy state (Compare={compare})");
            var superseded = vm.StartQAsync(default, "Repeat activation");
            var newest = vm.StartQAsync(default, "Repeat activation");
            await Task.WhenAll(superseded, newest);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(vm.QState == QRunState.Complete, $"Rapid repeated activation uses the newest request (Compare={compare})");
            vm.ClearQ();
        }
        settings.QAutoExpandIsland = true;
        await session.BeginAsync(QMode.Ask, "openai", "openai-default", null);
        var toggle = (System.Windows.Controls.CheckBox)window.FindName("QCompareToggle");
        toggle.IsChecked = true;
        vm.QSelectedProvider = "gemini"; vm.QCompareProvider = "openai";
        var initialComparison = (QComparisonView)window.FindName("QComparisonPanel");
        var providerControl = (System.Windows.Controls.ComboBox)initialComparison.FindName("SecondProvider");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        providerControl.SelectedItem = vm.QCompareProviderOptions.First(p => p.Id == "anthropic");
        check(vm.QCompareProvider == "anthropic", "Second provider can be changed through its UI selector");
        providerControl.SelectedItem = vm.QCompareProviderOptions.First(p => p.Id == "openai");
        check(vm.QCompareEnabled && vm.QCompareProvider != vm.QSelectedProvider, "Compare toggle binds and selects two distinct providers");
        await vm.SubmitQAsync("Explain binary search in plain English.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(vm.QLeftLabel == "Gemini:" && vm.QRightLabel == "OAI:", "Collapsed labels match the selected providers");
        check(vm.QLeftCompact == "Halve the search each step." && vm.QRightCompact == "Check the middle, then repeat.", "Both actual answers appear in compact lines");
        check(vm.QState == QRunState.Complete && vm.QLeftCanCopy && vm.QRightCanCopy, "Both provider responses complete independently and are copyable");
        var originalRight = vm.QRightAnswer;
        await vm.RetryComparisonAsync(false);
        check(vm.QRightAnswer == originalRight, "Retrying one column preserves the other answer");
        settings.QShortcuts = [new() { Name = "MCQ/OPEN", Prompt = "fixture" }, new() { Name = "Explain", Prompt = "fixture" }];
        vm.ApplySettings(); position.VerificationWorkArea = (1920, 1080);
        vm.IsExpanded = true; window.ApplySettings(); await capture("compare-expanded");
        var comparison = (QComparisonView)window.FindName("QComparisonPanel");
        check(comparison.IsVisible, "Expanded comparison panel is visible");
        check(Equals(((System.Windows.Controls.ComboBox)comparison.FindName("SecondModel")).SelectedItem, vm.QCompareModel), "Second model stays selected after streaming");
        var secondProvider = (System.Windows.Controls.ComboBox)comparison.FindName("SecondProvider");
        check(secondProvider.SelectedItem is QProviderChoice choice && choice.Id == vm.QCompareProvider
            && ((TextBlock)secondProvider.Template.FindName("SelectedLabel", secondProvider)).Text == choice.Name,
            "Second provider template renders the actual selected provider name");
        var leftText = (TextBlock)comparison.FindName("LeftResponse");
        var rightText = (TextBlock)comparison.FindName("RightResponse");
        check(leftText.Text == vm.QLeftAnswer && rightText.Text == vm.QRightAnswer, "Expanded columns bind the two independent answers");
        var shell = (FrameworkElement)window.FindName("GlassShell");
        void Crop(string name)
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * 2), (int)Math.Ceiling(window.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render((Visual)window.Content);
            var origin = shell.TranslatePoint(new System.Windows.Point(), (UIElement)window.Content);
            var crop = new CroppedBitmap(bitmap, new Int32Rect((int)Math.Round(origin.X * 2), (int)Math.Round(origin.Y * 2),
                (int)Math.Round(shell.ActualWidth * 2), (int)Math.Round(shell.ActualHeight * 2)));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(crop));
            using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
        }
        Crop("compare-expanded-detail");
        vm.IsExpanded = false; window.ApplySettings(); await capture("compare-collapsed"); Crop("compare-collapsed-detail");
        var compact = (FrameworkElement)window.FindName("QCompactComparison");
        check(compact.IsVisible && compact.ActualHeight >= 46 && shell.ActualHeight >= 68, "Collapsed view fits two labeled lines without overlap");
        check(!vm.ShowCompactStatusContent, "Clock and normal status do not compete with comparison answers");
        check(shell.ActualWidth >= 480, "Compare mode reserves readable compact width without changing normal settings");
        vm.IsExpanded = true; position.VerificationWorkArea = (640, 480); window.ApplySettings(); await capture("compare-narrow");
        var scroller = (ScrollViewer)comparison.FindName("ComparisonScroller");
        scroller.ScrollToBottom(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await capture("compare-narrow-scrolled");
        check(scroller.VerticalOffset > 0, "Both stacked answers remain reachable on small screens");
        var prompt = (FrameworkElement)window.FindName("QPromptBox"); var point = prompt.TranslatePoint(new System.Windows.Point(), shell);
        check(point.Y + prompt.ActualHeight <= shell.ActualHeight + 1, "Compare composer remains on-screen on a small display");
        await persistence.SaveAsync(settings); var loaded = await persistence.LoadAsync();
        check(loaded.QCompareEnabled && loaded.QCompareProvider == "openai", "Compare mode and second provider persist");
        position.VerificationWorkArea = (1920, 1080);
        foreach (var hex in new[] { "#241C3C", "#F3D8B6" })
        {
            settings.Theme = ThemeMode.Custom; settings.CustomThemeColorHex = hex;
            vm.ApplySettings(); vm.IsExpanded = true; window.ApplySettings(); await capture("theme-compare-" + hex[1..]);
            var surfaceColor = ((SolidColorBrush)((Border)window.FindName("QShell")).Background).Color;
            check(surfaceColor.ToString() == "#FF" + hex[1..], "Q shell uses custom background " + hex);
            check(((SolidColorBrush)((Border)comparison.FindName("LeftCard")).Background).Color.ToString()
                == "#FF" + vm.QThemePalette.Card[1..], "Comparison cards follow custom theme " + hex);
            vm.IsExpanded = false; window.ApplySettings(); await capture("theme-compact-" + hex[1..]);
        }
        await persistence.SaveAsync(settings); loaded = await persistence.LoadAsync();
        check(loaded.Theme == ThemeMode.Custom && loaded.CustomThemeColorHex == "#F3D8B6", "Custom mode and color persist");
        settings.Theme = ThemeMode.Light; vm.ApplySettings(); vm.IsExpanded = true; window.ApplySettings(); await capture("theme-compare-light");
        check(!vm.IsDarkTheme && vm.QThemePalette == ThemePalette.Light, "Light mode remains available after Custom");
        settings.Theme = ThemeMode.Dark; vm.ApplySettings(); window.ApplySettings(); await capture("theme-compare-dark");
        check(vm.IsDarkTheme && vm.QThemePalette == ThemePalette.Dark, "Dark mode remains available after Custom");
        var picker = new ThemeColorPickerWindow("#241C3C") { ShowActivated = false, Opacity = 0 };
        picker.Show();
        var hexInput = (System.Windows.Controls.TextBox)picker.FindName("HexInput");
        hexInput.Text = "#BADHEX";
        check(!((System.Windows.Controls.Button)picker.FindName("ApplyButton")).IsEnabled, "Picker rejects invalid HEX without applying it");
        hexInput.Text = "#123D38";
        check(picker.SelectedColorHex == "#123D38", "Picker HEX updates the draft color");
        ((Slider)picker.FindName("Red")).Value = 128;
        check(picker.SelectedColorHex == "#803D38", "Picker RGB sliders update the HEX color");
        hexInput.Text = "#241C3C"; picker.UpdateLayout();
        var pickerBitmap = new RenderTargetBitmap((int)Math.Ceiling(picker.ActualWidth), (int)Math.Ceiling(picker.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        pickerBitmap.Render((Visual)picker.Content);
        var pickerEncoder = new PngBitmapEncoder(); pickerEncoder.Frames.Add(BitmapFrame.Create(pickerBitmap));
        using (var pickerFile = File.Create(Path.Combine(output, "custom-color-picker.png"))) pickerEncoder.Save(pickerFile);
        picker.Close();
        check(settings.CustomThemeColorHex == "#F3D8B6", "Closing color picker without Use color leaves saved color unchanged");
        using (var pixels = new System.Drawing.Bitmap(3, 3))
        {
            pixels.SetPixel(1, 2, System.Drawing.Color.FromArgb(18, 61, 56));
            check(ScreenColorPicker.Sample(pixels, new(-1920, -200, 3, 3), new(-1919, -198)) == "#123D38",
                "Eyedropper returns exact RGB at negative monitor coordinates");
        }
        vm.ClearQ(); check(!vm.IsQActive && vm.QLeftCompact == "Waiting for your question…", "New-question cleanup clears both answers");
        toggle.IsChecked = false; await session.BeginAsync(QMode.Ask, "gemini", "gemini-default", null);
        await vm.SubmitQAsync("Single question");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(vm.QSingleMode && vm.QResponse == "Halve the search each step.", "Single-provider mode still works after comparison");
    }

    private sealed class ProviderFixture(string id) : IQProvider
    {
        public QProviderInfo Info { get; } = new(id, id, QProviderCapabilities.Text | QProviderCapabilities.Streaming, id + "-default");
        public string? LastCredential { get; private set; }
        public string? LastModel { get; private set; }
        public Task<IReadOnlyList<QModelInfo>> GetModelsAsync(string? credential, CancellationToken cancellationToken, string? baseUrl = null)
            => Task.FromResult<IReadOnlyList<QModelInfo>>([]);
        public async IAsyncEnumerable<QStreamEvent> StreamAsync(QRequest request, string? credential, string? baseUrl,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastCredential = credential; LastModel = request.Model;
            yield return new QStreamEvent.Started();
            await Task.Yield();
            yield return new QStreamEvent.Text(id == "gemini" ? "Halve the search each step." : id == "openai" ? "Check the middle, then repeat." : "Local fixture response — no network request.");
            yield return new QStreamEvent.Completed();
        }
    }
    private sealed class FakeSecrets : IQSecretStore { public string? Get(string id) => null; public void Set(string id, string? value) { } public void Remove(string id) { } }
    private sealed class AsyncDisposeAdapter(CodexAppServerClient client) : IDisposable { public CodexAppServerClient Client => client; public void Dispose() => client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}
