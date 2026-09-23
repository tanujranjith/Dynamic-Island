using System.Windows;
using System.Windows.Interop;
using DynamicIsland.Windows.Infrastructure;
using DynamicIsland.Windows.Interop;
using DynamicIsland.Windows.Models;
using Forms = System.Windows.Forms;

namespace DynamicIsland.Windows.Services;

public sealed class WindowPositionService
{
    internal (double Width, double Height)? VerificationWorkArea { get; set; }
    public (double Width, double Height) AvailableSize(Window window, AppSettings settings)
    {
        if (AppDataPaths.IsPreview && VerificationWorkArea is { } fixture) return fixture;
        var screen = SelectScreen(settings);
        var scale = Math.Max(96u, NativeMethods.GetDpiForWindow(new WindowInteropHelper(window).Handle)) / 96d;
        return (screen.WorkingArea.Width / scale, screen.WorkingArea.Height / scale);
    }
    // Transparent margin around the visible pill (matches the Grid Margin in IslandWindow.xaml) and
    // the pill corner radius, in device-independent units.
    private const double PillMarginLeft = 20, PillMarginTop = 10, PillMarginRight = 20, PillMarginBottom = 18, PillRadius = 18;

    // Window top in device pixels for the configured TopOffset. TopOffset is the gap from the top of
    // the screen to the visible pill; we subtract the transparent top margin so the window can sit
    // slightly off-screen and the pill can reach the very top.
    private static int TopY(Forms.Screen screen, AppSettings settings, double scale, bool workingArea)
    {
        var baseTop = workingArea ? screen.WorkingArea.Top : screen.Bounds.Top;
        return baseTop + (int)Math.Round((Math.Max(0, settings.TopOffset) - PillMarginTop) * scale);
    }

    /// <summary>
    /// Turns the real acrylic backdrop blur on/off and clips it to the rounded pill via a window
    /// region, so the frost stays inside the pill instead of filling the rectangular window. Must be
    /// re-applied whenever the window size changes (the region is in client pixels).
    /// </summary>
    public void ApplyBackdropFrost(Window window, bool enable, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;
        NativeMethods.SetAcrylic(handle, enable, dark);
        if (!enable)
        {
            NativeMethods.SetWindowRgn(handle, nint.Zero, true);
            return;
        }
        UpdateFrostRegion(window);
    }

    public void UpdateFrostRegion(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;
        if (!NativeMethods.GetWindowRect(handle, out var rect)) return;
        var scale = Math.Max(96u, NativeMethods.GetDpiForWindow(handle)) / 96d;
        int left = (int)Math.Round(PillMarginLeft * scale);
        int top = (int)Math.Round(PillMarginTop * scale);
        int right = rect.Width - (int)Math.Round(PillMarginRight * scale);
        int bottom = rect.Height - (int)Math.Round(PillMarginBottom * scale);
        int diameter = (int)Math.Round(PillRadius * 2 * scale);
        if (right <= left || bottom <= top) return;
        var region = NativeMethods.CreateRoundRectRgn(left, top, right + 1, bottom + 1, diameter, diameter);
        NativeMethods.SetWindowRgn(handle, region, false); // window takes ownership of the region
    }

    public void ApplyWindowStyles(Window window, AppSettings settings, bool compact)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        style = settings.ShowInAltTab
            ? (style | NativeMethods.WsExAppWindow) & ~NativeMethods.WsExToolWindow
            : (style | NativeMethods.WsExToolWindow) & ~NativeMethods.WsExAppWindow;
        style = settings.ClickThroughWhenCompact && compact
            ? style | NativeMethods.WsExTransparent
            : style & ~NativeMethods.WsExTransparent;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new nint(style));
        window.Topmost = settings.AlwaysOnTop;

    }

    private readonly Dictionary<Window, (double Width, double Height)> _visibleSizes = [];

    public void PositionInitial(Window window, AppSettings settings, double? visiblePillWidthDip = null, double? visiblePillHeightDip = null)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;
        if (visiblePillWidthDip is { } w && visiblePillHeightDip is { } h)
            _visibleSizes[window] = (w, h);
        var visible = _visibleSizes.GetValueOrDefault(window, (settings.IslandWidth, settings.IslandHeight));
        SetBounds(window, settings, WindowSizingPolicy.EffectiveDimension(window.Width, window.ActualWidth),
            WindowSizingPolicy.EffectiveDimension(window.Height, window.ActualHeight), visible.Width, visible.Height);
    }

    public void KeepAnchorWhileResizing(Window window, AppSettings settings) => PositionInitial(window, settings);

    public void SetAnimatedBounds(Window window, AppSettings settings, double widthDip, double heightDip)
    {
        var visible = _visibleSizes.GetValueOrDefault(window, (settings.IslandWidth, settings.IslandHeight));
        SetBounds(window, settings, widthDip, heightDip, visible.Width, visible.Height);
    }

    private void SetBounds(Window window, AppSettings settings, double widthDip, double heightDip, double visibleWidth, double visibleHeight)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;
        var screen = SelectScreen(settings);
        var scale = Math.Max(96u, NativeMethods.GetDpiForWindow(handle)) / 96d;
        var area = screen.WorkingArea;
        var horizontal = settings.DefaultPosition switch
        {
            PositionMode.TopLeft or PositionMode.MiddleLeft or PositionMode.BottomLeft => 0,
            PositionMode.TopRight or PositionMode.MiddleRight or PositionMode.BottomRight => 2,
            _ => 1
        };
        var vertical = settings.DefaultPosition switch
        {
            PositionMode.MiddleLeft or PositionMode.Center or PositionMode.MiddleRight => 1,
            PositionMode.BottomLeft or PositionMode.BottomCenter or PositionMode.BottomRight => 2,
            _ => 0
        };
        var manual = settings.DefaultPosition == PositionMode.Manual;
        var position = IslandPlacement.Place(area.Left, area.Top, area.Width, area.Height,
            widthDip * scale, visibleWidth * scale, visibleHeight * scale, 8 * scale,
            horizontal, vertical, settings.SideOffset * scale, settings.TopOffset * scale,
            manual ? settings.ManualLeftPixels : null, manual ? settings.ManualTopPixels : null);
        NativeMethods.SetWindowPos(handle, nint.Zero, (int)Math.Round(position.X), (int)Math.Round(position.Y),
            (int)Math.Round(widthDip * scale), (int)Math.Round(heightDip * scale),
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder);
    }

    public void CaptureManualPosition(Window window, AppSettings settings)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!NativeMethods.GetWindowRect(handle, out var rect)) return;
        var screen = Forms.Screen.FromHandle(handle);
        settings.DefaultPosition = PositionMode.Manual;
        settings.ManualLeftPixels = rect.Left;
        settings.ManualTopPixels = rect.Top;
        settings.SideOffset = 0;
        settings.TopOffset = 0;
        settings.ManualMonitorDeviceName = screen.DeviceName;
    }

    public void Recenter(Window window, AppSettings settings)
    {
        settings.DefaultPosition = PositionMode.TopCenter;
        settings.SideOffset = 0;
        settings.TopOffset = 2;
        settings.ManualLeftPixels = null;
        settings.ManualTopPixels = null;
        settings.ManualMonitorDeviceName = null;
        PositionInitial(window, settings);
    }

    private Forms.Screen SelectScreen(AppSettings settings)
    {
        var follow = settings.FollowActiveScreen
            || settings.PreferredMonitor.StartsWith("Active", StringComparison.OrdinalIgnoreCase);
        if (follow)
        {
            try
            {
                var fg = NativeMethods.GetForegroundWindow();
                if (fg != nint.Zero) return Forms.Screen.FromHandle(fg);
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(settings.PreferredMonitor)
            && !settings.PreferredMonitor.StartsWith("Primary", StringComparison.OrdinalIgnoreCase)
            && !settings.PreferredMonitor.StartsWith("Active", StringComparison.OrdinalIgnoreCase))
        {
            var pref = Forms.Screen.AllScreens.FirstOrDefault(
                s => string.Equals(s.DeviceName, settings.PreferredMonitor, StringComparison.OrdinalIgnoreCase));
            if (pref is not null) return pref;
        }

        if (!string.IsNullOrWhiteSpace(settings.ManualMonitorDeviceName))
        {
            var match = Forms.Screen.AllScreens.FirstOrDefault(
                s => string.Equals(s.DeviceName, settings.ManualMonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens.First();
    }

    private static (int X, int Y) EnsureVisible(Forms.Screen screen, int x, int y, int width, int height, int visibleWidth)
    {
        var bounds = screen.WorkingArea;
        var overhang = Math.Max(0, (width - Math.Min(visibleWidth, width)) / 2);
        var minX = bounds.Left - overhang;
        var maxX = bounds.Right - Math.Min(visibleWidth, width) - overhang;
        var safeX = Math.Clamp(x, minX, Math.Max(minX, maxX));
        // Allow the window slightly above the top so the transparent margin can sit off-screen and the
        // pill can hug the top edge.
        var minY = bounds.Top - 40;
        var safeY = Math.Clamp(y, minY, Math.Max(minY, bounds.Bottom - Math.Min(height, bounds.Height)));
        return (safeX, safeY);
    }
}
