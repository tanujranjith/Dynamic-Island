using System.Runtime.InteropServices;
using System.Windows.Threading;
using DynamicIsland.Windows.Infrastructure;
using DynamicIsland.Windows.Interop;
using DynamicIsland.Windows.Models;

namespace DynamicIsland.Windows.Services;

/// <summary>Registers Q chords and an optional non-consuming V-A-R listener.</summary>
public sealed class QShortcutService(GlobalHotkeyService hotkeys, LoggingService log,
    Dispatcher dispatcher, Action activate) : IDisposable
{
    private readonly List<int> _ids = [];
    private readonly QSequenceMatcher _sequence = new();
    private readonly HashSet<uint> _down = [];
    private HookProc? _callback;
    private nint _hook;
    private QActivationShortcuts? _configured;
    private int _generation;
    public string Status { get; private set; } = "";

    public void Configure(QActivationShortcuts choices)
    {
        if (_configured == choices) return;
        Dispose();
        _configured = choices;
        if (AppDataPaths.IsPreview) return;
        var failures = new List<string>();
        Register(QActivationShortcuts.CtrlAltQ, "Ctrl+Alt+Q", 0x0003, 'Q');
        Register(QActivationShortcuts.ShiftA, "Shift+A", 0x0004, 'A');
        Register(QActivationShortcuts.ShiftComma, "Shift+comma", 0x0004, 0xBC);
        Register(QActivationShortcuts.ShiftPeriod, "Shift+period", 0x0004, 0xBE);
        if (choices.HasFlag(QActivationShortcuts.VarSequence))
        {
            _callback = KeyboardCallback;
            _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
            if (_hook == 0) failures.Add("V → A → R");
        }
        Status = failures.Count > 0 ? "Unavailable: " + string.Join(", ", failures) + ". Another app may be using these shortcuts." : "";
        if (failures.Count > 0) log.Info("Q shortcut registration failed: " + string.Join(", ", failures));

        void Register(QActivationShortcuts flag, string label, uint modifiers, uint key)
        {
            if (!choices.HasFlag(flag)) return;
            var id = hotkeys.Register("Open Q (" + label + ")", modifiers | 0x4000, key, activate);
            if (id == 0) failures.Add(label);
            else _ids.Add(id);
        }
    }

    private nint KeyboardCallback(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            try
            {
                var key = Marshal.PtrToStructure<KeyboardData>(data);
                // Synthetic input must not accidentally invoke Q.
                if ((key.Flags & 0x10) == 0)
                {
                    if (message == 0x101 || message == 0x105) _down.Remove(key.Key);
                    else if (message == 0x100 || message == 0x104)
                    {
                        var repeat = !_down.Add(key.Key);
                        var uppercase = IsDown(0x10) != ((GetKeyState(0x14) & 1) != 0);
                        var modified = uppercase || IsDown(0x11) || IsDown(0x12) || IsDown(0x5B) || IsDown(0x5C);
                        if (_sequence.Press(key.Key, Environment.TickCount64, NativeMethods.GetForegroundWindow(), modified, repeat))
                        {
                            var generation = _generation;
                            dispatcher.BeginInvoke(() =>
                            {
                                if (generation == _generation && _configured?.HasFlag(QActivationShortcuts.VarSequence) == true)
                                    activate();
                            }, DispatcherPriority.Background);
                        }
                    }
                }
            }
            catch { _sequence.Reset(); }
        }
        // Leave every key, including the final R, available to the foreground app.
        return CallNextHookEx(_hook, code, message, data);
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose()
    {
        _generation++;
        foreach (var id in _ids) hotkeys.Unregister(id);
        _ids.Clear();
        if (_hook != 0) UnhookWindowsHookEx(_hook);
        _hook = 0;
        _down.Clear();
        _sequence.Reset();
        _configured = null;
        Status = "";
    }

    private delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData { public uint Key, ScanCode, Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int type, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
}
