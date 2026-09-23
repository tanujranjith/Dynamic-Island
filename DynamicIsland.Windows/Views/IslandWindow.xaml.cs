using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Windows.Infrastructure;
using DynamicIsland.Windows.Models;
using DynamicIsland.Windows.Services;
using DynamicIsland.Windows.ViewModels;
using DynamicIsland.Windows.Services.Q;
using Microsoft.Win32;

namespace DynamicIsland.Windows.Views;

public partial class IslandWindow : Window
{
    private readonly IslandViewModel _viewModel;
    private readonly WindowPositionService _position;
    private readonly SettingsService _settingsService;
    private readonly LoggingService _log;
    private readonly TimerAlarmViewModel _timerViewModel;
    private readonly ScreenContextService _qScreen;
    private readonly DispatcherTimer _collapseTimer = new();
    private readonly DispatcherTimer _timerPanelLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _sourceReady;
    private bool _initialLayoutStabilized;
    private bool _mainExpansionPending;
    private bool _dragging;
    private bool _dimmed;
    private bool _hiddenForFullscreen;
    private bool _settingsWindowOpen;
    private bool _scrubbing;
    private bool _timerPanelOpen;
    private bool _timerPanelHostShown;
    private bool _timerPanelHostAnimating;
    private bool _timerPanelClosing;
    private bool _timerPanelPinnedByClick;
    private int _timerPanelAnimationGeneration;
    private bool _suppressExpandedAnimation;
    private bool _liveTimerVisible;
    private bool _lastIsStatsStyle;
    private bool _lastShowAirPodsCard;
    private bool _lastShowWidgetsPanel;
    private bool _urgentAlertVisible;
    private Storyboard? _qThinkingAnimation;
    private bool _qFollowLatest = true;

    private const double TimerPanelNormalCornerRadius = 28d;
    private const double TimerPanelMorphCornerRadius = 300d;
    private const double TimerPanelCornerMorphDuration = 180d;

    private double TimerPanelWidth => WindowSizingPolicy.BoundedDimension(900, AvailableWorkArea.Width);
    // The timer editor contains a complete timer card plus a stopwatch card. A 520-DIP shell
    // leaves the editor viewport just short of the first card on smaller displays, making the
    // panel look like it is cut off even though its scrollbar is active. Use the available screen
    // height more effectively while retaining a small safety margin for the taskbar/edge.
    private double TimerPanelHeight => WindowSizingPolicy.BoundedDimension(600, AvailableWorkArea.Height, 24);
    private (double Width, double Height) AvailableWorkArea => _position.AvailableSize(this, _viewModel.Settings);
    private const double LiveTimerExtraHeight = 86d;

    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? OpenQSettingsRequested;
    public event EventHandler? OpenClipboardRequested;
    public event EventHandler? RecenterRequested;

    public IslandWindow(IslandViewModel viewModel, TimerAlarmViewModel timerViewModel, WindowPositionService position, SettingsService settingsService, LoggingService log, ScreenContextService qScreen)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        ApplyQTheme();
        _timerViewModel = timerViewModel;
        TimerPanelContent.DataContext = timerViewModel;
        LiveTimerStrip.DataContext = timerViewModel;
        _liveTimerVisible = timerViewModel.ShowLiveTimer;
        LiveTimerStrip.Visibility = _liveTimerVisible ? Visibility.Visible : Visibility.Collapsed;
        _lastIsStatsStyle = viewModel.IsStatsStyle;
        _lastShowAirPodsCard = viewModel.ShowAirPodsCard;
        _lastShowWidgetsPanel = viewModel.ShowWidgetsPanel;
        _position = position;
        _settingsService = settingsService;
        _log = log;
        _qScreen = qScreen;
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!_timerPanelOpen && !GlassShell.IsMouseOver && !_dragging && !_viewModel.PinExpanded) _viewModel.IsExpanded = false;
        };
        _timerPanelLeaveTimer.Tick += (_, _) => TryAutoCloseTimerPanel();
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            if (_viewModel.Settings.IdleDimming && !GlassShell.IsMouseOver) SetDimmed(true);
        };
        _fullscreenTimer.Tick += (_, _) => { _viewModel.InteractionProtected = _timerPanelOpen || IsKeyboardFocusWithin || Mouse.Captured is not null; CheckFullscreen(); CheckFollowScreen(); EnsureHealthy(); _qScreen.RememberForeground(new System.Windows.Interop.WindowInteropHelper(this).Handle); };
        _fullscreenTimer.Start();
        SourceInitialized += (_, _) =>
        {
            _sourceReady = true;
            _position.ApplyWindowStyles(this, _viewModel.Settings, compact: true);
            ApplyCaptureAffinity();
            ApplyLayout(animate: false);
            ApplyFrost();
        };
        Loaded += (_, _) =>
        {
            ApplyLayout(animate: false);
            ApplyFrost();
            ApplyRoundedShellClip();
            UpdateQPromptComposer();
            // Re-fit the pill whenever the expanded content's size changes (live CPU/RAM/weather/title text).
            ExpandedContent.SizeChanged += (_, _) => UpdateAutoGrow();
            QContent.SizeChanged += (_, _) => UpdateAutoGrow();
            StatsExpandedContent.SizeChanged += (_, _) => UpdateAutoGrow();
            StatsOverlay.SizeChanged += (_, _) => UpdateAutoGrow();
            GlassShell.SizeChanged += (_, _) => { ApplyRoundedShellClip(); UrgentAlertStrip.Margin = new Thickness(12, GlassShell.ActualHeight + 12, 12, 0); UrgentAlertStrip.MaxWidth = Math.Max(1, Math.Min(650, ActualWidth - 24)); };
            SyncUrgentAlertStrip(animate: false);
            // Let the first real render commit the compact HWND/content geometry before accepting
            // an expansion request. Without this warm-up, the first hover/click can race WPF's
            // initial measure and be applied as a layout snap instead of a visible morph.
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsLoaded) return;
                ApplyLayout(animate: false);
                UpdateLayout();
                _initialLayoutStabilized = true;
                var shouldExpand = _mainExpansionPending;
                _mainExpansionPending = false;
                if (shouldExpand && !_timerPanelOpen && !_viewModel.IsExpanded && GlassShell.IsMouseOver)
                    _viewModel.IsExpanded = true;
            }, DispatcherPriority.Render);
        };
        GotKeyboardFocus += (_, _) => _viewModel.InteractionProtected = true;
        LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() => _viewModel.InteractionProtected = _timerPanelOpen || IsKeyboardFocusWithin || Mouse.Captured is not null);
        LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(TryAutoCloseTimerPanel);
        GotMouseCapture += (_, _) => _viewModel.InteractionProtected = true;
        LostMouseCapture += (_, _) => _viewModel.InteractionProtected = _timerPanelOpen || IsKeyboardFocusWithin;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        _viewModel.NotificationGroupOpenRequested += OnNotificationGroupOpen;
        _timerViewModel.PropertyChanged += TimerViewModelOnPropertyChanged;
        _timerViewModel.ShowAlarmTabRequested += TimerViewModel_ShowAlarmTabRequested;
        SystemEvents.DisplaySettingsChanged += SystemEventsOnDisplaySettingsChanged;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => ApplyLayout(animate: false), DispatcherPriority.Loaded);
        SystemEvents.PowerModeChanged += SystemEventsOnPowerModeChanged;
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= SystemEventsOnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= SystemEventsOnPowerModeChanged;
            _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
            _viewModel.NotificationGroupOpenRequested -= OnNotificationGroupOpen;
            _timerViewModel.PropertyChanged -= TimerViewModelOnPropertyChanged;
            _timerViewModel.ShowAlarmTabRequested -= TimerViewModel_ShowAlarmTabRequested;
            _fullscreenTimer.Stop();
            _idleTimer.Stop();
            _timerPanelLeaveTimer.Stop();
            try { _qThinkingAnimation?.Remove(this); } catch { }
        };
    }

    public void ApplySettings()
    {
        _position.ApplyWindowStyles(this, _viewModel.Settings, _viewModel.IsCompact);
        ApplyCaptureAffinity();
        ApplyLayout(animate: false);
        ApplyRoundedShellClip();
        ApplyFrost();
        EnsureHealthy();
    }

    // WPF Border.ClipToBounds clips to a rectangle, not to CornerRadius. Without an explicit
    // rounded geometry, the media artwork below Q can show through the transparent corner pixels.
    // Apply the same physical clip to the entire shell so every presentation layer shares one mask.
    private void ApplyRoundedShellClip()
    {
        var width = GlassShell.ActualWidth;
        var height = GlassShell.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var requested = _viewModel.IslandCornerRadius.TopLeft;
        var radius = Math.Clamp(requested, 0d, Math.Min(width, height) / 2d);
        GlassShell.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
    }

    private void ApplyCaptureAffinity()
    {
        if (!_sourceReady) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero) return;

        // Keep the Island visible in normal screenshots during the Q stabilization period. The
        // Q capture service still targets the remembered foreground window and never relies on
        // this affinity flag for context isolation.
        var affinity = _viewModel.Settings.ShowIslandInScreenshots
            ? Interop.NativeMethods.WdaNone
            : Interop.NativeMethods.WdaExcludeFromCapture;
        Interop.NativeMethods.SetWindowDisplayAffinity(hwnd, affinity);
    }

    // Settings has its own live preview. The real transparent overlay must not cover it.
    public void SetSettingsWindowOpen(bool open)
    {
        _settingsWindowOpen = open;
        if (!_sourceReady) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero) return;
        Interop.NativeMethods.SetWindowPos(hwnd,
            open ? Interop.NativeMethods.HwndNoTopmost : Interop.NativeMethods.HwndTopmost,
            0, 0, 0, 0,
            Interop.NativeMethods.SwpNoMove | Interop.NativeMethods.SwpNoSize | Interop.NativeMethods.SwpNoActivate);
    }

    /// <summary>Force the island back into view (used by the tray "Recenter").</summary>
    public void ForceShow()
    {
        _fullscreenStreak = 0;
        _hiddenForFullscreen = false;
        Visibility = Visibility.Visible;
        GlassShell.BeginAnimation(OpacityProperty, null);
        GlassShell.Opacity = 1;
        _dimmed = false;
    }

    // Ensures no acrylic backdrop / window region is left applied. Real acrylic blur cannot be clipped
    // to the rounded pill on a layered (AllowsTransparency) window â€” it fills the whole rectangle and
    // reintroduces the square halo â€” so the frosting is done with WPF layers instead (see XAML).
    private void ApplyFrost()
    {
        if (!_sourceReady) return;
        _position.ApplyBackdropFrost(this, enable: false, _viewModel.IsDarkTheme);
    }

    private bool _lastQSurface, _lastQActive, _lastQComparison;
    private ThemePalette? _lastQPalette;
    private void ApplyQTheme()
    {
        var palette = _viewModel.QThemePalette;
        if (palette == _lastQPalette) return;
        _lastQPalette = palette;
        var values = new Dictionary<string, string>
        {
            ["QSurface"] = palette.Surface, ["QCard"] = palette.Card, ["QControl"] = palette.Control,
            ["QHover"] = palette.Hover, ["QBorder"] = palette.Border, ["QText"] = palette.Text,
            ["QMuted"] = palette.Muted, ["QAccent"] = palette.Accent, ["QSelected"] = palette.Selected
        };
        foreach (var (key, hex) in values)
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            brush.Freeze(); Resources[key] = brush;
        }
    }
    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IslandViewModel.QThemePalette) or nameof(IslandViewModel.IsDarkTheme)) ApplyQTheme();
        if (e.PropertyName == nameof(IslandViewModel.QCanStop)) UpdateQPromptComposer();
        if (e.PropertyName == nameof(IslandViewModel.AccentBrush))
        {
            // Timer tab visuals are set in code because the active state is switched
            // imperatively. Reapply them when artwork changes so the open panel follows
            // the same adaptive accent as the island and timer orb.
            if (_timerPanelOpen)
            {
                SetTimerTabVisual(TimerTabButton, TimerTabContent.Visibility == Visibility.Visible);
                SetTimerTabVisual(AlarmTabButton, AlarmTabContent.Visibility == Visibility.Visible);
                SetTimerTabVisual(StopwatchTabButton, StopwatchTabContent.Visibility == Visibility.Visible);
            }
        }
        else if (e.PropertyName == nameof(IslandViewModel.IsExpanded))
        {
            if (_suppressExpandedAnimation) return;
            _position.ApplyWindowStyles(this, _viewModel.Settings, _viewModel.IsCompact);
            // Dynamic rows (AirPods, timers, quotes) can appear while the island is compact.
            // Recompute the host HWND before expanding; animating only the inner shell leaves the
            // transparent window at its old compact-state height and clips the bottom of the pill.
            ApplyLayout(animate: true);
        }
        else if (e.PropertyName == nameof(IslandViewModel.IsStatsStyle) || e.PropertyName == nameof(IslandViewModel.IsAppleStyle))
        {
            // Hardened: only re-layout when the actual visual mode changed. Spurious
            // PropertyChanged("IsStatsStyle") from unrelated media/battery/audio updates
            // must NOT restart the pill animation (previously caused visible flicker).
            var isStats = _viewModel.IsStatsStyle;
            if (isStats == _lastIsStatsStyle) return;
            _lastIsStatsStyle = isStats;
            if (_timerPanelOpen) return;
            if (_viewModel.IsExpanded)
            {
                // Visual mode changes alter both the active content and its minimum safe height.
                // Re-run the real layout path immediately so Stats never inherits Apple dimensions.
                ApplyLayout(animate: true);
            }
            else
            {
                // Compact visual mode must update immediately without a full morph.
                ApplyVisualMode();
            }
        }
        else if (e.PropertyName == nameof(IslandViewModel.IsQActive) || e.PropertyName == nameof(IslandViewModel.ShowQSurface) || e.PropertyName == nameof(IslandViewModel.QCompareEnabled))
        {
            if (_timerPanelOpen) return;
            if (_lastQSurface == _viewModel.ShowQSurface && _lastQActive == _viewModel.IsQActive && _lastQComparison == _viewModel.QCompareEnabled) return;
            _lastQSurface = _viewModel.ShowQSurface; _lastQActive = _viewModel.IsQActive; _lastQComparison = _viewModel.QCompareEnabled;
            ApplyVisualMode();
            ApplyLayout(animate: false);
            if (_viewModel.ShowQSurface && _viewModel.IsExpanded)
            {
                _qFollowLatest = true;
                Dispatcher.BeginInvoke(() =>
                {
                    QTranscriptScroll.ScrollToEnd();
                    QPromptBox.Focus();
                    Keyboard.Focus(QPromptBox);
                });
            }
        }
        else if (e.PropertyName == nameof(IslandViewModel.ShowAirPodsCard))
        {
            // AirPods battery/name properties are raised for every BLE packet. Only the card's
            // visibility changes the expanded layout; content updates are handled by bindings.
            var showAirPodsCard = _viewModel.ShowAirPodsCard;
            if (showAirPodsCard == _lastShowAirPodsCard) return;
            _lastShowAirPodsCard = showAirPodsCard;
            if (_timerPanelOpen) return;
            ApplyVisualMode();
            // Keep the transparent host ready even when the card connects while compact. The next
            // expansion must not inherit the smaller host height that was calculated at startup.
            ApplyLayout(animate: _viewModel.IsExpanded);
            Dispatcher.BeginInvoke(() => UpdateLiveWidgetsOverflow(LiveWidgetsScroller), DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(IslandViewModel.ShowWidgetsPanel))
        {
            var showWidgetsPanel = _viewModel.ShowWidgetsPanel;
            if (showWidgetsPanel == _lastShowWidgetsPanel) return;
            _lastShowWidgetsPanel = showWidgetsPanel;
            if (_timerPanelOpen) return;
            ApplyVisualMode();
            ApplyLayout(animate: _viewModel.IsExpanded);
            Dispatcher.BeginInvoke(() => UpdateLiveWidgetsOverflow(LiveWidgetsScroller), DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(IslandViewModel.IsAirPodsBannerActive))
        {
            if (_viewModel.IsAirPodsBannerActive) PlayAirPodsConnectionIntro();
            else ReturnFromAirPodsCompactBanner();
        }
        else if (e.PropertyName == nameof(IslandViewModel.HasUrgentAlert))
        {
            // Timer completion notifications arrive on the same cadence as timer ticks. Defer
            // the visual transition to Render and guard the state so the entrance never restarts.
            Dispatcher.BeginInvoke(() => SyncUrgentAlertStrip(animate: true), DispatcherPriority.Render);
        }
        else if (e.PropertyName == nameof(IslandViewModel.ShowTimerOrb))
        {
            // The privacy dot must move with the timer activity orb instead of landing on top of it.
            if (!_timerPanelOpen && !_viewModel.IsExpanded)
            {
                var metrics = Metrics();
                UpdateTimerOrbLayout(metrics.cW, metrics.cH);
                UpdatePrivacyIndicatorLayout(metrics.cW, metrics.cH, animate: true);
            }
        }
        else if (e.PropertyName == nameof(IslandViewModel.QState) ||
                 e.PropertyName == nameof(IslandViewModel.QShowInlineThinking) ||
                 e.PropertyName == nameof(IslandViewModel.QResponse) ||
                 e.PropertyName == nameof(IslandViewModel.QPromptText))
        {
            if (e.PropertyName == nameof(IslandViewModel.QState) || e.PropertyName == nameof(IslandViewModel.QShowInlineThinking))
                UpdateQThinkingAnimation();
            ScheduleQTranscriptScroll();
        }
        else if (e.PropertyName == nameof(IslandViewModel.PrivacySeq))
        {
            Dispatcher.BeginInvoke(PlayPrivacyDotIntro, DispatcherPriority.Render);
        }
        else if (e.PropertyName == nameof(IslandViewModel.BannerSeq))
        {
            // BannerSeq increments once per new Windows notification or volume warning,
            // so the entrance plays exactly once and never restarts mid-display.
            PlayNotificationIntro();
        }
    }

    private void TimerViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TimerAlarmViewModel.ShowLiveTimer)) return;
        var visible = _timerViewModel.ShowLiveTimer;
        if (visible == _liveTimerVisible) return;

        _liveTimerVisible = visible;
        LiveTimerStrip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_viewModel.IsExpanded && !_timerPanelOpen)
            ApplyLayout(animate: true);
    }

    private Storyboard? _notifIntro;

    private void SyncUrgentAlertStrip(bool animate)
    {
        var shouldShow = _viewModel.HasUrgentAlert;
        if (shouldShow == _urgentAlertVisible)
        {
            if (!shouldShow) UrgentAlertStrip.Visibility = Visibility.Collapsed;
            return;
        }

        _urgentAlertVisible = shouldShow;
        UrgentAlertStrip.BeginAnimation(UIElement.OpacityProperty, null);
        UrgentAlertScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        UrgentAlertScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        UrgentAlertTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        if (!shouldShow)
        {
            if (!animate || _viewModel.IsReducedMotion)
            {
                UrgentAlertStrip.Opacity = 0d;
                UrgentAlertStrip.Visibility = Visibility.Collapsed;
                return;
            }

            UrgentAlertStrip.Opacity = 1d;
            var fadeOut = new DoubleAnimation(0d, TimeSpan.FromMilliseconds(180));
            fadeOut.Completed += (_, _) =>
            {
                if (_viewModel.HasUrgentAlert) return;
                UrgentAlertStrip.Visibility = Visibility.Collapsed;
                UrgentAlertScale.ScaleX = UrgentAlertScale.ScaleY = 1d;
                UrgentAlertTranslate.Y = 0d;
            };
            UrgentAlertStrip.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            UrgentAlertTranslate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0d, -6d, TimeSpan.FromMilliseconds(180))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            return;
        }

        UrgentAlertStrip.Visibility = Visibility.Visible;
        if (!animate || _viewModel.IsReducedMotion)
        {
            UrgentAlertStrip.Opacity = 1d;
            UrgentAlertScale.ScaleX = UrgentAlertScale.ScaleY = 1d;
            UrgentAlertTranslate.Y = 0d;
            return;
        }

        UrgentAlertStrip.Opacity = 0d;
        UrgentAlertScale.ScaleX = UrgentAlertScale.ScaleY = 0.94d;
        UrgentAlertTranslate.Y = -10d;
        UrgentAlertStrip.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(190))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        var reveal = TimeSpan.FromMilliseconds(260);
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        UrgentAlertScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94d, 1d, reveal) { EasingFunction = ease });
        UrgentAlertScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94d, 1d, reveal) { EasingFunction = ease });
        UrgentAlertTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10d, 0d, reveal) { EasingFunction = ease });
    }

    private void PlayPrivacyDotIntro()
    {
        if (!_viewModel.ShowPrivacyInUse) return;

        if (_viewModel.IsExpanded)
        {
            PlayExpandedPrivacyActivityIntro();
            return;
        }

        PrivacyDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PrivacyDotScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        if (_viewModel.IsReducedMotion)
        {
            PrivacyDotScale.ScaleX = PrivacyDotScale.ScaleY = 1d;
            return;
        }

        var reveal = TimeSpan.FromMilliseconds(260);
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
        PrivacyDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.25d, 1d, reveal) { EasingFunction = spring });
        PrivacyDotScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.25d, 1d, reveal) { EasingFunction = spring });
    }

    private void PlayExpandedPrivacyActivityIntro()
    {
        ExpandedPrivacyActivity.BeginAnimation(UIElement.OpacityProperty, null);
        ExpandedPrivacyScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ExpandedPrivacyScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ExpandedPrivacyTranslate.BeginAnimation(TranslateTransform.XProperty, null);

        if (_viewModel.IsReducedMotion)
        {
            ExpandedPrivacyActivity.Opacity = 1d;
            ExpandedPrivacyScale.ScaleX = ExpandedPrivacyScale.ScaleY = 1d;
            ExpandedPrivacyTranslate.X = 0d;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(260);
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        ExpandedPrivacyActivity.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(180)));
        ExpandedPrivacyScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.96d, 1d, duration) { EasingFunction = ease });
        ExpandedPrivacyScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.96d, 1d, duration) { EasingFunction = ease });
        ExpandedPrivacyTranslate.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(12d, 0d, duration) { EasingFunction = ease });
    }

    private void PlayAirPodsConnectionIntro()
    {
        AirPodsImageScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        AirPodsImageScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AirPodsImageTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        AirPodsImageTranslation.BeginAnimation(TranslateTransform.YProperty, null);
        AirPodsCompactBanner.BeginAnimation(UIElement.OpacityProperty, null);
        AirPodsConnectionImage.BeginAnimation(UIElement.OpacityProperty, null);
        AirPodsImageScale.ScaleX = AirPodsImageScale.ScaleY = 0.82d;
        AirPodsImageTranslation.X = -6d;
        AirPodsImageTranslation.Y = 2d;
        AirPodsCompactBanner.Opacity = 0d;
        AirPodsConnectionImage.Opacity = 0d;

        if (_viewModel.IsReducedMotion || _viewModel.IsExpanded || _timerPanelOpen)
        {
            AirPodsImageScale.ScaleX = AirPodsImageScale.ScaleY = 1d;
            AirPodsImageTranslation.X = AirPodsImageTranslation.Y = 0d;
            AirPodsCompactBanner.Opacity = 1d;
            AirPodsConnectionImage.Opacity = 1d;
            return;
        }

        var compactWidth = Metrics().cW;
        var connectionWidth = Math.Clamp(compactWidth + 92d, 300d, 360d);
        GlassShell.BeginAnimation(WidthProperty, null);
        GlassShell.BeginAnimation(WidthProperty, new DoubleAnimation(connectionWidth, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 }
        });
        AirPodsCompactBanner.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(160)));
        AirPodsConnectionImage.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(180)));
        AirPodsImageScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.82d, 1d, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.16 }
        });
        AirPodsImageScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.82d, 1d, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.16 }
        });
        AirPodsImageTranslation.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-6d, 0d, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        AirPodsImageTranslation.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(2d, 0d, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void ReturnFromAirPodsCompactBanner()
    {
        if (_viewModel.IsExpanded || _timerPanelOpen) return;
        var compactWidth = Metrics().cW;
        GlassShell.BeginAnimation(WidthProperty, new DoubleAnimation(compactWidth, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // The notification "combo" entrance: pop + blur-in, spring scale, then an accent glow pulse and a
    // light sweep across the card (see the NotifIntro storyboard in IslandWindow.xaml).
    private void PlayNotificationIntro()
    {
        // Tint the glow and the sweep to the current accent — the same colour as the app-name label.
        var accent = (_viewModel.AccentBrush as SolidColorBrush)?.Color ?? System.Windows.Media.Color.FromRgb(0x5A, 0xA7, 0xFF);
        NotifGlow.Color = accent;
        NotifSweepStop.Color = System.Windows.Media.Color.FromArgb(0x70, accent.R, accent.G, accent.B);

        if (_viewModel.IsReducedMotion)
        {
            // Reduced motion: skip the animation and show the banner settled with a soft static glow.
            _notifIntro?.Stop(this);
            NotifScale.ScaleX = NotifScale.ScaleY = 1d;
            NotifBanner.Opacity = 1d;
            NotifBlur.Radius = 0d;
            NotifSweepRect.Opacity = 0d;
            NotifGlow.BlurRadius = 16d;
            NotifGlow.Opacity = 0.45d;
            return;
        }

        _notifIntro ??= (Storyboard)Resources["NotifIntro"];
        _notifIntro.Begin(this, isControllable: true);
    }

    // The window is a large, transparent, click-through canvas; only the centred pill is hit-testable
    // and animates. A generous fixed canvas lets the pill auto-grow to fit big text without resizing the
    // HWND (and the area around the pill stays click-through). (pillCompact, pillExpanded, window)
    // Quotes add a footer only when enabled. Keep the original canvas otherwise, because a transparent
    // WPF window has to redraw its whole canvas while the island animates.
    private const double CanvasW = 1200d, BaseCanvasH = 330d, QuoteFooterHeight = 56d;
    private const double QSurfaceExtraHeight = 220d;
    // The Stats dashboard is taller than the Apple activity deck (album art plus three
    // stacked tiles instead of one compact row). Reserve this space even when the user
    // turns auto-grow off so the bottom of the dashboard can never be clipped.
    private const double StatsDashboardExtraHeight = 64d;
    private (double cW, double cH, double eW, double eH, double winW, double winH) Metrics()
    {
        var quoteSpace = _viewModel.ShowQuoteInExpanded ? QuoteFooterHeight : 0d;
        var timerSpace = _liveTimerVisible ? LiveTimerExtraHeight : 0d;
        var statsSpace = _viewModel.IsStatsStyle ? StatsDashboardExtraHeight : 0d;
        var qSpace = _viewModel.ShowQSurface ? QSurfaceExtraHeight : 0d;
        // AirPods and widgets now share one 64-DIP accessory lane.
        var accessorySpace = _viewModel.ShowAirPodsCard || _viewModel.ShowWidgetsPanel ? 76d : 0d;
        // Compact dimensions are user-controlled and deliberately independent from the expanded
        // canvas.  Previously this method ignored IslandWidth/IslandHeight and used a preset for
        // both states, so adjusting the mini island could distort the expanded layout.
        var compactWidth = Math.Clamp(_viewModel.Settings.IslandWidth, 72d, 360d);
        var compactHeight = Math.Clamp(_viewModel.Settings.IslandHeight, 38d, 90d);
        if (_viewModel.QShowCompactComparison)
        {
            compactWidth = Math.Min(Math.Max(compactWidth, 480), Math.Max(72, AvailableWorkArea.Width - 24));
            compactHeight = Math.Max(compactHeight, 68);
        }
        var (expandedWidth, expandedHeight) = _viewModel.Settings.IslandSize switch
        {
            // The media header, transport controls, and the live-activity cards need 260px+
            // after their outer margins.  The smaller values clipped the entire bottom row,
            // making RAM/network/battery appear to have disappeared.
            IslandSize.Compact => (820d, 264d + quoteSpace + timerSpace + statsSpace + qSpace + accessorySpace),
            IslandSize.Large => (1000d, 322d + quoteSpace + timerSpace + statsSpace + qSpace + accessorySpace),
            _ => (900d, 292d + quoteSpace + timerSpace + statsSpace + qSpace + accessorySpace)
        };
        if (_viewModel.QShowCompactComparison) { expandedWidth = Math.Max(expandedWidth, 1100); expandedHeight = Math.Max(expandedHeight, 700); }
        // The transparent HWND must be at least as tall as the pill, otherwise WPF clips
        // a correctly measured Stats dashboard at the canvas boundary.
        var windowHeight = Math.Max(BaseCanvasH + quoteSpace + timerSpace, expandedHeight + 40d);
                var available = AvailableWorkArea;
        expandedWidth = WindowSizingPolicy.BoundedDimension(expandedWidth, available.Width);
        expandedHeight = WindowSizingPolicy.BoundedDimension(expandedHeight, available.Height, 76);
        return (compactWidth, compactHeight, expandedWidth, expandedHeight, Math.Min(CanvasW, available.Width), Math.Min(available.Height, Math.Max(windowHeight, expandedHeight + 64)));
    }

    // When auto-grow is on, size the expanded pill to its content so nothing clips.
    private (double W, double H) ExpandedPillSize()
    {
        var m = Metrics();
        // Q has a fixed interaction frame. Letting the answer text determine the whole pill height
        // pushes the composer and footer below the shell after a long response. The response card
        // owns a ScrollViewer, so keep the prompt/actions/footer pinned in the reserved Q height.
        if (_viewModel.ShowQSurface) return (m.eW, m.eH);
        try
        {
            // Measure the content unconstrained so trimmed/async text reports its true natural size.
            // StatsOverlay lives inside ExpandedContent, so this measures the exact surface the
            // user sees in either visual style.  The old detached Stats grid is intentionally no
            // longer part of sizing or visibility decisions.
            var content = _viewModel.ShowQSurface ? QContent : ExpandedContent;
            content.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var d = content.DesiredSize;
            // Auto-grow off keeps the configured width, but it must never suppress mandatory
            // vertical growth. Dynamic rows (AirPods, timer, quotes) can otherwise be arranged
            // below the fixed shell and get cut in half even though the host window is tall enough.
            var w = _viewModel.Settings.AutoGrowPill
                ? Math.Clamp(d.Width + 2, m.eW, Math.Max(m.eW, Math.Min(CanvasW - 24, AvailableWorkArea.Width - 24)))
                : m.eW;
            var h = WindowSizingPolicy.AntiClippingDimension(m.eH, d.Height + 2, m.winH - 12);
            return (WindowSizingPolicy.BoundedDimension(w, AvailableWorkArea.Width), WindowSizingPolicy.BoundedDimension(h, AvailableWorkArea.Height, 76));
        }
        catch { return (m.eW, m.eH); }
    }

    // Snap the expanded pill to fit its current content (called as content text changes size).
    private bool _inAutoGrow;
    private bool _expandAnimating;
    private int _pillAnimationGeneration;
    private bool _loggedAnimationStart;
    private bool _loggedAnimationCompletion;
    private void UpdateAutoGrow()
    {
        if (_inAutoGrow || _expandAnimating || !_viewModel.IsExpanded || !_viewModel.Settings.AutoGrowPill) return;
        _inAutoGrow = true;
        try
        {
            var (w, h) = ExpandedPillSize();
            if (Math.Abs(GlassShell.Width - w) > 0.5 || Math.Abs(GlassShell.Height - h) > 0.5)
            {
                // Clear any held open-animation values so the new size actually takes effect.
                GlassShell.BeginAnimation(WidthProperty, null);
                GlassShell.BeginAnimation(HeightProperty, null);
                GlassShell.Width = w;
                GlassShell.Height = h;
                SizeExpandedViewport(w, h);
            }
        }
        finally { _inAutoGrow = false; }
    }

    private void ApplyLayout(bool animate)
    {
        // Keep the expanded tree mounted so it can be measured, but do not let it render at full
        // opacity before AnimatePill has installed its fade clock. That one-frame reveal was the
        // flash seen at the very beginning of an expansion.
        var deferExpandedReveal = animate && !_viewModel.IsReducedMotion && _viewModel.IsExpanded;
        var deferCompactReveal = animate && !_viewModel.IsReducedMotion && !_viewModel.IsExpanded;
        ApplyVisualMode(deferExpandedReveal, deferCompactReveal);
        var m = Metrics();
        UpdateTimerOrbLayout(m.cW, m.cH);
        UpdatePrivacyIndicatorLayout(m.cW, m.cH, animate);
        var desiredWindowHeight = _timerPanelOpen ? Math.Max(m.winH, TimerPanelHeight + 20d) : m.winH;
        if (Math.Abs(Width - m.winW) > 0.5 || Math.Abs(Height - desiredWindowHeight) > 0.5)
        {
            Width = m.winW;
            Height = desiredWindowHeight;
        }
        // The transparent HWND stays anchored to the user's main-island position even while the
        // timer host is open. The timer surface is a sibling overlay; using its width here moves
        // the main island for TopLeft/TopRight layouts and makes the overlay look like its owner.
        var targetPillWidth = _viewModel.IsExpanded ? ExpandedPillSize().W : m.cW;
        var targetPillHeight = _viewModel.IsExpanded ? ExpandedPillSize().H : m.cH;
        _position.PositionInitial(this, _viewModel.Settings,
            _timerPanelOpen ? Math.Max(targetPillWidth, TimerPanelWidth) : targetPillWidth,
            _timerPanelOpen ? Math.Max(targetPillHeight, TimerPanelHeight) : targetPillHeight);
        // The timer host may be opening or closing while the main shell is changing state. Its
        // overlay owns that transition, so the main shell must settle synchronously and never
        // inherit the timer orb's geometry.
        AnimatePill(animate && !_timerPanelOpen);
        ApplyTimerPanelHostLayout(animate);
    }

    private void UpdateTimerOrbLayout(double compactWidth, double compactHeight)
    {
        var size = Math.Clamp(compactHeight, 38d, 62d);
        CompactTimerOrb.Width = size;
        CompactTimerOrb.Height = size;
        var airPodsGap = _viewModel.IsAirPodsBannerActive ? 26d : 0d;
        TimerOrbTranslate.X = compactWidth / 2d + 8d + size / 2d + airPodsGap;
    }

    private void UpdatePrivacyIndicatorLayout(double compactWidth, double compactHeight, bool animate)
    {
        // Privacy belongs to the compact island/orb composition, never to the timer panel's
        // expanded bounds. Keeping this coordinate space stable prevents the dot from flying to
        // the panel's far edge and then returning when TimerEditorOpen changes the orb visibility.
        var (activeWidth, activeHeight) = _viewModel.IsExpanded
            ? ExpandedPillSize()
            : (compactWidth, compactHeight);

        // Follow the shell's trailing edge. Expanded mode keeps the orb near the top controls.
        var targetX = activeWidth / 2d + 12d;
        if (!_viewModel.IsExpanded && !_timerPanelOpen && _viewModel.ShowTimerOrb)
        {
            var timerOrbSize = Math.Clamp(compactHeight, 38d, 62d);
            var timerOrbRight = TimerOrbTranslate.X + timerOrbSize / 2d;
            // Place privacy to the right of the timer orb with a small optical gap.
            targetX = timerOrbRight + 6d + 9d;
        }
        var targetY = _viewModel.IsExpanded
            ? 12d
            : Math.Max(0d, (activeHeight - 18d) / 2d);

        PrivacyDotTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        PrivacyDotTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        // Position changes are committed immediately. PrivacySeq owns the dot's entrance motion;
        // animating its location during every layout pass caused the visible far-right flicker.
        PrivacyDotTranslate.X = targetX;
        PrivacyDotTranslate.Y = targetY;
    }

    private void ApplyVisualMode(bool deferExpandedReveal = false, bool deferCompactReveal = false)
    {
        var apple = _viewModel.IsAppleStyle;
        var expanded = _viewModel.IsExpanded;
        var q = _viewModel.ShowQSurface;
        ExpandedContent.Margin = q ? new Thickness(0) : new Thickness(28, 22, 28, 22);
        // Give Q its own bounded layout viewport. Without an explicit height, the parent Grid can
        // arrange the response card at its natural text height and clip the prompt/footer below it.
        var qHeight = q ? Metrics().eH : double.NaN;
        QContent.Height = qHeight;
        QContent.MaxHeight = q ? qHeight : double.PositiveInfinity;
        QContent.VerticalAlignment = q ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        SetModeContent(CompactContent, !expanded && (apple || _viewModel.IsEdgePill || _viewModel.IsHolePunchMode || _viewModel.QShowCompactComparison), deferCompactReveal);
        // The full Apple activity deck is the stable shared expanded surface. The compact Stats
        // layout still provides the dense glanceable alternative, while this prevents Stats mode
        // from ever opening into an empty shell during rapid hover/settings transitions.
        // TimerPanelContent is deliberately not part of this tree. The timer has its own sibling
        // host so its transform and hit testing can never change the main island's geometry.
        ExpandedViewport.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ExpandedContent.Width = Math.Max(600, Metrics().eW - (q ? 0 : 56));
        ExpandedContent.Height = q ? Metrics().eH : double.NaN;
        SetModeContent(ExpandedContent, expanded, deferExpandedReveal);
        SetModeContent(QContent, expanded && q);
        SetModeContent(StatsCompactContent, !apple && !_viewModel.IsEdgePill && !_viewModel.IsHolePunchMode && !expanded && !_viewModel.QShowCompactComparison, deferCompactReveal);
        SetModeContent(StatsExpandedContent, false);
        SetModeContent(StatsOverlay, !apple && expanded);
    }

    // Switching visual modes while the shell is expanded previously changed Visibility only. If an
    // in-flight transition had left the new content at opacity 0, the result was a large blank pill.
    // Reset the full presentation state together so either layout is always immediately usable.
    private static void SetModeContent(UIElement content, bool visible, bool deferReveal = false)
    {
        content.BeginAnimation(UIElement.OpacityProperty, null);
        content.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        content.Opacity = visible && !deferReveal ? 1d : 0d;
        content.IsHitTestVisible = visible && !deferReveal;
    }

    private void SetPillAnimationCache(bool enabled, UIElement compactContent)
    {
        // Width/height animation still resizes the shell, but caching the already-laid-out
        // content prevents repainting every timer control and text glyph on each frame.
        ExpandedContent.CacheMode = enabled ? new BitmapCache() : null;
        compactContent.CacheMode = enabled ? new BitmapCache() : null;
        if (!enabled)
        {
            CompactContent.CacheMode = null;
            StatsCompactContent.CacheMode = null;
        }
    }

    private Grid ActiveCompactContent => _viewModel.IsStatsStyle ? StatsCompactContent : CompactContent;
    private Grid ActiveExpandedContent => _viewModel.ShowQSurface ? QContent : ExpandedContent;

    // Lay out the expanded subtree once at its destination size. The shell's rounded clip
    // reveals it during the morph without resizing a ScrollViewer and all its children per frame.
    // Hidden scrollbars retain wheel/keyboard scrolling when a small monitor constrains the shell.
    private void SizeExpandedViewport(double width, double height)
    {
        ExpandedViewport.Width = width;
        ExpandedViewport.Height = height;
    }

    // The drop shadow is the most expensive part of each software-composited animation frame. Retain it
    // visually, but use WPF's lower-cost rendering mode until the zoom lands.
    private System.Windows.Media.Effects.DropShadowEffect? _animatedShellShadow;
    private System.Windows.Media.Effects.RenderingBias _savedShellShadowBias;
    private void UseFastPillShadow()
    {
        if (_animatedShellShadow is not null) return;
        if (GlassShell.Effect is not System.Windows.Media.Effects.DropShadowEffect shadow || shadow.IsFrozen) return;
        _animatedShellShadow = shadow;
        _savedShellShadowBias = shadow.RenderingBias;
        shadow.RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance;
    }

    private void RestorePillShadow()
    {
        if (_animatedShellShadow is null) return;
        _animatedShellShadow.RenderingBias = _savedShellShadowBias;
        _animatedShellShadow = null;
    }

    // Animate the clipped shell itself while the expanded viewport stays pinned at its destination
    // size. Scaling a transparent WPF Border exposed a full-width composition frame on some GPUs
    // before its transform clock was applied, which read as a distracting flash during expansion.
    private void AnimatePill(bool animate)
    {
        var generation = ++_pillAnimationGeneration;
        var m = Metrics();
        var expanded = _viewModel.IsExpanded;
        var reduced = _viewModel.Settings.AnimationIntensity == AnimationIntensity.Reduced;
        // Make the destination surface measurable before computing its safe expanded bounds.
        // A Collapsed WPF element reports no desired size, which previously defeated the
        // anti-clipping calculation on the first expansion. Keep it transparent until its fade
        // animation is ready, so WPF never presents a fully-visible intermediate frame.
        ApplyVisualMode(animate && !reduced && expanded, animate && !reduced && !expanded);
        var (eW, eH) = ExpandedPillSize();
        var targetW = _viewModel.IsExpanded ? eW : m.cW;
        var targetH = _viewModel.IsExpanded ? eH : m.cH;
        if (_viewModel.IsExpanded) SizeExpandedViewport(targetW, targetH);
        var compactContent = ActiveCompactContent;
        var expandedContent = ActiveExpandedContent;
        var currentWidth = Math.Max(1d, GlassShell.ActualWidth);
        var currentHeight = Math.Max(1d, GlassShell.ActualHeight);

        if (!animate || reduced)
        {
            SetPillAnimationCache(enabled: false, compactContent);
            GlassShell.BeginAnimation(WidthProperty, null);
            GlassShell.BeginAnimation(HeightProperty, null);
            GlassShell.BeginAnimation(UIElement.OpacityProperty, null);
            PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PillScale.ScaleX = PillScale.ScaleY = 1d;
            GlassShell.Width = targetW;
            GlassShell.Height = targetH;
            GlassShell.Opacity = 1d;
            ApplyVisualMode();
            _expandAnimating = false;
            RestorePillShadow();
            if (_viewModel.IsExpanded && !_timerPanelOpen) UpdateAutoGrow();
            return;
        }

        // Expansion covers the full compact-to-editor distance. Give it enough time for the
        // compositor to render a visibly fluid sequence of frames instead of making the first
        // few frames carry most of a 900x600 resize. Collapse stays quick and familiar.
        var duration = expanded
            ? TimeSpan.FromMilliseconds(_viewModel.Settings.AnimationIntensity == AnimationIntensity.Expressive ? 420d : 340d)
            : TimeSpan.FromMilliseconds(_viewModel.Settings.AnimationIntensity == AnimationIntensity.Expressive ? 280d : 210d);
        // Keep the shell dimensions monotonic. A BackEase on the actual width made the island
        // overshoot horizontally, producing the wide flash visible in the frame captures before
        // it settled back to the intended size. Apple-like motion here comes from a fast ease-out,
        // while the content fade provides the softer handoff.
        IEasingFunction ease = expanded
            ? new QuinticEase { EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseInOut };

        _expandAnimating = true;
        SetPillAnimationCache(enabled: true, compactContent);
        UseFastPillShadow();
        if (!_loggedAnimationStart)
        {
            _loggedAnimationStart = true;
            _log.Info($"Real island animation started: expanded={expanded}, from={GlassShell.ActualWidth:0.#}x{GlassShell.ActualHeight:0.#}, target={targetW:0.#}x{targetH:0.#}, duration={duration.TotalMilliseconds:0}ms");
        }

        // Freeze an interrupted morph at its currently rendered bounds, then continue from there.
        // This keeps rapid pointer reversals smooth without a scale reset or a full-width flash.
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PillScale.ScaleX = PillScale.ScaleY = 1d;
        GlassShell.BeginAnimation(WidthProperty, null);
        GlassShell.BeginAnimation(HeightProperty, null);
        GlassShell.BeginAnimation(UIElement.OpacityProperty, null);
        GlassShell.Width = currentWidth;
        GlassShell.Height = currentHeight;
        GlassShell.Opacity = 1d;
        var widthAnimation = new DoubleAnimation(targetW, duration) { EasingFunction = ease };
        var heightAnimation = new DoubleAnimation(targetH, duration) { EasingFunction = ease };
        heightAnimation.Completed += (_, _) => FinishPillMorph(generation, targetW, targetH, expanded);
        GlassShell.BeginAnimation(WidthProperty, widthAnimation);
        GlassShell.BeginAnimation(HeightProperty, heightAnimation);

        if (expanded)
        {
            // Preserve a compact visual while the rounded clip begins widening, then hand off
            // promptly to the expanded surface. This avoids a black or full-width flash.
            compactContent.Visibility = Visibility.Visible;
            compactContent.Opacity = 1d;
            compactContent.IsHitTestVisible = false;
            // Fade the main expanded surface after the shell starts widening. The timer overlay
            // has its own animation and is never mounted in this tree.
            ExpandedContent.Opacity = 0d;
            ExpandedContent.IsHitTestVisible = false;
            var fade = new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.56))
            {
                BeginTime = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.12),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            ExpandedContent.BeginAnimation(UIElement.OpacityProperty, fade);
            compactContent.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0d, TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.34))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else
        {
            // Keep the expanded surface mounted while the shell narrows. Switching to the compact
            // tree before the animation starts makes collapse look like a clipped replacement.
            // Cross-fade the compact tree near the end, then ApplyVisualMode commits the compact
            // layout after the shell reaches its final bounds.
            ExpandedViewport.Visibility = Visibility.Visible;
            ExpandedContent.Visibility = Visibility.Visible;
            ExpandedContent.Opacity = 1d;
            ExpandedContent.IsHitTestVisible = false;
            expandedContent.Visibility = Visibility.Visible;
            expandedContent.Opacity = 1d;
            expandedContent.IsHitTestVisible = false;
            compactContent.Visibility = Visibility.Visible;
            compactContent.Opacity = 0d;
            compactContent.IsHitTestVisible = false;

            // Reveal the compact island only near the end of the shrink. Showing it at the
            // beginning makes the timer panel look like it turns into a large normal island
            // before performing a second resize.
            var fadeDuration = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3);
            var expandedFade = new DoubleAnimation(0d, fadeDuration)
            {
                BeginTime = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.2),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            var compactFade = new DoubleAnimation(1d, fadeDuration)
            {
                BeginTime = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.7),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            expandedContent.BeginAnimation(UIElement.OpacityProperty, expandedFade);
            compactContent.BeginAnimation(UIElement.OpacityProperty, compactFade);
        }
    }

    private void FinishPillMorph(int generation, double targetW, double targetH, bool expanded)
    {
        if (generation != _pillAnimationGeneration) return;
        SetPillAnimationCache(enabled: false, CompactContent);
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PillScale.ScaleX = PillScale.ScaleY = 1d;
        GlassShell.BeginAnimation(WidthProperty, null);
        GlassShell.BeginAnimation(HeightProperty, null);
        GlassShell.BeginAnimation(UIElement.OpacityProperty, null);
        GlassShell.Width = targetW;
        GlassShell.Height = targetH;
        ApplyVisualMode();
        GlassShell.Opacity = 1d;
        _expandAnimating = false;
        RestorePillShadow();
        if (!_loggedAnimationCompletion)
        {
            _loggedAnimationCompletion = true;
            _log.Info($"Real island animation completed: expanded={expanded}, final={GlassShell.ActualWidth:0.#}x{GlassShell.ActualHeight:0.#}");
        }
        if (_viewModel.IsExpanded && !_timerPanelOpen) UpdateAutoGrow();
        LogAirPodsLayoutSnapshot();
    }

    private void ApplyTimerPanelHostLayout(bool animate)
    {
        var width = TimerPanelWidth;
        var height = TimerPanelHeight;
        TimerPanelHost.Width = width;
        TimerPanelHost.Height = height;
        TimerPanelContent.Height = Math.Max(180d, height - 44d);
        TimerPanelContent.VerticalAlignment = VerticalAlignment.Top;
        TimerTabContent.Height = AlarmTabContent.Height = StopwatchTabContent.Height = Math.Max(100d, height - 216d);

        if (_timerPanelOpen)
        {
            if (!_timerPanelHostShown)
                BeginTimerPanelHostOpen(animate);
            else if (!_timerPanelHostAnimating && !_timerPanelClosing)
                SetTimerPanelHostSettled();
            return;
        }

        if (!_timerPanelHostShown)
            SetTimerPanelHostHidden();
    }

    private void BeginTimerPanelHostOpen(bool animate)
    {
        var generation = ++_timerPanelAnimationGeneration;
        var reversing = _timerPanelClosing && _timerPanelHostShown;
        var currentScaleX = TimerPanelScale.ScaleX;
        var currentScaleY = TimerPanelScale.ScaleY;
        var currentTranslateX = TimerPanelTranslate.X;
        var currentContentOpacity = TimerPanelContent.Opacity;
        var currentCornerRadius = TimerPanelHost.CornerRadius;

        StopTimerPanelHostAnimations(preserveCurrent: reversing);
        _timerPanelClosing = false;
        _timerPanelHostShown = true;
        _timerPanelHostAnimating = true;
        TimerPanelHost.Visibility = Visibility.Visible;
        TimerPanelContent.Visibility = Visibility.Visible;
        TimerPanelContent.BeginAnimation(UIElement.OpacityProperty, null);
        TimerPanelContent.Opacity = reversing ? currentContentOpacity : 0d;
        TimerPanelHost.Opacity = 1d;
        TimerPanelHost.CornerRadius = reversing
            ? currentCornerRadius
            : new CornerRadius(TimerPanelMorphCornerRadius);
        TimerPanelHost.IsHitTestVisible = false;
        TimerPanelContent.IsHitTestVisible = false;

        var targetWidth = Math.Max(1d, TimerPanelHost.Width);
        var targetHeight = Math.Max(1d, TimerPanelHost.Height);
        var orbSize = Math.Clamp(CompactTimerOrb.Width, 38d, 62d);
        var orbOffset = TimerOrbTranslate.X;
        var startScaleX = reversing ? currentScaleX : Math.Clamp(orbSize / targetWidth, 0.025d, 1d);
        var startScaleY = reversing ? currentScaleY : Math.Clamp(orbSize / targetHeight, 0.025d, 1d);
        var startTranslateX = reversing ? currentTranslateX : orbOffset;

        if (!animate || _viewModel.IsReducedMotion)
        {
            SetTimerPanelHostSettled();
            return;
        }

        TimerPanelScale.ScaleX = startScaleX;
        TimerPanelScale.ScaleY = startScaleY;
        TimerPanelTranslate.X = startTranslateX;
        var duration = TimeSpan.FromMilliseconds(
            _viewModel.Settings.AnimationIntensity == AnimationIntensity.Expressive ? 420d : 340d);
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        TimerPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1d, duration) { EasingFunction = ease });
        TimerPanelTranslate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0d, duration) { EasingFunction = ease });
        TimerPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateTimerPanelOpenAnimation(duration, ease, generation));
        var cornerDuration = TimeSpan.FromMilliseconds(
            Math.Min(TimerPanelCornerMorphDuration, duration.TotalMilliseconds));
        TimerPanelHost.BeginAnimation(Border.CornerRadiusProperty,
            new CornerRadiusAnimation(
                TimerPanelHost.CornerRadius,
                new CornerRadius(TimerPanelNormalCornerRadius),
                cornerDuration));
        TimerPanelContent.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(TimerPanelContent.Opacity, 1d, TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.55))
            {
                BeginTime = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.18),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private DoubleAnimation CreateTimerPanelOpenAnimation(TimeSpan duration, IEasingFunction ease, int generation)
    {
        var animation = new DoubleAnimation(1d, duration) { EasingFunction = ease };
        animation.Completed += (_, _) => FinishTimerPanelHostOpen(generation);
        return animation;
    }

    private void FinishTimerPanelHostOpen(int generation)
    {
        if (generation != _timerPanelAnimationGeneration || !_timerPanelOpen || _timerPanelClosing) return;
        SetTimerPanelHostSettled();
        _log.Info("Timer panel orb morph opened");
    }

    private void BeginTimerPanelHostClose()
    {
        if (!_timerPanelHostShown) return;

        var generation = ++_timerPanelAnimationGeneration;
        var currentScaleX = TimerPanelScale.ScaleX;
        var currentScaleY = TimerPanelScale.ScaleY;
        var currentTranslateX = TimerPanelTranslate.X;
        var currentContentOpacity = TimerPanelContent.Opacity;
        var currentCornerRadius = TimerPanelHost.CornerRadius;
        StopTimerPanelHostAnimations(preserveCurrent: true);
        _timerPanelClosing = true;
        _timerPanelHostAnimating = true;
        TimerPanelHost.Visibility = Visibility.Visible;
        TimerPanelContent.Visibility = Visibility.Visible;
        TimerPanelHost.IsHitTestVisible = false;
        TimerPanelContent.IsHitTestVisible = false;
        TimerPanelContent.BeginAnimation(UIElement.OpacityProperty, null);
        TimerPanelContent.Opacity = currentContentOpacity;

        var orbSize = Math.Clamp(CompactTimerOrb.Width, 38d, 62d);
        var targetScaleX = Math.Clamp(orbSize / Math.Max(1d, TimerPanelHost.Width), 0.025d, 1d);
        var targetScaleY = Math.Clamp(orbSize / Math.Max(1d, TimerPanelHost.Height), 0.025d, 1d);
        var duration = TimeSpan.FromMilliseconds(
            _viewModel.Settings.AnimationIntensity == AnimationIntensity.Expressive ? 300d : 230d);
        if (_viewModel.IsReducedMotion)
        {
            FinishTimerPanelHostClose(generation);
            return;
        }

        TimerPanelScale.ScaleX = currentScaleX;
        TimerPanelScale.ScaleY = currentScaleY;
        TimerPanelTranslate.X = currentTranslateX;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        TimerPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(targetScaleX, duration) { EasingFunction = ease });
        TimerPanelTranslate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(TimerOrbTranslate.X, duration) { EasingFunction = ease });
        var closeAnimation = new DoubleAnimation(targetScaleY, duration) { EasingFunction = ease };
        closeAnimation.Completed += (_, _) => FinishTimerPanelHostClose(generation);
        TimerPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, closeAnimation);
        var cornerDuration = TimeSpan.FromMilliseconds(
            Math.Min(TimerPanelCornerMorphDuration, duration.TotalMilliseconds));
        TimerPanelHost.BeginAnimation(Border.CornerRadiusProperty,
            new CornerRadiusAnimation(
                currentCornerRadius,
                new CornerRadius(TimerPanelMorphCornerRadius),
                cornerDuration));
        TimerPanelContent.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(currentContentOpacity, 0d, TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.42))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            });
    }

    private void FinishTimerPanelHostClose(int generation)
    {
        if (generation != _timerPanelAnimationGeneration || !_timerPanelClosing) return;
        SetTimerPanelHostHidden();
        _timerPanelOpen = false;
        _timerPanelClosing = false;
        _viewModel.TimerEditorOpen = false;
        _viewModel.InteractionProtected = IsKeyboardFocusWithin;
        _position.ApplyWindowStyles(this, _viewModel.Settings, compact: true);
        ApplyLayout(animate: false);
        _log.Info("Timer panel orb morph closed");
    }

    private void SetTimerPanelHostSettled()
    {
        StopTimerPanelHostAnimations(preserveCurrent: false);
        TimerPanelHost.Visibility = Visibility.Visible;
        TimerPanelContent.Visibility = Visibility.Visible;
        TimerPanelContent.BeginAnimation(UIElement.OpacityProperty, null);
        TimerPanelContent.Opacity = 1d;
        TimerPanelHost.Opacity = 1d;
        TimerPanelScale.ScaleX = TimerPanelScale.ScaleY = 1d;
        TimerPanelTranslate.X = TimerPanelTranslate.Y = 0d;
        TimerPanelHost.IsHitTestVisible = true;
        TimerPanelContent.IsHitTestVisible = true;
        _timerPanelHostShown = true;
        _timerPanelHostAnimating = false;
    }

    private void SetTimerPanelHostHidden()
    {
        StopTimerPanelHostAnimations(preserveCurrent: false);
        TimerPanelHost.Visibility = Visibility.Collapsed;
        TimerPanelHost.Opacity = 0d;
        TimerPanelHost.IsHitTestVisible = false;
        TimerPanelContent.Visibility = Visibility.Collapsed;
        TimerPanelContent.IsHitTestVisible = false;
        TimerPanelContent.BeginAnimation(UIElement.OpacityProperty, null);
        TimerPanelContent.Opacity = 0d;
        TimerPanelScale.ScaleX = TimerPanelScale.ScaleY = 1d;
        TimerPanelTranslate.X = TimerPanelTranslate.Y = 0d;
        _timerPanelHostShown = false;
        _timerPanelHostAnimating = false;
    }

    private void StopTimerPanelHostAnimations(bool preserveCurrent)
    {
        var scaleX = TimerPanelScale.ScaleX;
        var scaleY = TimerPanelScale.ScaleY;
        var translateX = TimerPanelTranslate.X;
        var translateY = TimerPanelTranslate.Y;
        var contentOpacity = TimerPanelContent.Opacity;
        var cornerRadius = TimerPanelHost.CornerRadius;
        TimerPanelHost.BeginAnimation(UIElement.OpacityProperty, null);
        TimerPanelContent.BeginAnimation(UIElement.OpacityProperty, null);
        TimerPanelHost.BeginAnimation(Border.CornerRadiusProperty, null);
        TimerPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        TimerPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        TimerPanelTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        TimerPanelTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        if (preserveCurrent)
        {
            TimerPanelHost.Opacity = 1d;
            TimerPanelScale.ScaleX = scaleX;
            TimerPanelScale.ScaleY = scaleY;
            TimerPanelTranslate.X = translateX;
            TimerPanelTranslate.Y = translateY;
            TimerPanelContent.Opacity = contentOpacity;
            TimerPanelHost.CornerRadius = cornerRadius;
        }
        else
        {
            TimerPanelHost.Opacity = 0d;
            TimerPanelScale.ScaleX = TimerPanelScale.ScaleY = 1d;
            TimerPanelTranslate.X = TimerPanelTranslate.Y = 0d;
            TimerPanelContent.Opacity = 0d;
            TimerPanelHost.CornerRadius = new CornerRadius(TimerPanelNormalCornerRadius);
        }
    }

    private sealed class CornerRadiusAnimation : AnimationTimeline
    {
        public CornerRadiusAnimation() { }

        public CornerRadiusAnimation(CornerRadius from, CornerRadius to, TimeSpan duration)
        {
            From = from;
            To = to;
            Duration = new Duration(duration);
        }

        public CornerRadius From { get; set; }
        public CornerRadius To { get; set; }

        public override Type TargetPropertyType => typeof(CornerRadius);

        protected override Freezable CreateInstanceCore() => new CornerRadiusAnimation
        {
            From = From,
            To = To,
            Duration = Duration
        };

        public override object GetCurrentValue(
            object defaultOriginValue,
            object defaultDestinationValue,
            AnimationClock animationClock)
        {
            var progress = animationClock.CurrentProgress ?? 1d;
            return new CornerRadius(
                Lerp(From.TopLeft, To.TopLeft, progress),
                Lerp(From.TopRight, To.TopRight, progress),
                Lerp(From.BottomRight, To.BottomRight, progress),
                Lerp(From.BottomLeft, To.BottomLeft, progress));
        }

        private static double Lerp(double from, double to, double progress) => from + ((to - from) * progress);
    }

    private void LogAirPodsLayoutSnapshot()
    {
        if (!_viewModel.IsExpanded || !_viewModel.ShowAirPodsCard) return;

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var cardOrigin = AirPodsCard.TranslatePoint(new System.Windows.Point(0, 0), GlassShell);
                var cardBottom = cardOrigin.Y + AirPodsCard.ActualHeight;
                _log.Info(
                    $"AirPods layout: window={ActualWidth:0.#}x{ActualHeight:0.#}, " +
                    $"shell={GlassShell.ActualWidth:0.#}x{GlassShell.ActualHeight:0.#}, " +
                    $"contentDesired={ExpandedContent.DesiredSize.Width:0.#}x{ExpandedContent.DesiredSize.Height:0.#}, " +
                    $"contentActual={ExpandedContent.ActualWidth:0.#}x{ExpandedContent.ActualHeight:0.#}, " +
                    $"cardY={cardOrigin.Y:0.#}, cardHeight={AirPodsCard.ActualHeight:0.#}, cardBottom={cardBottom:0.#}");
            }
            catch (InvalidOperationException)
            {
                // The visual may be detached if the user collapses the island during this render pass.
            }
        }, DispatcherPriority.Render);
    }

    private void Pill_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _idleTimer.Stop();
        SetDimmed(false);
        if (!_timerPanelOpen && _viewModel.Settings.ExpandOnHover && !_viewModel.Settings.ClickThroughWhenCompact)
            RequestMainExpansion();
    }

    private void Pill_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_timerPanelOpen) return;
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, _viewModel.Settings.CollapseDelayMilliseconds));
        _collapseTimer.Start();
        // Don't dim when click-through is on — the pill can't receive the hover that un-dims it.
        if (_viewModel.Settings.IdleDimming && !_viewModel.Settings.ClickThroughWhenCompact) _idleTimer.Start();
    }

    // Safety net: the island must never get stuck invisible/off-screen. Runs every second.
    private void EnsureHealthy()
    {
        try
        {
            var legitimatelyHidden = _viewModel.Settings.AutoHideFullscreen && _hiddenForFullscreen;
            if (!legitimatelyHidden && Visibility != Visibility.Visible) Visibility = Visibility.Visible;
            if (!_dimmed && GlassShell.Opacity < 0.99)
            {
                GlassShell.BeginAnimation(OpacityProperty, null);
                GlassShell.Opacity = 1;
            }
            // Re-assert top-most z-order. Windows silently drops a layered tool-window's top-most position
            // after a full-screen app, an explorer.exe restart, RDP/secure-desktop, or a display/GPU change
            // — the window stays "Visible" to WPF but renders behind everything (the "island vanished until
            // I hit Recenter" case). This heartbeat puts it back on top; SWP_NOMOVE/NOSIZE/NOACTIVATE keep
            // it cheap and non-disruptive (no reposition, no focus steal).
            if (!legitimatelyHidden && !_settingsWindowOpen && _viewModel.Settings.AlwaysOnTop) ReassertTopmost();
            EnsureOnScreen();
        }
        catch { }
    }

    private void ReassertTopmost()
    {
        if (!_sourceReady) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero) return;
        Interop.NativeMethods.SetWindowPos(hwnd, Interop.NativeMethods.HwndTopmost, 0, 0, 0, 0,
            Interop.NativeMethods.SwpNoMove | Interop.NativeMethods.SwpNoSize | Interop.NativeMethods.SwpNoActivate);
    }

    private void EnsureOnScreen()
    {
        if (!_sourceReady) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero || !Interop.NativeMethods.GetWindowRect(hwnd, out var r)) return;
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length == 0) return;
        var minL = screens.Min(s => s.Bounds.Left); var maxR = screens.Max(s => s.Bounds.Right);
        var minT = screens.Min(s => s.Bounds.Top); var maxB = screens.Max(s => s.Bounds.Bottom);
        // Only act if the window has drifted completely off every monitor (e.g. a VDI resolution change).
        if (r.Right <= minL || r.Left >= maxR || r.Bottom <= minT || r.Top >= maxB)
            _position.PositionInitial(this, _viewModel.Settings);
    }

    // Idle dimming: fade the pill when it's been left alone, restore on hover.
    private void SetDimmed(bool dim)
    {
        var target = dim ? Math.Clamp(_viewModel.Settings.IdleOpacityPercent / 100.0, 0.2, 1.0) : 1.0;
        if (_dimmed == dim && Math.Abs(GlassShell.Opacity - target) < 0.01) return;
        _dimmed = dim;
        GlassShell.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(280))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // Auto-hide while a fullscreen app/game owns the foreground monitor.
    private int _fullscreenStreak;
    private void CheckFullscreen()
    {
        if (!_viewModel.Settings.AutoHideFullscreen)
        {
            _fullscreenStreak = 0;
            if (_hiddenForFullscreen) { _hiddenForFullscreen = false; Visibility = Visibility.Visible; }
            return;
        }
        bool fullscreen = false;
        try
        {
            var fg = Interop.NativeMethods.GetForegroundWindow();
            var self = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (fg != nint.Zero && fg != self && fg != Interop.NativeMethods.GetShellWindow() && !IsDesktopOrShell(fg))
            {
                // Never hide for one of our own windows, such as Settings.
                Interop.NativeMethods.GetWindowThreadProcessId(fg, out var pid);
                if (pid != (uint)Environment.ProcessId && Interop.NativeMethods.GetWindowRect(fg, out var r))
                {
                    var screen = System.Windows.Forms.Screen.FromHandle(fg).Bounds;
                    fullscreen = r.Left <= screen.Left && r.Top <= screen.Top
                              && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
                }
            }
        }
        catch { }

        // Debounce: require two consecutive detections before hiding (avoids transient misfires);
        // restore immediately the moment it's no longer fullscreen.
        _fullscreenStreak = fullscreen ? _fullscreenStreak + 1 : 0;
        var hide = _fullscreenStreak >= 2;
        if (hide != _hiddenForFullscreen)
        {
            _hiddenForFullscreen = hide;
            Visibility = hide ? Visibility.Hidden : Visibility.Visible;
        }
    }

    // The desktop ("Progman"/"WorkerW") and the taskbar fill the screen but must not count as fullscreen.
    private static bool IsDesktopOrShell(nint hwnd)
    {
        var sb = new System.Text.StringBuilder(64);
        Interop.NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        var cls = sb.ToString();
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    // Follow the monitor that owns the foreground window.
    private string _lastFollowDevice = "";
    private void CheckFollowScreen()
    {
        var s = _viewModel.Settings;
        var follow = s.FollowActiveScreen || s.PreferredMonitor.StartsWith("Active", StringComparison.OrdinalIgnoreCase);
        if (!follow || s.DefaultPosition == PositionMode.Manual) return;
        try
        {
            var fg = Interop.NativeMethods.GetForegroundWindow();
            if (fg == nint.Zero) return;
            var dev = System.Windows.Forms.Screen.FromHandle(fg).DeviceName;
            if (dev == _lastFollowDevice) return;
            _lastFollowDevice = dev;
            _position.PositionInitial(this, s);
        }
        catch { }
    }

    // Drag-to-scrub playback, with the same direct click behaviour as before.
    private void ProgressBar_SeekStart(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ActualWidth <= 0 || !_viewModel.CanSeek) return;
        _scrubbing = true;
        fe.CaptureMouse();
        SeekToPointer(fe, e.GetPosition(fe).X);
        e.Handled = true;
    }

    // Recovery path that works even if a visual layout is temporarily broken.
    private void Pill_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.Settings.ClickThroughWhenCompact)
            OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void ProgressBar_SeekMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_scrubbing || sender is not FrameworkElement fe || fe.ActualWidth <= 0) return;
        SeekToPointer(fe, e.GetPosition(fe).X);
        e.Handled = true;
    }

    private void ProgressBar_SeekEnd(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && _scrubbing && fe.ActualWidth > 0)
            SeekToPointer(fe, e.GetPosition(fe).X);
        EndScrub(sender as FrameworkElement);
        e.Handled = true;
    }

    private void ProgressBar_SeekCancel(object sender, System.Windows.Input.MouseEventArgs e) => EndScrub(sender as FrameworkElement);

    private void SeekToPointer(FrameworkElement track, double x)
    {
        var fraction = Math.Clamp(x / track.ActualWidth, 0, 1);
        if (_viewModel.SeekCommand.CanExecute(fraction)) _viewModel.SeekCommand.Execute(fraction);
    }

    private void EndScrub(FrameworkElement? track)
    {
        _scrubbing = false;
        if (track?.IsMouseCaptured == true) track.ReleaseMouseCapture();
    }

    // Click album art to open the source app.
    private void AlbumArt_Click(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.Settings.ClickArtOpensApp && _viewModel.Media.HasSession)
        {
            _viewModel.OpenMediaAppCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Output-device row: opens an in-island picker listing active output endpoints; click one to switch default.
    private void OutputDevice_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.RefreshOutputDevices();
        OutputDevicePopup.IsOpen = true;
        e.Handled = true;
    }

    private void OutputDeviceItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string id })
            _viewModel.SelectOutputDeviceCommand.Execute(id);
        OutputDevicePopup.IsOpen = false;
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => _viewModel.IsExpanded = false;

    // The More button owns its menu placement. Keeping this independent from drag handling prevents WPF
    // from treating the last pointer position as the popup anchor and detaching the menu from the island.
    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.ContextMenu is not null)
        {
            b.ContextMenu.PlacementTarget = b;
            b.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            b.ContextMenu.IsOpen = true;
        }
    }

    private void RecenterMenu_Click(object sender, RoutedEventArgs e) => RecenterRequested?.Invoke(this, EventArgs.Empty);
    private void ClipboardMenu_Click(object sender, RoutedEventArgs e) => OpenClipboardRequested?.Invoke(this, EventArgs.Empty);
    private void OnNotificationGroupOpen(object? sender, EventArgs e) => NotificationHistoryPopup.IsOpen = true;
    private void NotificationHistoryMenu_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenNotificationHistoryCommand.Execute(null);
        NotificationHistoryPopup.IsOpen = true;
        e.Handled = true;
    }
    private void CollapseMenu_Click(object sender, RoutedEventArgs e) => _viewModel.IsExpanded = false;

    private void OpenNotification_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenCurrentNotificationCommand.Execute(null);
        e.Handled = true;
    }

    private void DismissNotification_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.DismissCurrentNotificationCommand.Execute(null);
        e.Handled = true;
    }

    private void OpenHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: NotificationHistoryItem item })
            _viewModel.OpenNotificationCommand.Execute(item);
        e.Handled = true;
    }

    private void DismissHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: NotificationHistoryItem item })
            _viewModel.DismissHistoryItemCommand.Execute(item);
        e.Handled = true;
    }

    private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_timerPanelOpen && _viewModel.IsCompact)
        {
            RequestMainExpansion();
            e.Handled = true;
        }
    }

    private void RequestMainExpansion()
    {
        if (_timerPanelOpen || !_viewModel.IsCompact) return;
        if (!_initialLayoutStabilized)
        {
            _mainExpansionPending = true;
            return;
        }
        _viewModel.IsExpanded = true;
    }

    private void DragGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.Settings.LockPosition) return;
        _dragging = true;
        try { DragMove(); }
        catch { }
        finally
        {
            _dragging = false;
            _position.CaptureManualPosition(this, _viewModel.Settings);
            _ = _settingsService.SaveAsync(_viewModel.Settings);
        }
        e.Handled = true;
    }

    private void ClipboardButton_Click(object sender, RoutedEventArgs e) => OpenClipboardRequested?.Invoke(this, EventArgs.Empty);
    private void JoinMeeting_Click(object sender, RoutedEventArgs e) { _viewModel.OpenMeetingCommand.Execute(null); e.Handled = true; }
    private void LiveWidgetsScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroller || scroller.ScrollableWidth <= 0d) return;
        scroller.ScrollToHorizontalOffset(
            Math.Clamp(scroller.HorizontalOffset - e.Delta, 0d, scroller.ScrollableWidth));
        e.Handled = true;
    }

    private void LiveWidgetsScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroller) return;
        UpdateLiveWidgetsOverflow(scroller);
    }

    private void AccessoryLane_SizeChanged(object sender, SizeChangedEventArgs e) => _viewModel.SetAccessoryLaneWidth(e.NewSize.Width);
    private void LiveWidgetsPreviousButton_Click(object sender, RoutedEventArgs e) { LiveWidgetsScroller.ScrollToHorizontalOffset(Math.Max(0, LiveWidgetsScroller.HorizontalOffset - 160)); e.Handled = true; }
    private void LiveWidgetsScroller_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        var target = e.Key switch { Key.Home => 0, Key.End => LiveWidgetsScroller.ScrollableWidth, Key.Left => LiveWidgetsScroller.HorizontalOffset - 160, _ => LiveWidgetsScroller.HorizontalOffset + 160 };
        LiveWidgetsScroller.ScrollToHorizontalOffset(Math.Clamp(target, 0, LiveWidgetsScroller.ScrollableWidth)); e.Handled = true;
    }
    private void LiveWidgetsScroller_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) { if (e.NewFocus is FrameworkElement element) element.BringIntoView(); }
    private void LiveWidgetsMoreButton_Click(object sender, RoutedEventArgs e)
    {
        LiveWidgetsScroller.ScrollToHorizontalOffset(
            Math.Clamp(LiveWidgetsScroller.HorizontalOffset + 160d, 0d, LiveWidgetsScroller.ScrollableWidth));
        e.Handled = true;
    }

    private void UpdateLiveWidgetsOverflow(ScrollViewer scroller)
    {
        // Show the fade + chevron only while widget content is clipped on the right.
        var canScrollRight = scroller.ScrollableWidth > 0d &&
            scroller.HorizontalOffset < scroller.ScrollableWidth - 1d;
        LiveWidgetsPreviousButton.Visibility = scroller.HorizontalOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
        var visibility = canScrollRight ? Visibility.Visible : Visibility.Collapsed;
        if (LiveWidgetsFade.Visibility != visibility) LiveWidgetsFade.Visibility = visibility;
        if (LiveWidgetsMoreButton.Visibility != visibility) LiveWidgetsMoreButton.Visibility = visibility;
    }

    private void TimerButton_Click(object sender, RoutedEventArgs e)
    {
        ShowTimerPanel(pinOpen: true);
        e.Handled = true;
    }

    private void TimerOrb_Click(object sender, MouseButtonEventArgs e)
    {
        ShowTimerPanel(pinOpen: true);
        e.Handled = true;
    }

    private void TimerOrb_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // The orb is a live activity affordance: hovering it should reveal the full
        // timer surface just like Apple's Dynamic Island, without requiring a click.
        if (!_timerPanelOpen) ShowTimerPanel(pinOpen: false);
    }

    private void TimerPanel_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        TryAutoCloseTimerPanel();
    }

    private void TimerOrb_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // The orb sits above the shell: hovering it opens the panel without the pointer
        // ever entering TimerPanelContent, so its leave must also arm the auto-close.
        TryAutoCloseTimerPanel();
    }

    private void TryAutoCloseTimerPanel()
    {
        if (!_timerPanelOpen)
        {
            _timerPanelLeaveTimer.Stop();
            return;
        }

        // A deliberate click owns the panel until the user closes it. Hover-opened panels
        // still use the pointer-based auto-close path below.
        if (_timerPanelPinnedByClick)
        {
            _timerPanelLeaveTimer.Stop();
            return;
        }

        // WPF can keep the transparent overlay under the pointer and never deliver the
        // subsequent MouseLeave. Read the OS cursor position instead, which also works
        // after the pointer has left the transparent window entirely.
        if (IsPointerOverGlassShell() || IsPointerOverTimerPanelHost() || CompactTimerOrb.IsMouseOver)
        {
            // Keep checking while the panel is open. Stopping here loses the leave event
            // in the transparent-overlay case that caused the panel to stick open.
            if (!_timerPanelLeaveTimer.IsEnabled) _timerPanelLeaveTimer.Start();
            return;
        }

        // A captured mouse means a dropdown popup or scrollbar drag is still active.
        if (Mouse.Captured is not null)
        {
            if (!_timerPanelLeaveTimer.IsEnabled) _timerPanelLeaveTimer.Start();
            return;
        }

        _timerPanelLeaveTimer.Stop();
        System.Windows.Input.Keyboard.ClearFocus();
        CloseTimerPanel();
    }

    private bool IsPointerOverGlassShell()
    {
        if (GlassShell.ActualWidth <= 0 || GlassShell.ActualHeight <= 0) return true;
        if (!Interop.NativeMethods.GetCursorPos(out var cursor)) return true;

        var point = GlassShell.PointFromScreen(new System.Windows.Point(cursor.X, cursor.Y));
        return point.X >= 0 && point.Y >= 0
            && point.X <= GlassShell.ActualWidth
            && point.Y <= GlassShell.ActualHeight;
    }

    private bool IsPointerOverTimerPanelHost()
    {
        if (TimerPanelHost.Visibility != Visibility.Visible || TimerPanelHost.ActualWidth <= 0 || TimerPanelHost.ActualHeight <= 0)
            return false;
        if (!Interop.NativeMethods.GetCursorPos(out var cursor)) return true;

        var point = TimerPanelHost.PointFromScreen(new System.Windows.Point(cursor.X, cursor.Y));
        return point.X >= 0 && point.Y >= 0
            && point.X <= TimerPanelHost.ActualWidth
            && point.Y <= TimerPanelHost.ActualHeight;
    }

    public void ShowTimerPanel(bool pinOpen = false)
    {
        if (pinOpen) _timerPanelPinnedByClick = true;

        if (_timerPanelClosing)
        {
            _timerPanelLeaveTimer.Stop();
            BeginTimerPanelHostOpen(animate: true);
            if (!_timerPanelPinnedByClick) _timerPanelLeaveTimer.Start();
            return;
        }
        if (_timerPanelOpen) return;

        _collapseTimer.Stop();
        _timerPanelLeaveTimer.Stop();
        _idleTimer.Stop();
        SetDimmed(false);
        if (_viewModel.IsExpanded)
        {
            _suppressExpandedAnimation = true;
            _viewModel.IsExpanded = false;
            _suppressExpandedAnimation = false;
        }
        _timerPanelOpen = true;
        _viewModel.TimerEditorOpen = true;
        _viewModel.InteractionProtected = true;
        if (!_timerPanelPinnedByClick) _timerPanelLeaveTimer.Start();
        ShowTimerTab();
        ForceShow();
        _position.ApplyWindowStyles(this, _viewModel.Settings, compact: false);
        ApplyLayout(animate: true);
        _log.Info("In-island timer panel opened");
    }

    public void ToggleTimerPanel()
    {
        if (_timerPanelOpen) CloseTimerPanel();
        else ShowTimerPanel();
    }

    public void CloseTimerPanel()
    {
        _timerPanelLeaveTimer.Stop();
        _timerPanelPinnedByClick = false;
        if (!_timerPanelOpen || _timerPanelClosing) return;
        // Keep TimerEditorOpen true until the host has finished shrinking. This keeps the compact orb
        // hidden underneath the panel and lets the separate host land exactly on its orb geometry.
        BeginTimerPanelHostClose();
        _log.Info("In-island timer panel closed");
    }

    private void CloseTimerPanel_Click(object sender, RoutedEventArgs e)
    {
        CloseTimerPanel();
        e.Handled = true;
    }

    private void TimerTab_Click(object sender, RoutedEventArgs e)
    {
        ShowTimerTab();
        e.Handled = true;
    }

    private void AlarmTab_Click(object sender, RoutedEventArgs e)
    {
        ShowAlarmTab();
        e.Handled = true;
    }

    private void StopwatchTab_Click(object sender, RoutedEventArgs e)
    {
        ShowStopwatchTab();
        e.Handled = true;
    }

    private void TimerViewModel_ShowAlarmTabRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(ShowAlarmTab);
    }

    private void ShowAlarmTab()
    {
        TimerTabContent.Visibility = Visibility.Collapsed;
        AlarmTabContent.Visibility = Visibility.Visible;
        StopwatchTabContent.Visibility = Visibility.Collapsed;
        SetTimerTabVisual(TimerTabButton, false);
        SetTimerTabVisual(AlarmTabButton, true);
        SetTimerTabVisual(StopwatchTabButton, false);
        TimerHeaderIcon.Text = "\uE7B7";
        TimerHeaderTitle.Text = "Alarm";
        TimerHeaderSubtitle.Text = "Wake up. Do more.";
        TimerFooterText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TimerAlarmViewModel.AlarmStateText)));
    }

    private void ShowTimerTab()
    {
        TimerTabContent.Visibility = Visibility.Visible;
        AlarmTabContent.Visibility = Visibility.Collapsed;
        StopwatchTabContent.Visibility = Visibility.Collapsed;
        SetTimerTabVisual(TimerTabButton, true);
        SetTimerTabVisual(AlarmTabButton, false);
        SetTimerTabVisual(StopwatchTabButton, false);
        TimerHeaderIcon.Text = "\uE916";
        TimerHeaderTitle.Text = "Timer";
        TimerHeaderSubtitle.Text = "Set a duration. Stay focused.";
        TimerFooterText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TimerAlarmViewModel.TimerFooterText)));
    }

    private void ShowStopwatchTab()
    {
        TimerTabContent.Visibility = Visibility.Collapsed;
        AlarmTabContent.Visibility = Visibility.Collapsed;
        StopwatchTabContent.Visibility = Visibility.Visible;
        SetTimerTabVisual(TimerTabButton, false);
        SetTimerTabVisual(AlarmTabButton, false);
        SetTimerTabVisual(StopwatchTabButton, true);
        TimerHeaderIcon.Text = "\uE916";
        TimerHeaderTitle.Text = "Stopwatch";
        TimerHeaderSubtitle.Text = "Focus. Create. Get things done.";
        TimerFooterText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TimerAlarmViewModel.StopwatchText)));
    }

    private void SetTimerTabVisual(System.Windows.Controls.Button button, bool active)
    {
        button.Background = active
            ? _viewModel.AccentBrush
            : System.Windows.Media.Brushes.Transparent;
        button.BorderBrush = active
            ? _viewModel.AccentBrush
            : System.Windows.Media.Brushes.Transparent;
        button.Foreground = active
            ? System.Windows.Media.Brushes.White
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0xB0, 0xCE));
    }
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
    private void QSettingsButton_Click(object sender, RoutedEventArgs e) => OpenQSettingsRequested?.Invoke(this, EventArgs.Empty);
    private void RecenterButton_Click(object sender, RoutedEventArgs e) => RecenterRequested?.Invoke(this, EventArgs.Empty);

    private void QButton_Click(object sender, RoutedEventArgs e)
    {
        _qFollowLatest = true;
        _ = _viewModel.StartQAsync(_qScreen.LastForegroundTarget);
        e.Handled = true;
    }

    private void QAsk_Click(object sender, RoutedEventArgs e) => _viewModel.SetQMode(DynamicIsland.Q.Core.QMode.Ask);
    private void QSay_Click(object sender, RoutedEventArgs e) => _viewModel.SetQMode(DynamicIsland.Q.Core.QMode.Say);
    private void QAllow_Click(object sender, RoutedEventArgs e) => _viewModel.AcceptQDisclosure();
    private async void QCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.QCanCopyResponse) return;
        _viewModel.CopyQResponse();
        if (sender is not System.Windows.Controls.Button button) return;
        var content = button.Content;
        button.Content = "Copied";
        await Task.Delay(1200);
        button.Content = content;
    }
    private void QStop_Click(object sender, RoutedEventArgs e) => _viewModel.CancelQ();
    private async void QQuickAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string prompt } || string.IsNullOrWhiteSpace(prompt)) return;
        _viewModel.SetQMode(DynamicIsland.Q.Core.QMode.Ask);
        QPromptBox.Text = prompt;
        await SubmitQPromptAsync();
    }

    private void UpdateQThinkingAnimation()
    {
        if (!_sourceReady) return;
        try
        {
            _qThinkingAnimation ??= (Storyboard)Resources["QThinkingAnimation"];
            if (_viewModel.QShowInlineThinking)
                _qThinkingAnimation.Begin(this, isControllable: true);
            else
                _qThinkingAnimation.Remove(this);
        }
        catch { }
    }

    private void ScheduleQTranscriptScroll()
    {
        if (!_sourceReady || !_qFollowLatest || !_viewModel.ShowQSurface) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_qFollowLatest) return;
            QTranscriptScroll.ScrollToEnd();
            QJumpToLatestButton.Visibility = Visibility.Collapsed;
        }, DispatcherPriority.Background);
    }

    private void QTranscriptScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        var atLatest = viewer.ScrollableHeight <= 1 || viewer.VerticalOffset >= viewer.ScrollableHeight - 12;
        if (e.VerticalChange != 0 && e.ExtentHeightChange == 0) _qFollowLatest = atLatest;
        if (e.ExtentHeightChange > 0 && _qFollowLatest)
            Dispatcher.BeginInvoke(viewer.ScrollToEnd, DispatcherPriority.Background);
        QJumpToLatestButton.Visibility = viewer.ScrollableHeight > 8 && !_qFollowLatest
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void QJumpToLatest_Click(object sender, RoutedEventArgs e)
    {
        _qFollowLatest = true;
        QTranscriptScroll.ScrollToEnd();
        QJumpToLatestButton.Visibility = Visibility.Collapsed;
    }

    private async void QRetry_Click(object sender, RoutedEventArgs e)
    {
        var prompt = _viewModel.QPromptText;
        if (string.IsNullOrWhiteSpace(prompt)) return;
        _qFollowLatest = true;
        QPromptBox.Clear();
        UpdateQPromptComposer();
        await _viewModel.SubmitQAsync(prompt);
    }

    private void QNew_Click(object sender, RoutedEventArgs e)
    {
        _qFollowLatest = true;
        QPromptBox.Clear();
        UpdateQPromptComposer();
        _viewModel.ClearQ();
        _ = _viewModel.StartQAsync(_qScreen.LastForegroundTarget);
    }
    private void QClose_Click(object sender, RoutedEventArgs e) => _viewModel.ClearQ();
    private async void QMic_Click(object sender, RoutedEventArgs e)
    {
        var text = await _viewModel.DictateQAsync();
        if (!string.IsNullOrWhiteSpace(text)) QPromptBox.Text = text;
        QPromptBox.Focus();
    }
    private async void QSend_Click(object sender, RoutedEventArgs e) => await SubmitQPromptAsync();
    private async void QPromptBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            await SubmitQPromptAsync();
        }
    }

    private void QPromptBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateQPromptComposer();

    private void UpdateQPromptComposer()
    {
        if (QPromptBox is null || QPromptPlaceholder is null || QSendButton is null) return;
        var hasPrompt = !string.IsNullOrWhiteSpace(QPromptBox.Text);
        QPromptPlaceholder.Visibility = hasPrompt ? Visibility.Collapsed : Visibility.Visible;
        QSendButton.IsEnabled = hasPrompt && !_viewModel.QCanStop;
    }

    private async Task SubmitQPromptAsync()
    {
        var prompt = QPromptBox.Text.Trim();
        if (prompt.Length == 0 || _viewModel.QCanStop) return;
        _qFollowLatest = true;
        QPromptBox.Clear();
        UpdateQPromptComposer();
        await _viewModel.SubmitQAsync(prompt);
    }

    private void SystemEventsOnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
        _position.PositionInitial(this, _viewModel.Settings));

    private void SystemEventsOnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            Dispatcher.BeginInvoke(() =>
            {
                _position.ApplyWindowStyles(this, _viewModel.Settings, _viewModel.IsCompact);
                _position.PositionInitial(this, _viewModel.Settings);
            });
    }
}
