namespace DynamicIsland.Windows.Models;

public enum MediaPlaybackState { NoSession, Playing, Paused, Stopped, Unknown }

public sealed record MediaInfo
{
    public static readonly MediaInfo Empty = new();
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public string SourceAppId { get; init; } = string.Empty;
    public string SourceAppName { get; init; } = string.Empty;
    public MediaPlaybackState PlaybackState { get; init; } = MediaPlaybackState.NoSession;
    public bool CanPlayPause { get; init; }
    public bool CanPrevious { get; init; }
    public bool CanNext { get; init; }
    // Whether the active session reports it can change playback position (GSMTC
    // IsPlaybackPositionEnabled). When false the 10s rewind/forward controls are disabled rather than
    // silently no-opping, so the UI never pretends seeking works when the provider can't honour it.
    public bool CanSeek { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    // Windows' GlobalSystemMediaTransportControls does not surface an explicit-content flag, so this
    // stays false today; the badge in the UI is wired to it for when a source can provide it.
    public bool IsExplicit { get; init; }
    public byte[]? Artwork { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public bool HasSession => PlaybackState != MediaPlaybackState.NoSession;
    public bool IsPlaying => PlaybackState == MediaPlaybackState.Playing;
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? SourceAppName : Title;
}
