using DynamicIsland.Windows.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Windows.Services;

public sealed class MediaSessionService(LoggingService log) : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, DateTimeOffset> _recentActivity = new(StringComparer.OrdinalIgnoreCase);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _selectedSession;
    private Task? _pollTask;
    private string _preferredApp = "Automatic";
    private string _lastIdentity = string.Empty;
    private byte[]? _artwork;

    public event EventHandler<MediaInfo>? Changed;
    public event EventHandler<IReadOnlyList<string>>? AvailableAppsChanged;
    public MediaInfo Current { get; private set; } = MediaInfo.Empty;

    public async Task StartAsync()
    {
        if (_pollTask is not null) return;
        if (Environment.GetEnvironmentVariable("DI_DEMO") == "1")
        {
            _pollTask = DemoLoopAsync(_shutdown.Token);
            return;
        }
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _pollTask = PollLoopAsync(_shutdown.Token);
        }
        catch (Exception ex)
        {
            log.Error("Windows media sessions are unavailable", ex);
            Publish(MediaInfo.Empty);
        }
    }

    // Off by default. With DI_DEMO=1 the island shows a fixed, royalty-free demo track (for screenshots
    // and promo clips) instead of whatever is really playing.
    private async Task DemoLoopAsync(CancellationToken token)
    {
        byte[]? art = null;
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DynamicIsland.Windows", "demo-cover.png");
            if (File.Exists(path)) art = await File.ReadAllBytesAsync(path, token);
        }
        catch { }

        var start = DateTimeOffset.Now;
        while (!token.IsCancellationRequested)
        {
            var elapsed = 38 + (DateTimeOffset.Now - start).TotalSeconds;
            Publish(new MediaInfo
            {
                Title = "Neon Drift",
                Artist = "Aurora",
                SourceAppName = "Aurora",
                PlaybackState = MediaPlaybackState.Playing,
                CanPlayPause = true,
                CanPrevious = true,
                CanNext = true,
                CanSeek = true,
                Position = TimeSpan.FromSeconds(Math.Min(elapsed, 89)),
                Duration = TimeSpan.FromSeconds(90),
                Artwork = art,
                UpdatedAt = DateTimeOffset.Now
            });
            try { await Task.Delay(250, token); } catch { break; }
        }
    }

    public void SetPreferredApp(string? appId) =>
        _preferredApp = string.IsNullOrWhiteSpace(appId) ? "Automatic" : appId;

    public async Task TogglePlayPauseAsync()
    {
        try { if (_selectedSession is not null) await _selectedSession.TryTogglePlayPauseAsync(); }
        catch (Exception ex) { log.Error("Play/pause command failed", ex); }
    }

    public async Task PreviousAsync()
    {
        try { if (_selectedSession is not null) await _selectedSession.TrySkipPreviousAsync(); }
        catch (Exception ex) { log.Error("Previous command failed", ex); }
    }

    public async Task NextAsync()
    {
        try { if (_selectedSession is not null) await _selectedSession.TrySkipNextAsync(); }
        catch (Exception ex) { log.Error("Next command failed", ex); }
    }

    public async Task SeekFractionAsync(double fraction)
    {
        try
        {
            if (_selectedSession is null) return;
            var duration = Current.Duration;
            if (duration.Ticks <= 0) return;
            await _selectedSession.TryChangePlaybackPositionAsync((long)(Math.Clamp(fraction, 0, 1) * duration.Ticks));
        }
        catch (Exception ex) { log.Error("Seek failed", ex); }
    }

    // Seek a fixed amount relative to the current position (used by the 10s rewind/forward controls).
    // The target is clamped to [0, duration] so a near-start rewind lands exactly on 0 and a near-end
    // forward lands exactly on the track length — never overshooting either end. Seeking is relative to
    // the last published position, which is what the user sees on the progress bar, so a +10s tap lands
    // 10s ahead of the displayed elapsed time.
    public async Task SeekByAsync(TimeSpan offset)
    {
        try
        {
            if (_selectedSession is null || !Current.CanSeek) return;
            var duration = Current.Duration;
            if (duration.Ticks <= 0) return;
            var target = Infrastructure.SeekMath.ClampSeek(Current.Position, offset, duration);
            await _selectedSession.TryChangePlaybackPositionAsync(target.Ticks);
        }
        catch (Exception ex) { log.Error("Relative seek failed", ex); }
    }

    public void LaunchSource()
    {
        try
        {
            var id = Current.SourceAppId;
            if (string.IsNullOrWhiteSpace(id)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"shell:AppsFolder\\{id}",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { log.Error("Launching media source failed", ex); }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await RefreshAsync(); }
            catch (Exception ex) { log.Error("Media session refresh failed", ex); }
            try { await Task.Delay(500, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshAsync()
    {
        if (_manager is null) return;
        var sessions = _manager.GetSessions().ToArray();
        var apps = sessions.Select(s => s.SourceAppUserModelId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        AvailableAppsChanged?.Invoke(this, apps);

        var current = _manager.GetCurrentSession();
        var candidates = new List<SessionCandidate>();
        foreach (var session in sessions)
        {
            try
            {
                var playback = session.GetPlaybackInfo();
                var properties = await session.TryGetMediaPropertiesAsync();
                var timeline = session.GetTimelineProperties();
                var id = session.SourceAppUserModelId ?? string.Empty;
                var state = MapPlayback(playback.PlaybackStatus);
                var identity = $"{id}|{properties.Title}|{properties.Artist}|{state}";
                if (state == MediaPlaybackState.Playing || identity != _lastIdentity)
                    _recentActivity[id] = DateTimeOffset.Now;
                var score = Score(session, current, id, state, properties.Title, properties.Artist);
                candidates.Add(new SessionCandidate(session, playback, properties, timeline, id, state, score));
            }
            catch { }
        }

        var best = candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => _recentActivity.GetValueOrDefault(c.AppId))
            .ThenBy(c => c.AppId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (best is null)
        {
            _selectedSession = null;
            _lastIdentity = string.Empty;
            _artwork = null;
            Publish(MediaInfo.Empty);
            return;
        }

        _selectedSession = best.Session;
        var newIdentity = $"{best.AppId}|{best.Properties.Title}|{best.Properties.Artist}";
        if (!string.Equals(newIdentity, _lastIdentity, StringComparison.Ordinal))
        {
            _lastIdentity = newIdentity;
            _artwork = await ReadArtworkAsync(best.Properties.Thumbnail);
        }

        var controls = best.Playback.Controls;
        var (position, duration) = ResolvePosition(best.Timeline, best.State);
        Publish(new MediaInfo
        {
            Title = best.Properties.Title ?? string.Empty,
            Artist = string.IsNullOrWhiteSpace(best.Properties.Artist)
                ? best.Properties.AlbumArtist ?? string.Empty : best.Properties.Artist,
            Album = best.Properties.AlbumTitle ?? string.Empty,
            SourceAppId = best.AppId,
            SourceAppName = FriendlyAppName(best.AppId),
            PlaybackState = best.State,
            CanPlayPause = controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
            CanPrevious = controls.IsPreviousEnabled,
            CanNext = controls.IsNextEnabled,
            CanSeek = controls.IsPlaybackPositionEnabled,
            Position = position,
            Duration = duration,
            Artwork = _artwork,
            UpdatedAt = DateTimeOffset.Now
        });
    }

    // GSMTC reports the position as a snapshot taken at LastUpdatedTime — it does not advance on its
    // own, so polling it returns a stale value (it freezes, and can show the previous track's position
    // until the app pushes a new timeline). When playing, extrapolate from that timestamp so progress
    // tracks the song. Clamped to the track length so it never overshoots.
    private static (TimeSpan Position, TimeSpan Duration) ResolvePosition(
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline, MediaPlaybackState state)
    {
        var start = timeline.StartTime;
        var duration = timeline.EndTime > start
            ? timeline.EndTime - start
            : timeline.MaxSeekTime > TimeSpan.Zero ? timeline.MaxSeekTime : TimeSpan.Zero;

        var position = timeline.Position - start;
        if (state == MediaPlaybackState.Playing && timeline.LastUpdatedTime > DateTimeOffset.MinValue)
        {
            var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
            if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(4)) position += elapsed;
        }
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration) position = duration;
        return (position, duration);
    }

    private int Score(GlobalSystemMediaTransportControlsSession session,
        GlobalSystemMediaTransportControlsSession? current, string appId, MediaPlaybackState state,
        string? title, string? artist)
    {
        var score = state switch
        {
            MediaPlaybackState.Playing => 10_000,
            MediaPlaybackState.Paused => 4_000,
            MediaPlaybackState.Stopped => 1_000,
            _ => 0
        };
        if (!string.Equals(_preferredApp, "Automatic", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_preferredApp, appId, StringComparison.OrdinalIgnoreCase)) score += 20_000;
        if (ReferenceEquals(session, current)) score += 800;
        if (!string.IsNullOrWhiteSpace(title)) score += 300;
        if (!string.IsNullOrWhiteSpace(artist)) score += 100;
        if (_recentActivity.TryGetValue(appId, out var recent))
            score += Math.Max(0, 200 - (int)(DateTimeOffset.Now - recent).TotalSeconds);
        return score;
    }

    private static MediaPlaybackState MapPlayback(GlobalSystemMediaTransportControlsSessionPlaybackStatus status) =>
        status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackState.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackState.NoSession,
            _ => MediaPlaybackState.Unknown
        };

    private static async Task<byte[]?> ReadArtworkAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size is < 32 or > 5_000_000) return null;
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var size = await reader.LoadAsync((uint)stream.Size);
            var data = new byte[size];
            reader.ReadBytes(data);
            return data;
        }
        catch { return null; }
    }

    private static string FriendlyAppName(string appId)
    {
        var value = appId.ToLowerInvariant();
        if (value.Contains("spotify")) return "Spotify";
        if (value.Contains("zune") || value.Contains("music")) return "Media Player";
        if (value.Contains("vlc")) return "VLC";
        if (value.Contains("chrome")) return "Chrome";
        if (value.Contains("edge")) return "Microsoft Edge";
        var name = appId.Split('!')[0].Split('_')[0].Split('.').LastOrDefault();
        return string.IsNullOrWhiteSpace(name) ? "Media" : name;
    }

    private void Publish(MediaInfo info)
    {
        if (info == Current) return;
        Current = info;
        Changed?.Invoke(this, info);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private sealed record SessionCandidate(
        GlobalSystemMediaTransportControlsSession Session,
        GlobalSystemMediaTransportControlsSessionPlaybackInfo Playback,
        GlobalSystemMediaTransportControlsSessionMediaProperties Properties,
        GlobalSystemMediaTransportControlsSessionTimelineProperties Timeline,
        string AppId,
        MediaPlaybackState State,
        int Score);
}
