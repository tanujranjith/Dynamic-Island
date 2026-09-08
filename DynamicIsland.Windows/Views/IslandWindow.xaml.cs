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
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _sourceReady;
    private bool _dragging;
    private bool _dimmed;
    private bool _hiddenForFullscreen;
    private bool _settingsWindowOpen;
    private bool _scrubbing;
    private bool _timerPanelOpen;
    private bool _suppressExpandedAnimation;
    private bool _liveTimerVisible;
    private bool _lastIsStatsStyle;
    private bool _lastShowAirPodsCard;
    private bool _lastShowWidgetsPanel;
    private Storyboard? _qThinkingAnimation;
    private bool _qFollowLatest = true;

    private double TimerPanelWidth => WindowSizingPolicy.BoundedDimension(1000, AvailableWorkArea.Width);
    private double TimerPanelHeight => WindowSizingPolicy.BoundedDimension(520, AvailableWorkArea.Height, 76);
    private (double Width, double Height) AvailableWorkArea => _position.AvailableSize(this, _viewModel.Settings);
    private const double LiveTimerExtraHeight = 86d;

    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? OpenClipboardRequested;
    public event EventHandler? RecenterRequested;

    public IslandWindow(IslandViewModel viewModel, TimerAlarmViewModel timerViewModel, WindowPositionService position, SettingsService settingsService, LoggingService log, ScreenContextService qScreen)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
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
        };
        GotKeyboardFocus += (_, _) => _viewModel.InteractionProtected = true;
        LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() => _viewModel.InteractionProtected = _timerPanelOpen || IsKeyboardFocusWithin || Mouse.Captured is not null);
        GotMouseCapture += (_, _) => _viewModel.InteractionProtected = true;
        LostMouseCapture += (_, _) => _viewModel.InteractionProtected = _timerPanelOpen || IsKeyboardFocusWithin;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        _viewModel.NotificationGroupOpenRequested += OnNotificationGroupOpen;
        _timerViewModel.PropertyChanged += TimerViewModelOnPropertyChanged;
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
            _fullscreenTimer.Stop();
            _idleTimer.Stop();
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

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IslandViewModel.IsExpanded))
        {
            if (_suppressExpandedAnimation) return;
            if (_timerPanelOpen) _timerPanelOpen = false; _viewModel.TimerEditorOpen = false; _viewModel.InteractionProtected = IsKeyboardFocusWithin;
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
        else if (e.PropertyName == nameof(IslandViewModel.IsQActive) || e.PropertyName == nameof(IslandViewModel.ShowQSurface))
        {
            if (_timerPanelOpen) return;
            ApplyVisualMode();
            if (_viewModel.IsExpanded) ApplyLayout(animate: false);
            if (_viewModel.ShowQSurface)
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
        var (expandedWidth, expandedHeight) = _viewModel.Settings.IslandSize switch
        {
            // The media header, transport controls, and the live-activity cards need 260px+
            // after their outer margins.  The smaller values clipped the entire bottom row,
            // making RAM/network/battery appear to have disappeared.
            IslandSize.Compact => (820d, 264d + quoteSpace + timerSpace + statsSpace + qSpace + accessorySpace),
            IslandSize.Large => (1000d, 322d + quoteSpace + timerSpace + statsSpace + qSpace + accessorySpace),
            _ => (900d, 292d + quoteSpace + timerSpace + statsSpace + qSpace + accessorySpace)
        };
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
        ApplyVisualMode();
        var m = Metrics();
        UpdateTimerOrbLayout(m.cW, m.cH);
        UpdatePrivacyIndicatorLayout(m.cW, m.cH, animate);
        var desiredWindowHeight = _timerPanelOpen ? Math.Max(m.winH, TimerPanelHeight + 20d) : m.winH;
        if (Math.Abs(Width - m.winW) > 0.5 || Math.Abs(Height - desiredWindowHeight) > 0.5)
        {
            Width = m.winW;
            Height = desiredWindowHeight;
        }
        var targetPillWidth = _timerPanelOpen ? TimerPanelWidth : _viewModel.IsExpanded ? ExpandedPillSize().W : m.cW;
        _position.PositionInitial(this, _viewModel.Settings, targetPillWidth);
        AnimatePill(animate);
    }

    private void UpdateTimerOrbLayout(double compactWidth, double compactHeight)
    {
        var size = Math.Clamp(compactHeight, 38d, 62d);
        CompactTimerOrb.Width = size;
        CompactTimerOrb.Height = size;
        TimerOrbTranslate.X = compactWidth / 2d + 8d + size / 2d;
    }

    private void UpdatePrivacyIndicatorLayout(double compactWidth, double compactHeight, bool animate)
    {
        var (activeWidth, activeHeight) = _timerPanelOpen
            ? (TimerPanelWidth, TimerPanelHeight)
            : _viewModel.IsExpanded
                ? ExpandedPillSize()
                : (compactWidth, compactHeight);

        // Follow the shell's trailing edge. Expanded mode keeps the orb near the top controls.
        var targetX = activeWidth / 2d + 12d;
        var targetY = _viewModel.IsExpanded || _timerPanelOpen
            ? 12d
            : Math.Max(0d, (activeHeight - 18d) / 2d);

        PrivacyDotTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        PrivacyDotTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        if (!animate || _viewModel.IsReducedMotion)
        {
            PrivacyDotTranslate.X = targetX;
            PrivacyDotTranslate.Y = targetY;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(
            _viewModel.Settings.AnimationIntensity == AnimationIntensity.Expressive ? 340d : 250d);
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        PrivacyDotTranslate.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(targetX, duration) { EasingFunction = ease });
        PrivacyDotTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(targetY, duration) { EasingFunction = ease });
    }

    private void ApplyVisualMode()
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
        SetModeContent(CompactContent, !expanded && !_timerPanelOpen && (apple || _viewModel.IsEdgePill || _viewModel.IsHolePunchMode));
        // The full Apple activity deck is the stable shared expanded surface. The compact Stats
        // layout still provides the dense glanceable alternative, while this prevents Stats mode
        // from ever opening into an empty shell during rapid hover/settings transitions.
        // TimerPanelContent is intentionally hosted inside the shared expanded surface so it uses
        // the same clipped island material. Keep that parent alive while the timer panel is open;
        // the timer's opaque black layer covers the media deck underneath.
        // QContent and TimerPanelContent are overlays hosted inside ExpandedContent. Keep the
        // shared expanded parent mounted while either overlay is active; collapsing the parent
        // makes the Island render as a black shell even though the child overlay is visible.
                ExpandedViewport.Visibility = expanded || _timerPanelOpen ? Visibility.Visible : Visibility.Collapsed;
        ExpandedContent.Width = Math.Max(600, (_timerPanelOpen ? TimerPanelWidth : Metrics().eW) - (q ? 0 : 56));
        ExpandedContent.Height = q ? Metrics().eH : _timerPanelOpen ? Math.Max(200, TimerPanelHeight - 44) : double.NaN;
        SetModeContent(ExpandedContent, expanded || _timerPanelOpen);
        SetModeContent(QContent, expanded && q);
        SetModeContent(StatsCompactContent, !apple && !_viewModel.IsEdgePill && !_viewModel.IsHolePunchMode && !expanded && !_timerPanelOpen);
        SetModeContent(StatsExpandedContent, false);
        SetModeContent(StatsOverlay, !apple && expanded && !_timerPanelOpen);
        TimerPanelContent.Height = Math.Max(180, TimerPanelHeight - 44);
        TimerPanelContent.VerticalAlignment = VerticalAlignment.Top;
        TimerTabContent.Height = AlarmTabContent.Height = Math.Max(100, TimerPanelHeight - 216);
        SetModeContent(TimerPanelContent, _timerPanelOpen && !q);
    }

    // Switching visual modes while the shell is expanded previously changed Visibility only. If an
    // in-flight transition had left the new content at opacity 0, the result was a large blank pill.
    // Reset the full presentation state together so either layout is always immediately usable.
    private static void SetModeContent(UIElement content, bool visible)
    {
        content.BeginAnimation(UIElement.OpacityProperty, null);
        content.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        content.Opacity = visible ? 1d : 0d;
        content.IsHitTestVisible = visible;
    }

    private Grid ActiveCompactContent => _viewModel.IsStatsStyle ? StatsCompactContent : CompactContent;
    private Grid ActiveExpandedContent => _viewModel.ShowQSurface ? QContent : _timerPanelOpen ? TimerPanelContent : ExpandedContent;

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

    // This is the original real-island animation path: animate GlassShell itself inside the fixed,
    // transparent HWND. The preserved working build used this exact BackEase/QuinticEase approach.
    private void AnimatePill(bool animate)
    {
        var generation = ++_pillAnimationGeneration;
        var m = Metrics();
        // Make the destination surface measurable before computing its safe expanded bounds.
        // A Collapsed WPF element reports no desired size, which previously defeated the
        // anti-clipping calculation on the first expansion.
        ApplyVisualMode();
        var (eW, eH) = ExpandedPillSize();
        var targetW = _timerPanelOpen ? TimerPanelWidth : _viewModel.IsExpanded ? eW : m.cW;
        var targetH = _timerPanelOpen ? TimerPanelHeight : _viewModel.IsExpanded ? eH : m.cH;
        if (_viewModel.IsExpanded || _timerPanelOpen) SizeExpandedViewport(targetW, targetH);
        var reduced = _viewModel.Settings.AnimationIntensity == AnimationIntensity.Reduced;
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PillScale.ScaleX = PillScale.ScaleY = 1d;
        var expanded = _viewModel.IsExpanded || _timerPanelOpen;
        var compactContent = ActiveCompactContent;
        var expandedContent = ActiveExpandedContent;

        if (!animate || reduced)
        {
            GlassShell.BeginAnimation(WidthProperty, null);
            GlassShell.BeginAnimation(HeightProperty, null);
            GlassShell.Width = targetW;
            GlassShell.Height = targetH;
            ApplyVisualMode();
            _expandAnimating = false;
            RestorePillShadow();
            if (_viewModel.IsExpanded && !_timerPanelOpen) UpdateAutoGrow();
            return;
        }

        var duration = TimeSpan.FromMilliseconds(
            _viewModel.Settings.AnimationIntensity == AnimationIntensity.Expressive ? 340d : 250d);
        IEasingFunction ease = expanded
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 }
            : new QuinticEase { EasingMode = EasingMode.EaseOut };

        _expandAnimating = true;
        UseFastPillShadow();
        if (!_loggedAnimationStart)
        {
            _loggedAnimationStart = true;
            _log.Info($"Real island animation started: expanded={expanded}, from={GlassShell.ActualWidth:0.#}x{GlassShell.ActualHeight:0.#}, target={targetW:0.#}x{targetH:0.#}, duration={duration.TotalMilliseconds:0}ms");
        }

        var widthAnimation = new DoubleAnimation(targetW, duration) { EasingFunction = ease };
        var heightAnimation = new DoubleAnimation(targetH, duration) { EasingFunction = ease };
        heightAnimation.Completed += (_, _) => FinishPillMorph(generation, targetW, targetH, expanded);
        GlassShell.BeginAnimation(WidthProperty, widthAnimation);
        GlassShell.BeginAnimation(HeightProperty, heightAnimation);

        if (expanded)
        {
            expandedContent.Opacity = 0d;
            expandedContent.IsHitTestVisible = true;
            var fade = new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(150))
            {
                BeginTime = TimeSpan.FromMilliseconds(150)
            };
            expandedContent.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        else
        {
            compactContent.Opacity = 1d;
            compactContent.IsHitTestVisible = true;
        }
    }

    private void FinishPillMorph(int generation, double targetW, double targetH, bool expanded)
    {
        if (generation != _pillAnimationGeneration) return;
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PillScale.ScaleX = PillScale.ScaleY = 1d;
        GlassShell.BeginAnimation(WidthProperty, null);
        GlassShell.BeginAnimation(HeightProperty, null);
        GlassShell.Width = targetW;
        GlassShell.Height = targetH;
        ApplyVisualMode();
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
            _viewModel.IsExpanded = true;
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
            _viewModel.IsExpanded = true;
            e.Handled = true;
        }
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
        ShowTimerPanel();
        e.Handled = true;
    }

    private void TimerOrb_Click(object sender, MouseButtonEventArgs e)
    {
        ShowTimerPanel();
        e.Handled = true;
    }

    private void TimerOrb_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // The orb is a live activity affordance: hovering it should reveal the full
        // timer surface just like Apple's Dynamic Island, without requiring a click.
        if (!_timerPanelOpen) ShowTimerPanel();
    }

    private void TimerPanel_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Keep the editor stable while typing or using a dropdown.
        if (!IsKeyboardFocusWithin && Mouse.Captured is null) CloseTimerPanel();
    }

    public void ShowTimerPanel()
    {
        _collapseTimer.Stop();
        _idleTimer.Stop();
        SetDimmed(false);
        if (_viewModel.IsExpanded)
        {
            _suppressExpandedAnimation = true;
            _viewModel.IsExpanded = false;
            _suppressExpandedAnimation = false;
        }
        _timerPanelOpen = true; _viewModel.TimerEditorOpen = true; _viewModel.InteractionProtected = true;
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
        if (!_timerPanelOpen) return;
        _timerPanelOpen = false; _viewModel.TimerEditorOpen = false; _viewModel.InteractionProtected = IsKeyboardFocusWithin;
        _position.ApplyWindowStyles(this, _viewModel.Settings, compact: true);
        ApplyLayout(animate: true);
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
        TimerTabContent.Visibility = Visibility.Collapsed;
        AlarmTabContent.Visibility = Visibility.Visible;
        TimerTabButton.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0, 0, 0, 0));
        TimerTabButton.BorderBrush = System.Windows.Media.Brushes.Transparent;
        TimerTabButton.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0x9C, 0xA2));
        AlarmTabButton.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0x22, 0x26));
        AlarmTabButton.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x39, 0x39, 0x3E));
        AlarmTabButton.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0xAD, 0xFF));
        TimerFooterText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TimerAlarmViewModel.AlarmStateText)));
        e.Handled = true;
    }

    private void ShowTimerTab()
    {
        TimerTabContent.Visibility = Visibility.Visible;
        AlarmTabContent.Visibility = Visibility.Collapsed;
        TimerTabButton.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0x22, 0x26));
        TimerTabButton.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x39, 0x39, 0x3E));
        TimerTabButton.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0xAD, 0xFF));
        AlarmTabButton.Background = System.Windows.Media.Brushes.Transparent;
        AlarmTabButton.BorderBrush = System.Windows.Media.Brushes.Transparent;
        AlarmTabButton.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0x9C, 0xA2));
        TimerFooterText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TimerAlarmViewModel.TimerFooterText)));
    }
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
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
        QSendButton.IsEnabled = hasPrompt;
    }

    private async Task SubmitQPromptAsync()
    {
        var prompt = QPromptBox.Text.Trim();
        if (prompt.Length == 0) return;
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
