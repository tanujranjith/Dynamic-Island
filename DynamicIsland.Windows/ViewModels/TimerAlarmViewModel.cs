using System.Collections.ObjectModel;
using System.Windows.Input;
using DynamicIsland.Windows.Infrastructure;
using DynamicIsland.Windows.Models;
using DynamicIsland.Windows.Services;

namespace DynamicIsland.Windows.ViewModels;

public sealed class TimerAlarmViewModel : ObservableObject, IDisposable
{
    private readonly TimerAlarmService _service;
    private string _customMinutes = "10";
    private string _timerLabel = string.Empty;
    private string _alarmHour = "7";
    private string _alarmMinute = "00";
    private string _alarmLabel = string.Empty;
    private string _amPm = "AM";
    private bool _use24Hour;

    public TimerAlarmViewModel(TimerAlarmService service, bool use24Hour)
    {
        _service = service;
        _use24Hour = use24Hour;
        StartPresetCommand = new RelayCommand<string>(value =>
        {
            if (int.TryParse(value, out var minutes)) _service.StartTimer(TimeSpan.FromMinutes(minutes), TimerLabel);
        });
        StartCustomCommand = new RelayCommand(() =>
        {
            if (double.TryParse(CustomMinutes, out var minutes) && minutes > 0)
                _service.StartTimer(TimeSpan.FromMinutes(Math.Min(minutes, 24 * 60)), TimerLabel);
        });
        TimerPrimaryCommand = new RelayCommand(() =>
        {
            if (_service.State.Timer.Phase == TimerPhase.Running) _service.PauseTimer();
            else if (_service.State.Timer.Phase == TimerPhase.Paused) _service.ResumeTimer();
            else if (_service.State.Timer.Phase == TimerPhase.Completed) _service.ResetTimer();
        });
        ResetTimerCommand = new RelayCommand(_service.ResetTimer);
        CancelTimerCommand = new RelayCommand(_service.CancelTimer);
        SetAlarmCommand = new RelayCommand(SetAlarm);
        DeleteAlarmCommand = new RelayCommand(_service.DeleteAlarm);
        DismissAlarmCommand = new RelayCommand(_service.DismissAlarm);
        Snooze5Command = new RelayCommand(() => _service.SnoozeAlarm(5));
        Snooze10Command = new RelayCommand(() => _service.SnoozeAlarm(10));
        _swTimer.Tick += (_, _) => RaisePropertyChanged(nameof(StopwatchText));
        StopwatchPrimaryCommand = new RelayCommand(() =>
        {
            if (_sw.IsRunning) { _sw.Stop(); _swTimer.Stop(); }
            else { _sw.Start(); _swTimer.Start(); }
            RaisePropertyChanged(nameof(IsStopwatchRunning));
            RaisePropertyChanged(nameof(StopwatchPrimaryText));
        });
        StopwatchLapCommand = new RelayCommand(() =>
        {
            if (_sw.Elapsed > TimeSpan.Zero) Laps.Insert(0, $"Lap {Laps.Count + 1} · {FormatSw(_sw.Elapsed)}");
        });
        StopwatchResetCommand = new RelayCommand(() =>
        {
            _sw.Reset(); _swTimer.Stop(); Laps.Clear();
            RaisePropertyChanged(nameof(StopwatchText));
            RaisePropertyChanged(nameof(IsStopwatchRunning));
            RaisePropertyChanged(nameof(StopwatchPrimaryText));
        });
        _service.Changed += OnChanged;
    }

    private readonly System.Windows.Threading.DispatcherTimer _swTimer = new() { Interval = TimeSpan.FromMilliseconds(53) };
    private readonly System.Diagnostics.Stopwatch _sw = new();
    public ObservableCollection<string> Laps { get; } = [];
    public bool IsStopwatchRunning => _sw.IsRunning;
    public string StopwatchText => FormatSw(_sw.Elapsed);
    public string StopwatchPrimaryText => _sw.IsRunning ? "Stop" : _sw.Elapsed > TimeSpan.Zero ? "Resume" : "Start";
    public ICommand StopwatchPrimaryCommand { get; }
    public ICommand StopwatchLapCommand { get; }
    public ICommand StopwatchResetCommand { get; }
    private static string FormatSw(TimeSpan t) => t.Hours > 0 ? t.ToString(@"h\:mm\:ss\.ff") : t.ToString(@"mm\:ss\.ff");

    public string CustomMinutes { get => _customMinutes; set => SetProperty(ref _customMinutes, value); }
    public string TimerLabel { get => _timerLabel; set => SetProperty(ref _timerLabel, value); }
    public string AlarmHour { get => _alarmHour; set => SetProperty(ref _alarmHour, value); }
    public string AlarmMinute { get => _alarmMinute; set => SetProperty(ref _alarmMinute, value); }
    public string AlarmLabel { get => _alarmLabel; set => SetProperty(ref _alarmLabel, value); }
    public string AmPm { get => _amPm; set => SetProperty(ref _amPm, value); }
    public bool Use24Hour { get => _use24Hour; set => SetProperty(ref _use24Hour, value); }
    public string TimerRemaining => TimerAlarmService.FormatDuration(_service.TimerRemaining);
    public double TimerProgress => _service.TimerProgress * 100;
    public string TimerStateText => _service.State.Timer.Phase.ToString();
    public string TimerPrimaryText => _service.State.Timer.Phase switch
    {
        TimerPhase.Running => "Pause",
        TimerPhase.Paused => "Resume",
        TimerPhase.Completed => "Reset",
        _ => "Start"
    };
    public string AlarmStateText => _service.State.Alarm.Phase switch
    {
        AlarmPhase.None => "No alarm set",
        AlarmPhase.Scheduled => $"Scheduled for {TimerAlarmService.FormatAlarmTime(_service.State.Alarm)}",
        AlarmPhase.Snoozed => $"Snoozed until {_service.State.Alarm.SnoozeUntil:t}",
        AlarmPhase.Ringing => "Alarm ringing",
        _ => "Dismissed"
    };
    public bool IsAlarmRinging => _service.State.Alarm.Phase == AlarmPhase.Ringing;

    public ICommand StartPresetCommand { get; }
    public ICommand StartCustomCommand { get; }
    public ICommand TimerPrimaryCommand { get; }
    public ICommand ResetTimerCommand { get; }
    public ICommand CancelTimerCommand { get; }
    public ICommand SetAlarmCommand { get; }
    public ICommand DeleteAlarmCommand { get; }
    public ICommand DismissAlarmCommand { get; }
    public ICommand Snooze5Command { get; }
    public ICommand Snooze10Command { get; }

    private void SetAlarm()
    {
        if (!int.TryParse(AlarmHour, out var hour) || !int.TryParse(AlarmMinute, out var minute)) return;
        if (!Use24Hour)
        {
            hour = Math.Clamp(hour, 1, 12) % 12;
            if (string.Equals(AmPm, "PM", StringComparison.OrdinalIgnoreCase)) hour += 12;
        }
        _service.SetAlarm(hour, Math.Clamp(minute, 0, 59), Use24Hour, AlarmLabel);
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        RaisePropertyChanged(nameof(TimerRemaining));
        RaisePropertyChanged(nameof(TimerProgress));
        RaisePropertyChanged(nameof(TimerStateText));
        RaisePropertyChanged(nameof(TimerPrimaryText));
        RaisePropertyChanged(nameof(AlarmStateText));
        RaisePropertyChanged(nameof(IsAlarmRinging));
    }

    public void Dispose() => _service.Changed -= OnChanged;
}
