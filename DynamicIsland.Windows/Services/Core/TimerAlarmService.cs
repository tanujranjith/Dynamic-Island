using System.Media;
using System.Text.Json;
using System.Windows.Threading;
using DynamicIsland.Windows.Models;
using DynamicIsland.Windows.Infrastructure;

namespace DynamicIsland.Windows.Services;

public sealed class TimerAlarmEventArgs(string name, string title, string message) : EventArgs
{
    public string Name { get; } = name;
    public string Title { get; } = title;
    public string Message { get; } = message;
}

public sealed class TimerAlarmService : IDisposable
{
    private readonly LoggingService _log;
    private readonly string _statePath;
    private readonly DispatcherTimer _tickTimer = new(DispatcherPriority.Background);
    private System.Threading.Timer? _soundTimer;
    private readonly TimerEngine _engine;
    private DateTimeOffset _lastTick = DateTimeOffset.Now;
    public event EventHandler? Changed;
    public event EventHandler<TimerAlarmEventArgs>? EventRaised;
    public TimerAlarmSnapshot State => _engine.State;
    public string StorageWarning { get; private set; } = "";
    private bool _readOnly;
    public TimerAlarmService(LoggingService log, string? statePath = null)
    {
        _log = log;
        _statePath = statePath ?? Path.Combine(AppDataPaths.Root, "timer-alarm.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_statePath))!);
        var state = new TimerAlarmSnapshot();
        if (File.Exists(_statePath))
        {
            try
            {
                var json = File.ReadAllText(_statePath);
                state = TimerEngine.Deserialize(json);
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("Version", out _) && !File.Exists(_statePath + ".pre-v2.bak"))
                    File.Copy(_statePath, _statePath + ".pre-v2.bak", false);
            }
            catch (NotSupportedException ex) { _readOnly = true; StorageWarning = ex.Message; }
            catch (Exception ex)
            {
                // Preserve unreadable data; never silently replace it on disposal.
                _readOnly = true;
                StorageWarning = "Timer storage could not be loaded. Original data is preserved; changes are temporary.";
                _log.Error(StorageWarning, ex);
            }
        }
        _engine = new TimerEngine(state);
        _engine.Tick(recover: true);
        Save();
        _tickTimer.Tick += (_, _) => Tick();
        Microsoft.Win32.SystemEvents.PowerModeChanged += PowerChanged;
        Microsoft.Win32.SystemEvents.TimeChanged += TimeChanged;
    }
    private void TimeChanged(object? sender, EventArgs e) => _tickTimer.Dispatcher.BeginInvoke(() =>
    {
        _engine.Tick(true);
        _lastTick = DateTimeOffset.Now;
        SaveAndNotify();
    });
    private void PowerChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            _tickTimer.Dispatcher.BeginInvoke(() => { _engine.Tick(true); _lastTick = DateTimeOffset.Now; SaveAndNotify(); });
    }
    public void Start() { UpdateTickInterval(); _tickTimer.Start(); }
    public TimerState? DisplayTimer(Guid? pin = null) => _engine.DisplayTimer(pin);
    public TimeSpan Remaining(TimerState timer) => _engine.GetRemaining(timer);
    public TimeSpan TimerRemaining => Remaining(State.Timer);
    public double TimerProgress => State.Timer.TotalSeconds <= 0 ? 0 : Math.Clamp(1 - TimerRemaining.TotalSeconds / State.Timer.TotalSeconds, 0, 1);
    public void SelectTimer(Guid id) { State.SelectedTimerId = id; Changed?.Invoke(this, EventArgs.Empty); }
    public void SelectAlarm(Guid? id) { State.SelectedAlarmId = id; Changed?.Invoke(this, EventArgs.Empty); }
    public void StartTimer(TimeSpan duration, string? label = null) { _engine.Add(duration, label); SaveAndNotify(); }
    public void PauseTimer() => PauseTimer(State.Timer.Id);
    public void PauseTimer(Guid id) { _engine.Pause(id); SaveAndNotify(); }
    public void ResumeTimer() => ResumeTimer(State.Timer.Id);
    public void ResumeTimer(Guid id) { _engine.Resume(id); SaveAndNotify(); }
    public void ResetTimer() => RestartTimer(State.Timer.Id);
    public void RestartTimer(Guid id) { _engine.Restart(id); SaveAndNotify(); }
    public void CancelTimer() => CancelTimer(State.Timer.Id);
    public void CancelTimer(Guid id) { State.Timers.RemoveAll(t => t.Id == id); SaveAndNotify(); }
    public void AcknowledgeTimer() => AcknowledgeTimer(State.Timer.Id);
    public void AcknowledgeTimer(Guid id)
    { if (State.Timers.FirstOrDefault(t => t.Id == id) is { } timer) timer.CompletionAcknowledged = true; SaveAndNotify(); }
    public void SavePreset(string label, double seconds)
    { State.Presets.Add(new(Guid.NewGuid(), string.IsNullOrWhiteSpace(label) ? $"{seconds / 60:0.#} minutes" : label.Trim(), Math.Clamp(seconds, 1, 86400))); SaveAndNotify(); }
    public void DeletePreset(Guid id) { State.Presets.RemoveAll(p => p.Id == id); SaveAndNotify(); }
    public void ClearMissed() { State.MissedAlerts.Clear(); SaveAndNotify(); }
    public void SetAlarm(int hour, int minute, bool use24Hour, string? label = null, AlarmRepeat repeat = AlarmRepeat.Once,
        int weekdayMask = 0, int intervalDays = 1, DateTime? endDate = null, Guid? editId = null)
    {
        var now = _engine.Now;
        var h = Math.Clamp(hour, 0, 23); var m = Math.Clamp(minute, 0, 59);
        var first = RecurrenceCalculator.Next(now, h, m, AlarmRepeat.Daily, timeZone: TimeZoneInfo.Local) ?? now.AddDays(1);
        int? anchor = repeat == AlarmRepeat.Weekly ? (int)first.DayOfWeek : null;
        DateTime? anchorDate = repeat == AlarmRepeat.EveryNDays ? first.Date : null;
        var target = RecurrenceCalculator.Next(now, h, m, repeat, anchor, anchorDate, weekdayMask, intervalDays, endDate, TimeZoneInfo.Local);
        if (target is null) throw new ArgumentException("Choose repeat days and an end date that allow a future alarm.");
        var alarm = new AlarmState { Id = editId ?? Guid.NewGuid(), Phase = AlarmPhase.Scheduled, Hour = h, Minute = m,
            Use24Hour = use24Hour, Label = label?.Trim() ?? "", Repeat = repeat, RepeatAnchorDayOfWeek = anchor,
            RepeatAnchorDate = anchorDate, RepeatWeekdayMask = weekdayMask, RepeatIntervalDays = Math.Clamp(intervalDays, 1, 365),
            RepeatEndDate = endDate?.Date, TargetAt = target };
        if (editId is not null) State.Alarms.RemoveAll(a => a.Id == editId);
        State.Alarms.Add(alarm); State.SelectedAlarmId = alarm.Id; SaveAndNotify();
    }
    public void DeleteAlarm() => DeleteAlarm(State.Alarm.Id);
    public void DeleteAlarm(Guid id) { State.Alarms.RemoveAll(a => a.Id == id); SaveAndNotify(); }
    public void DismissAlarm() => DismissAlarm(State.Alarm.Id);
    public void DismissAlarm(Guid id)
    { if (State.Alarms.FirstOrDefault(a => a.Id == id) is { Phase: AlarmPhase.Ringing or AlarmPhase.Snoozed } alarm) TimerEngine.AdvanceAlarm(alarm, _engine.Now, TimeZoneInfo.Local); SaveAndNotify(); }
    public void SnoozeAlarm(int minutes) => SnoozeAlarm(State.Alarm.Id, minutes);
    public void SnoozeAlarm(Guid id, int minutes) { _engine.Snooze(id, minutes); SaveAndNotify(); }
    private void Tick()
    {
        var now = DateTimeOffset.Now;
        var result = _engine.Tick(now - _lastTick > TimeSpan.FromSeconds(45));
        _lastTick = now;
        if (result.Completed.Count > 0 && !State.Alarms.Any(a => a.Phase == AlarmPhase.Ringing) && !AppDataPaths.IsPreview) SystemSounds.Asterisk.Play();
        if (result.Completed.Count > 0) EventRaised?.Invoke(this, new("timer_completed", "Timer done", string.Join(", ", State.Timers.Where(t => result.Completed.Contains(t.Id)).Select(TimerEngine.TimerLabel))));
        if (result.Ringing.Count > 0) EventRaised?.Invoke(this, new("alarm_ringing", "Alarm", string.Join(", ", State.Alarms.Where(a => result.Ringing.Contains(a.Id)).Select(a => string.IsNullOrWhiteSpace(a.Label) ? FormatAlarmTime(a) : a.Label))));
        SyncSound();
        if (result.Changed) Save();
        UpdateTickInterval();
        if (result.Changed || State.Timers.Any(t => t.Phase == TimerPhase.Running) || State.Alarms.Any(a => a.Phase is AlarmPhase.Scheduled or AlarmPhase.Snoozed or AlarmPhase.Ringing)) Changed?.Invoke(this, EventArgs.Empty);
    }
    private void SyncSound()
    {
        var ringing = State.Alarms.Any(a => a.Phase == AlarmPhase.Ringing) && !AppDataPaths.IsPreview;
        if (ringing && _soundTimer is null) _soundTimer = new(_ => { try { SystemSounds.Exclamation.Play(); } catch { } }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1.4));
        if (!ringing) { _soundTimer?.Dispose(); _soundTimer = null; }
    }
    private void UpdateTickInterval()
    {
        var active = State.Timers.Any(t => t.Phase == TimerPhase.Running || (t.Phase == TimerPhase.Completed && !t.CompletionAcknowledged)) || State.Alarms.Any(a => a.Phase == AlarmPhase.Ringing);
        var next = State.Alarms.Where(a => a.Phase is AlarmPhase.Scheduled or AlarmPhase.Snoozed).Select(a => (a.Phase == AlarmPhase.Snoozed ? a.SnoozeUntil : a.TargetAt) ?? DateTimeOffset.MaxValue).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
        _tickTimer.Interval = TimeSpan.FromMilliseconds(active ? 500 : Math.Clamp((next - _engine.Now).TotalMilliseconds, 500, 30000));
    }
    private void SaveAndNotify() { SyncSound(); Save(); UpdateTickInterval(); Changed?.Invoke(this, EventArgs.Empty); }
    private void Save()
    {
        if (_readOnly) return;
        _engine.PrepareForSave();
        try { File.WriteAllText(_statePath + ".tmp", JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true })); File.Move(_statePath + ".tmp", _statePath, true); }
        catch (Exception ex) { StorageWarning = "Timer changes could not be saved."; _log.Error(StorageWarning, ex); }
    }
    public static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{value.Minutes:00}:{value.Seconds:00}";

    public static string FormatRepeat(AlarmRepeat repeat) => repeat switch
    {
        AlarmRepeat.Daily => "Daily",
        AlarmRepeat.Weekdays => "Weekdays",
        AlarmRepeat.Weekends => "Weekends",
        AlarmRepeat.Weekly => "Weekly",
        AlarmRepeat.SelectedWeekdays => "Selected weekdays",
        AlarmRepeat.EveryNDays => "Every N days",
        _ => "Once"
    };

    public static string FormatRepeat(AlarmState alarm)
    {
        if (alarm.Repeat == AlarmRepeat.SelectedWeekdays)
        {
            var days = Enum.GetValues<DayOfWeek>()
                .Where(day => (alarm.RepeatWeekdayMask & (1 << (int)day)) != 0)
                .Select(day => day.ToString()[..3]);
            return string.Join(", ", days);
        }
        if (alarm.Repeat == AlarmRepeat.EveryNDays) return $"Every {Math.Max(1, alarm.RepeatIntervalDays)} days";
        return FormatRepeat(alarm.Repeat);
    }

    public static string FormatAlarmTime(AlarmState alarm)
    {
        if (alarm.Use24Hour) return $"{alarm.Hour:00}:{alarm.Minute:00}";
        var suffix = alarm.Hour < 12 ? "AM" : "PM";
        var hour = alarm.Hour % 12;
        if (hour == 0) hour = 12;
        return $"{hour}:{alarm.Minute:00} {suffix}";
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerChanged;
        Microsoft.Win32.SystemEvents.TimeChanged -= TimeChanged;
        _tickTimer.Stop(); _soundTimer?.Dispose(); Save();
    }
}
