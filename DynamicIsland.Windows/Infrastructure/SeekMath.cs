namespace DynamicIsland.Windows.Infrastructure;

// Pure playback-seek arithmetic, factored out of MediaSessionService so the 10-second rewind/forward
// clamping can be unit-tested without the WinRT media session. The resulting position is clamped to
// [0, duration] so a rewind near the start lands exactly on 0 and a forward near the end lands exactly
// on the track length — never overshooting either end.
public static class SeekMath
{
    public static TimeSpan ClampSeek(TimeSpan position, TimeSpan offset, TimeSpan duration)
    {
        var target = position + offset;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && target > duration) target = duration;
        return target;
    }
}
