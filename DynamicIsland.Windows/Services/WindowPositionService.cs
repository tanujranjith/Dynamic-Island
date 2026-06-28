using System.Windows;
using System.Windows.Interop;
using DynamicIsland.Windows.Interop;
using DynamicIsland.Windows.Models;
using Forms = System.Windows.Forms;

namespace DynamicIsland.Windows.Services;

public sealed class WindowPositionService
{
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

    public void PositionInitial(Window window, AppSettings settings)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;
        var screen = SelectScreen(settings);
        var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(handle));
        var width = (int)Math.Round(window.ActualWidth * dpi / 96d);
        var height = (int)Math.Round(window.ActualHeight * dpi / 96d);
        if (width <= 0) width = (int)Math.Round(window.Width * dpi / 96d);
        if (height <= 0) height = (int)Math.Round(window.Height * dpi / 96d);

        int x;
        int y;
        if (settings.DefaultPosition == PositionMode.Manual &&
            settings.ManualLeftPixels is double manualX && settings.ManualTopPixels is double manualY)
        {
            x = (int)Math.Round(manualX);
            y = (int)Math.Round(manualY);
        }
        else if (settings.DefaultPosition == PositionMode.TopLeft)
        {
            x = screen.WorkingArea.Left + 18;
            y = TopY(screen, settings, dpi / 96d, workingArea: true);
        }
        else
        {
            x = screen.Bounds.Left + (screen.Bounds.Width - width) / 2;
            y = TopY(screen, settings, dpi / 96d, workingArea: false);
        }

        var visible = EnsureVisible(screen, x, y, width, height);
        NativeMethods.SetWindowPos(handle, nint.Zero, visible.X, visible.Y, width, height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder);
    }

    public void KeepAnchorWhileResizing(Window window, AppSettings settings)
    {
        if (settings.DefaultPosition == PositionMode.Manual) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;
        var screen = SelectScreen(settings);
        var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(handle));
        var width = (int)Math.Round(window.ActualWidth * dpi / 96d);
        var topLeft = settings.DefaultPosition == PositionMode.TopLeft;
        int x = topLeft
            ? screen.WorkingArea.Left + 18
            : screen.Bounds.Left + (screen.Bounds.Width - width) / 2;
        int y = TopY(screen, settings, dpi / 96d, topLeft);
        NativeMethods.SetWindowPos(handle, nint.Zero, x, y, 0, 0,
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoSize);
    }

    /// <summary>
    /// Sets the window's full rectangle (centered size + position) in one native call. Used by the
    /// per-frame resize animation so a single driver owns both size and position — this avoids the
    /// WPF Width/Height vs. reposition feedback loop that left height desynced from width.
    /// </summary>
    public void SetAnimatedBounds(Window window, AppSettings settings, double widthDip, double heightDip)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;
        var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(handle));
        var scale = dpi / 96d;
        var widthPx = Math.Max(1, (int)Math.Round(widthDip * scale));
        var heightPx = Math.Max(1, (int)Math.Round(heightDip * scale));
        var screen = SelectScreen(settings);

        int x, y;
        if (settings.DefaultPosition == PositionMode.Manual &&
            settings.ManualLeftPixels is double mx && settings.ManualTopPixels is double my)
        {
            x = (int)Math.Round(mx);
            y = (int)Math.Round(my);
        }
        else if (settings.DefaultPosition == PositionMode.TopLeft)
        {
            x = screen.WorkingArea.Left + 18;
            y = TopY(screen, settings, scale, workingArea: true);
        }
        else
        {
            x = screen.Bounds.Left + (screen.Bounds.Width - widthPx) / 2;
            y = TopY(screen, settings, scale, workingArea: false);
        }

        NativeMethods.SetWindowPos(handle, nint.Zero, x, y, widthPx, heightPx,
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
        settings.ManualMonitorDeviceName = screen.DeviceName;
    }

    public void Recenter(Window window, AppSettings settings)
    {
        settings.DefaultPosition = PositionMode.TopCenter;
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

    private static (int X, int Y) EnsureVisible(Forms.Screen screen, int x, int y, int width, int height)
    {
        var bounds = screen.WorkingArea;
        var safeX = Math.Clamp(x, bounds.Left, Math.Max(bounds.Left, bounds.Right - Math.Min(width, bounds.Width)));
        // Allow the window slightly above the top so the transparent margin can sit off-screen and the
        // pill can hug the top edge.
        var minY = bounds.Top - 40;
        var safeY = Math.Clamp(y, minY, Math.Max(minY, bounds.Bottom - Math.Min(height, bounds.Height)));
        return (safeX, safeY);
    }
}
