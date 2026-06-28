namespace DynamicIsland.Windows.Models;

public enum TimerPhase { Idle, Running, Paused, Completed }
public enum AlarmPhase { None, Scheduled, Ringing, Snoozed, Dismissed }

public sealed class TimerState
{
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
    public AlarmPhase Phase { get; set; }
    public int Hour { get; set; } = 7;
    public int Minute { get; set; }
    public bool Use24Hour { get; set; }
    public string Label { get; set; } = string.Empty;
    public DateTimeOffset? TargetAt { get; set; }
    public DateTimeOffset? RingStartedAt { get; set; }
    public DateTimeOffset? SnoozeUntil { get; set; }
    public int SnoozeCount { get; set; }
}

public sealed class TimerAlarmSnapshot
{
    public TimerState Timer { get; set; } = new();
    public AlarmState Alarm { get; set; } = new();
}
