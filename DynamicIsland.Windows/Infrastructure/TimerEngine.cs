using System.Text.Json;
using DynamicIsland.Windows.Models;

namespace DynamicIsland.Windows.Infrastructure;

// Platform-independent transitions. The WPF service owns scheduling, persistence and sound.
public sealed class TimerEngine(TimerAlarmSnapshot state, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<Guid, (DateTimeOffset? Start, long Stamp, double Seconds)> _anchors = [];
    public TimerAlarmSnapshot State { get; } = state;
    public DateTimeOffset Now => _clock.GetLocalNow();
    public TimeSpan GetRemaining(TimerState timer)
    {
        if (timer.Phase != TimerPhase.Running) return Remaining(timer, Now);
        if (!_anchors.TryGetValue(timer.Id, out var anchor) || anchor.Start != timer.StartedAt)
        { anchor = (timer.StartedAt, _clock.GetTimestamp(), Remaining(timer, Now).TotalSeconds); _anchors[timer.Id] = anchor; }
        return TimeSpan.FromSeconds(Math.Clamp(anchor.Seconds - _clock.GetElapsedTime(anchor.Stamp).TotalSeconds, 0, 86400));
    }
    public void PrepareForSave()
    {
        foreach (var timer in State.Timers.Where(t => t.Phase == TimerPhase.Running))
        {
            var remaining = GetRemaining(timer).TotalSeconds;
            timer.AccumulatedSeconds = timer.TotalSeconds - remaining;
            timer.StartedAt = Now;
            _anchors[timer.Id] = (timer.StartedAt, _clock.GetTimestamp(), remaining);
        }
        foreach (var id in _anchors.Keys.Where(id => !State.Timers.Any(t => t.Id == id)).ToArray()) _anchors.Remove(id);
    }
    public static TimeSpan Remaining(TimerState timer, DateTimeOffset now)
    {
        var seconds = timer.Phase switch
        {
            TimerPhase.Paused => timer.PausedRemainingSeconds,
            TimerPhase.Completed => 0,
            TimerPhase.Running when timer.StartedAt is not null => timer.TotalSeconds - timer.AccumulatedSeconds - (now - timer.StartedAt.Value).TotalSeconds,
            _ => timer.TotalSeconds
        };
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 86400));
    }
    public TimerState? DisplayTimer(Guid? pin = null) =>
        State.Timers.FirstOrDefault(t => t.Id == pin && t.Phase is TimerPhase.Running or TimerPhase.Paused)
        ?? State.Timers.Where(t => t.Phase == TimerPhase.Running).OrderBy(GetRemaining).FirstOrDefault()
        ?? State.Timers.FirstOrDefault(t => t.Phase == TimerPhase.Paused);
    public Guid Add(TimeSpan duration, string? label)
    {
        var timer = new TimerState { Label = label?.Trim() ?? "", TotalSeconds = Math.Clamp(duration.TotalSeconds, 1, 86400), StartedAt = Now, Phase = TimerPhase.Running };
        State.Timers.Add(timer);
        _anchors[timer.Id] = (timer.StartedAt, _clock.GetTimestamp(), timer.TotalSeconds);
        State.SelectedTimerId = timer.Id;
        return timer.Id;
    }
    public void Pause(Guid id)
    {
        if (State.Timers.FirstOrDefault(t => t.Id == id) is not { Phase: TimerPhase.Running } timer) return;
        timer.PausedRemainingSeconds = GetRemaining(timer).TotalSeconds;
        timer.AccumulatedSeconds = timer.TotalSeconds - timer.PausedRemainingSeconds;
        timer.StartedAt = null;
        timer.Phase = TimerPhase.Paused;
    }
    public void Resume(Guid id)
    {
        if (State.Timers.FirstOrDefault(t => t.Id == id) is not { Phase: TimerPhase.Paused } timer) return;
        timer.StartedAt = Now;
        timer.Phase = TimerPhase.Running;
        _anchors[timer.Id] = (timer.StartedAt, _clock.GetTimestamp(), timer.PausedRemainingSeconds);
    }
    public void Restart(Guid id)
    {
        if (State.Timers.FirstOrDefault(t => t.Id == id) is not { } timer) return;
        timer.StartedAt = Now;
        timer.AccumulatedSeconds = 0;
        timer.CompletedAt = null;
        timer.CompletionAcknowledged = false;
        timer.Phase = TimerPhase.Running;
        _anchors[timer.Id] = (timer.StartedAt, _clock.GetTimestamp(), timer.TotalSeconds);
    }
    public static void AdvanceAlarm(AlarmState alarm, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        alarm.RingStartedAt = null;
        alarm.SnoozeUntil = null;
        alarm.SnoozeCount = 0;
        alarm.TargetAt = alarm.Repeat == AlarmRepeat.Once ? null : RecurrenceCalculator.Next(now, alarm.Hour, alarm.Minute,
            alarm.Repeat, alarm.RepeatAnchorDayOfWeek, alarm.RepeatAnchorDate, alarm.RepeatWeekdayMask, alarm.RepeatIntervalDays, alarm.RepeatEndDate, timeZone);
        alarm.Phase = alarm.TargetAt is null ? AlarmPhase.Dismissed : AlarmPhase.Scheduled;
    }
    public void Snooze(Guid id, int minutes)
    {
        if (State.Alarms.FirstOrDefault(a => a.Id == id) is not { Phase: AlarmPhase.Ringing } alarm) return;
        alarm.Phase = AlarmPhase.Snoozed;
        alarm.SnoozeUntil = Now.AddMinutes(Math.Clamp(minutes, 1, 60));
        alarm.SnoozeCount++;
    }
    public TimerTickResult Tick(bool recover = false)
    {
        var now = Now;
        var completed = new List<Guid>();
        var ringing = new List<Guid>();
        var changed = false;
        foreach (var timer in State.Timers)
        {
            if (timer.Phase == TimerPhase.Running && GetRemaining(timer) <= TimeSpan.Zero)
            {
                timer.Phase = TimerPhase.Completed;
                timer.CompletedAt = now;
                timer.CompletionAcknowledged = recover;
                if (recover) State.MissedAlerts.Add($"Timer: {TimerLabel(timer)}");
                else completed.Add(timer.Id);
                changed = true;
            }
            if (timer.Phase == TimerPhase.Completed && !timer.CompletionAcknowledged &&
                (recover || now - timer.CompletedAt >= TimeSpan.FromSeconds(5)))
            { timer.CompletionAcknowledged = true; changed = true; }
        }
        foreach (var alarm in State.Alarms)
        {
            var due = alarm.Phase == AlarmPhase.Snoozed ? alarm.SnoozeUntil : alarm.TargetAt;
            if (recover && (alarm.Phase == AlarmPhase.Ringing ||
                (alarm.Phase is AlarmPhase.Scheduled or AlarmPhase.Snoozed && due <= now)))
            {
                State.MissedAlerts.Add($"Alarm: {(string.IsNullOrWhiteSpace(alarm.Label) ? $"{alarm.Hour:00}:{alarm.Minute:00}" : alarm.Label)}");
                AdvanceAlarm(alarm, now, _clock.LocalTimeZone);
                changed = true;
            }
            else if (alarm.Phase is AlarmPhase.Scheduled or AlarmPhase.Snoozed && due <= now)
            { alarm.Phase = AlarmPhase.Ringing; alarm.RingStartedAt = now; ringing.Add(alarm.Id); changed = true; }
            else if (alarm.Phase == AlarmPhase.Ringing && now - alarm.RingStartedAt >= TimeSpan.FromMinutes(1))
            { AdvanceAlarm(alarm, now, _clock.LocalTimeZone); changed = true; }
        }
        if (State.MissedAlerts.Count > 100) State.MissedAlerts.RemoveRange(0, State.MissedAlerts.Count - 100);
        return new(changed, completed, ringing);
    }
    public static string TimerLabel(TimerState t) => string.IsNullOrWhiteSpace(t.Label) ? $"{t.TotalSeconds / 60:0.#} minute timer" : t.Label;
    public static TimerAlarmSnapshot Deserialize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("Version", out var version) && version.GetInt32() > 2)
            throw new NotSupportedException("Timer data is from a newer island version; it has not been changed.");
        var state = JsonSerializer.Deserialize<TimerAlarmSnapshot>(json) ?? throw new JsonException("Missing timer state");
        if (!root.TryGetProperty("Version", out _))
        {
            if (root.TryGetProperty("Timer", out var t) && t.Deserialize<TimerState>() is { } timer && (timer.TotalSeconds > 0 || timer.Phase != TimerPhase.Idle)) state.Timers.Add(timer);
            if (root.TryGetProperty("Alarm", out var a) && a.Deserialize<AlarmState>() is { Phase: not AlarmPhase.None } alarm) state.Alarms.Add(alarm);
        }
        if (state.Timers is null || state.Alarms is null || state.Presets is null || state.MissedAlerts is null) throw new JsonException("Missing collections");
        if (state.Timers.Any(t => t is null || !double.IsFinite(t.TotalSeconds) || t.TotalSeconds < 0 || t.TotalSeconds > 86400 ||
            !double.IsFinite(t.AccumulatedSeconds) || !double.IsFinite(t.PausedRemainingSeconds) || !Enum.IsDefined(t.Phase)) ||
            state.Alarms.Any(a => a is null || a.Hour is < 0 or > 23 || a.Minute is < 0 or > 59 || !Enum.IsDefined(a.Phase) || !Enum.IsDefined(a.Repeat)))
            throw new JsonException("Invalid timer or alarm values");
        var ids = new HashSet<Guid>();
        foreach (var t in state.Timers) if (t.Id == Guid.Empty || !ids.Add(t.Id)) { t.Id = Guid.NewGuid(); ids.Add(t.Id); }
        foreach (var a in state.Alarms) if (a.Id == Guid.Empty || !ids.Add(a.Id)) { a.Id = Guid.NewGuid(); ids.Add(a.Id); }
        if (state.Presets.Any(p => p is null || !double.IsFinite(p.Seconds) || p.Seconds < 1 || p.Seconds > 86400)) throw new JsonException("Invalid saved preset");
        state.Version = 2;
        return state;
    }
}
public sealed record TimerTickResult(bool Changed, IReadOnlyList<Guid> Completed, IReadOnlyList<Guid> Ringing);
