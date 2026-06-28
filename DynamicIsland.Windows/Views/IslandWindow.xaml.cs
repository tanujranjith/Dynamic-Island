using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Windows.Models;
using DynamicIsland.Windows.Services;
using DynamicIsland.Windows.ViewModels;
using Microsoft.Win32;

namespace DynamicIsland.Windows.Views;

public partial class IslandWindow : Window
{
    private readonly IslandViewModel _viewModel;
    private readonly WindowPositionService _position;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _collapseTimer = new();
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _sourceReady;
    private bool _dragging;
    private bool _dimmed;
    private bool _hiddenForFullscreen;

    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? OpenTimerRequested;
    public event EventHandler? OpenVisionRequested;
    public event EventHandler? OpenClipboardRequested;
    public event EventHandler? RecenterRequested;

    public IslandWindow(IslandViewModel viewModel, WindowPositionService position, SettingsService settingsService)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _position = position;
        _settingsService = settingsService;
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!GlassShell.IsMouseOver && !_dragging && !_viewModel.PinExpanded) _viewModel.IsExpanded = false;
        };
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            if (_viewModel.Settings.IdleDimming && !GlassShell.IsMouseOver) SetDimmed(true);
        };
        _fullscreenTimer.Tick += (_, _) => { CheckFullscreen(); CheckFollowScreen(); EnsureHealthy(); };
        _fullscreenTimer.Start();
        SourceInitialized += (_, _) =>
        {
            _sourceReady = true;
            _position.ApplyWindowStyles(this, _viewModel.Settings, compact: true);
            ApplyLayout(animate: false);
            ApplyFrost();
        };
        Loaded += (_, _) =>
        {
            ApplyLayout(animate: false);
            ApplyFrost();
            // Re-fit the pill whenever the expanded content's size changes (live CPU/RAM/weather/title text).
            ExpandedContent.SizeChanged += (_, _) => UpdateAutoGrow();
        };
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        SystemEvents.DisplaySettingsChanged += SystemEventsOnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += SystemEventsOnPowerModeChanged;
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= SystemEventsOnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= SystemEventsOnPowerModeChanged;
            _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
            _fullscreenTimer.Stop();
            _idleTimer.Stop();
        };
    }

    public void ApplySettings()
    {
        _position.ApplyWindowStyles(this, _viewModel.Settings, _viewModel.IsCompact);
        ApplyLayout(animate: false);
        ApplyFrost();
        EnsureHealthy();
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
            _position.ApplyWindowStyles(this, _viewModel.Settings, _viewModel.IsCompact);
            AnimatePill(animate: true);
        }
        else if (e.PropertyName == nameof(IslandViewModel.NotificationSeq))
        {
            // NotificationSeq changes once per genuinely new notification (unlike ShowNotification, which
            // routine refreshes re-raise), so the entrance plays exactly once and never restarts mid-display.
            PlayNotificationIntro();
        }
    }

    private Storyboard? _notifIntro;

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
    // The expanded reference layout is wide and tall; the canvas must comfortably contain the largest pill.
    private const double CanvasW = 1200d, CanvasH = 248d;
    private (double cW, double cH, double eW, double eH, double winW, double winH) Metrics()
        => _viewModel.Settings.IslandSize switch
        {
            IslandSize.Compact => (212d, 40d, 820d, 184d, CanvasW, CanvasH),
            IslandSize.Large => (326d, 52d, 1000d, 216d, CanvasW, CanvasH),
            _ => (266d, 44d, 900d, 200d, CanvasW, CanvasH)
        };

    // When auto-grow is on, size the expanded pill to its content so nothing clips.
    private (double W, double H) ExpandedPillSize()
    {
        var m = Metrics();
        if (!_viewModel.Settings.AutoGrowPill) return (m.eW, m.eH);
        try
        {
            // Measure the content unconstrained so trimmed/async text reports its true natural size.
            ExpandedContent.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var d = ExpandedContent.DesiredSize;
            var w = Math.Clamp(d.Width + 2, m.eW, CanvasW - 24);
            var h = Math.Clamp(d.Height + 2, m.eH, CanvasH - 24);
            return (w, h);
        }
        catch { return (m.eW, m.eH); }
    }

    // Snap the expanded pill to fit its current content (called as content text changes size).
    private bool _inAutoGrow;
    private bool _expandAnimating;
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
            }
        }
        finally { _inAutoGrow = false; }
    }

    private void ApplyLayout(bool animate)
    {
        var m = Metrics();
        if (Math.Abs(Width - m.winW) > 0.5 || Math.Abs(Height - m.winH) > 0.5)
        {
            Width = m.winW;
            Height = m.winH;
        }
        _position.PositionInitial(this, _viewModel.Settings);
        AnimatePill(animate);
    }

    private void AnimatePill(bool animate)
    {
        var m = Metrics();
        var (eW, eH) = ExpandedPillSize();
        var targetW = _viewModel.IsExpanded ? eW : m.cW;
        var targetH = _viewModel.IsExpanded ? eH : m.cH;
        var reduced = _viewModel.Settings.AnimationIntensity == AnimationIntensity.Reduced;

        if (!animate || reduced)
        {
            GlassShell.BeginAnimation(WidthProperty, null);
            GlassShell.BeginAnimation(HeightProperty, null);
            GlassShell.Width = targetW;
            GlassShell.Height = targetH;
            ExpandedContent.BeginAnimation(OpacityProperty, null);
            ExpandedContent.Opacity = _viewModel.IsExpanded ? 1d : 0d;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(
            _viewModel.Settings.AnimationIntensity == AnimationIntensity.Expressive ? 340d : 250d);
        IEasingFunction ease = _viewModel.IsExpanded
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 }   // springy bounce on open
            : new QuinticEase { EasingMode = EasingMode.EaseOut };                  // smooth settle on close

        var widthAnim = new DoubleAnimation(targetW, duration) { EasingFunction = ease };
        if (_viewModel.IsExpanded)
        {
            // Once the open animation lands, drop the held animation values and track content live.
            _expandAnimating = true;
            widthAnim.Completed += (_, _) => { _expandAnimating = false; UpdateAutoGrow(); };
        }
        GlassShell.BeginAnimation(WidthProperty, widthAnim);
        GlassShell.BeginAnimation(HeightProperty, new DoubleAnimation(targetH, duration) { EasingFunction = ease });

        if (_viewModel.IsExpanded)
        {
            // Fade the controls in once the pill has reached (slightly overshot) full width, so the
            // wide content never overflows the still-growing pill. No rectangular clip needed.
            ExpandedContent.Opacity = 0d;
            ExpandedContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(150))
            {
                BeginTime = TimeSpan.FromMilliseconds(150)
            });
        }
        else
        {
            ExpandedContent.BeginAnimation(OpacityProperty, null);
            ExpandedContent.Opacity = 0d;
        }
    }

    private void Pill_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _idleTimer.Stop();
        SetDimmed(false);
        if (_viewModel.Settings.ExpandOnHover && !_viewModel.Settings.ClickThroughWhenCompact)
            _viewModel.IsExpanded = true;
    }

    private void Pill_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
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
            if (!legitimatelyHidden && _viewModel.Settings.AlwaysOnTop) ReassertTopmost();
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
                // Never hide for one of OUR OWN windows (settings, camera, the privacy-blur overlay — which
                // is itself fullscreen and would otherwise make the island vanish).
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

    // Click-to-seek on the playback bar.
    private void ProgressBar_Seek(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.ActualWidth > 0)
        {
            var fraction = Math.Clamp(e.GetPosition(fe).X / fe.ActualWidth, 0, 1);
            _viewModel.SeekCommand.Execute(fraction);
            e.Handled = true;
        }
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

    // Output-device row: opens Windows sound settings (interim "device picker" until an in-app selector ships).
    private void OutputDevice_Click(object sender, MouseButtonEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); }
        catch { }
        e.Handled = true;
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => _viewModel.IsExpanded = false;

    // The "more" button doubles as the drag handle: drag it to move the island, or click (no drag) to open
    // the overflow menu. DragMove returns instantly on a plain click, so we distinguish by cursor travel.
    private void MenuButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var start = System.Windows.Forms.Cursor.Position;
        if (!_viewModel.Settings.LockPosition)
        {
            _dragging = true;
            try { DragMove(); } catch { }
            _dragging = false;
        }
        var end = System.Windows.Forms.Cursor.Position;
        var moved = Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y) > 3;
        if (moved)
        {
            _position.CaptureManualPosition(this, _viewModel.Settings);
            _ = _settingsService.SaveAsync(_viewModel.Settings);
        }
        else if (sender is System.Windows.Controls.Button b && b.ContextMenu is not null)
        {
            b.ContextMenu.PlacementTarget = b;
            b.ContextMenu.IsOpen = true;
        }
        e.Handled = true;
    }

    private void RecenterMenu_Click(object sender, RoutedEventArgs e) => RecenterRequested?.Invoke(this, EventArgs.Empty);
    private void ClipboardMenu_Click(object sender, RoutedEventArgs e) => OpenClipboardRequested?.Invoke(this, EventArgs.Empty);
    private void CollapseMenu_Click(object sender, RoutedEventArgs e) => _viewModel.IsExpanded = false;

    private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsCompact)
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

    private void VisionButton_Click(object sender, RoutedEventArgs e) => OpenVisionRequested?.Invoke(this, EventArgs.Empty);
    private void ClipboardButton_Click(object sender, RoutedEventArgs e) => OpenClipboardRequested?.Invoke(this, EventArgs.Empty);
    private void JoinMeeting_Click(object sender, MouseButtonEventArgs e) { _viewModel.OpenMeetingCommand.Execute(null); e.Handled = true; }
    private void TimerButton_Click(object sender, RoutedEventArgs e) => OpenTimerRequested?.Invoke(this, EventArgs.Empty);
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
    private void RecenterButton_Click(object sender, RoutedEventArgs e) => RecenterRequested?.Invoke(this, EventArgs.Empty);

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

