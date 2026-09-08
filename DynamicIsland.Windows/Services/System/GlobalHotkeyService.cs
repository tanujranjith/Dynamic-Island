using System.Windows;
using System.Windows.Interop;
using DynamicIsland.Windows.Interop;

namespace DynamicIsland.Windows.Services;

/// <summary>Fixed, documented global shortcuts for the small always-on-top island.</summary>
public sealed class GlobalHotkeyService(LoggingService log) : IDisposable
{
    private readonly Dictionary<int, Action> _actions = [];
    private HwndSource? _source;
    private int _nextId = 0x5100;

    public void Attach(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowProc);
    }

    public bool Register(string name, uint modifiers, uint key, Action action)
    {
        if (_source is null || Infrastructure.AppDataPaths.IsPreview) return false;
        var id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_source.Handle, id, modifiers, key))
        {
            log.Debug($"Global shortcut '{name}' could not be registered.");
            return false;
        }
        _actions[id] = action;
        return true;
    }

    private nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmHotKey || !_actions.TryGetValue(wParam.ToInt32(), out var action)) return nint.Zero;
        try { action(); } catch (Exception ex) { log.Debug($"Global shortcut action failed: {ex.Message}"); }
        handled = true;
        return nint.Zero;
    }

    public void Dispose()
    {
        if (_source is null) return;
        foreach (var id in _actions.Keys) NativeMethods.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        _source.RemoveHook(WindowProc);
        _source = null;
    }
}
