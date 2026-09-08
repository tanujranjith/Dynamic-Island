namespace DynamicIsland.Windows.Models;

public enum TimerPhase { Idle, Running, Paused, Completed }
public enum AlarmPhase { None, Scheduled, Ringing, Snoozed, Dismissed }
public enum AlarmRepeat { Once, Daily, Weekdays, Weekends, Weekly, SelectedWeekdays, EveryNDays }

public sealed class TimerState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TimerPhase Phase { get; set; }
    public string Label { get; set; } = string.Empty;
    public double TotalSeconds { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public double AccumulatedSeconds { get; set; }
    public double PausedRemainingSeconds { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool CompletionAcknowledged { get; set; }
}

public sealed class AlarmState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AlarmPhase Phase { get; set; }
    public int Hour { get; set; } = 7;
    public int Minute { get; set; }
    public bool Use24Hour { get; set; }
    public string Label { get; set; } = string.Empty;
    public AlarmRepeat Repeat { get; set; } = AlarmRepeat.Once;
    // The weekday a Weekly alarm recurs on (set to the day it first rings). Null for non-weekly repeats.
    public int? RepeatAnchorDayOfWeek { get; set; }
    public DateTime? RepeatAnchorDate { get; set; }
    public int RepeatWeekdayMask { get; set; }
    public int RepeatIntervalDays { get; set; } = 1;
    public DateTime? RepeatEndDate { get; set; }
    public DateTimeOffset? TargetAt { get; set; }
    public DateTimeOffset? RingStartedAt { get; set; }
    public DateTimeOffset? SnoozeUntil { get; set; }
    public int SnoozeCount { get; set; }
}

public sealed class TimerAlarmSnapshot
{
    public int Version { get; set; } = 2;
    public List<TimerState> Timers { get; set; } = [];
    public List<AlarmState> Alarms { get; set; } = [];
    public List<TimerPreset> Presets { get; set; } = [];
    public List<string> MissedAlerts { get; set; } = [];
    public Guid? SelectedTimerId { get; set; }
    public Guid? SelectedAlarmId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public TimerState Timer => Timers.FirstOrDefault(t => t.Id == SelectedTimerId) ?? Timers.FirstOrDefault() ?? new();
    [System.Text.Json.Serialization.JsonIgnore]
    public AlarmState Alarm => Alarms.FirstOrDefault(a => a.Id == SelectedAlarmId) ?? Alarms.FirstOrDefault() ?? new();
}

public sealed record TimerPreset(Guid Id, string Label, double Seconds);
