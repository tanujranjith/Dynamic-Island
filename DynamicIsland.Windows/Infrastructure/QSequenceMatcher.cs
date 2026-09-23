namespace DynamicIsland.Windows.Infrastructure;

/// <summary>Tracks only progress through V-A-R; never stores typed text.</summary>
public sealed class QSequenceMatcher
{
    private int _progress;
    private long _started;
    private nint _window;

    public void Reset() => _progress = 0;

    public bool Press(uint key, long milliseconds, nint window, bool modified, bool repeat)
    {
        if (modified || window == 0) { Reset(); return false; }
        if (repeat) return false;
        if (_progress > 0 && (window != _window || milliseconds - _started > 2000))
            Reset();
        if (_progress == 1 && key == 'A') { _progress = 2; return false; }
        if (_progress == 2 && key == 'R') { Reset(); return true; }
        Reset();
        if (key == 'V')
        {
            _progress = 1;
            _started = milliseconds;
            _window = window;
        }
        return false;
    }
}
