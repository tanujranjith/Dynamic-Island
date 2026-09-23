using System.Collections.ObjectModel;
using System.Windows.Input;
using DynamicIsland.Windows.Infrastructure;
using DynamicIsland.Windows.Models;
using DynamicIsland.Windows.Services;

namespace DynamicIsland.Windows.ViewModels;

public sealed partial class TimerAlarmViewModel : ObservableObject, IDisposable
{
    private readonly TimerAlarmService _service;
    private string _customMinutes = "10";
    private string _timerLabel = string.Empty;
    private string _alarmHour = "7";
    private string _alarmMinute = "00";
    private string _alarmLabel = string.Empty;
    private string _amPm = "AM";
    private bool _use24Hour;
    private AlarmRepeat _alarmRepeat = AlarmRepeat.Once;
    private string _intervalDays = "1";
    private DateTime? _repeatEndDate;
    private bool _sunday, _monday, _tuesday, _wednesday, _thursday, _friday, _saturday;

    public TimerAlarmViewModel(TimerAlarmService service, bool use24Hour)
    {
        _service = service;
        _use24Hour = use24Hour;
        InitializeCollections();
        StartPresetCommand = new RelayCommand<string>(value =>
        {
            if (int.TryParse(value, out var minutes)) _service.StartTimer(TimeSpan.FromMinutes(minutes), TimerLabel);
        });
        StartCustomCommand = new RelayCommand(() =>
        {
            if (!double.TryParse(CustomMinutes, out var minutes) || !double.IsFinite(minutes) || minutes < 1d / 60 || minutes > 1440)
            { ValidationMessage = "Enter a duration from 1 second to 1440 minutes."; return; }
            ValidationMessage = "";
            _service.StartTimer(TimeSpan.FromMinutes(minutes), TimerLabel);
        });        TimerPrimaryCommand = new RelayCommand(() =>
        {
            if (_service.State.Timer.Phase == TimerPhase.Running) _service.PauseTimer();
            else if (_service.State.Timer.Phase == TimerPhase.Paused) _service.ResumeTimer();
            else if (_service.State.Timer.Phase == TimerPhase.Completed) _service.ResetTimer();
        });
        ResetTimerCommand = new RelayCommand(_service.ResetTimer);
        CancelTimerCommand = new RelayCommand(_service.CancelTimer);
        EndTimerCommand = new RelayCommand<Guid>(id => { if (id != Guid.Empty) _service.CancelTimer(id); });
        SetAlarmCommand = new RelayCommand(SetAlarm);
        DeleteAlarmCommand = new RelayCommand(_service.DeleteAlarm);
        DismissAlarmCommand = new RelayCommand(_service.DismissAlarm);
        Snooze5Command = new RelayCommand(() => _service.SnoozeAlarm(5));
        Snooze10Command = new RelayCommand(() => _service.SnoozeAlarm(10));
        UseTimerLabelForAlarmCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(TimerLabel))
            {
                ValidationMessage = "Enter a timer label first.";
                return;
            }

            AlarmLabel = TimerLabel.Trim();
            ValidationMessage = "Label sent to Alarm tab — review schedule and save.";
            ShowAlarmTabRequested?.Invoke(this, EventArgs.Empty);
        });
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
    public string IntervalDays { get => _intervalDays; set => SetProperty(ref _intervalDays, value); }
    public DateTime? RepeatEndDate { get => _repeatEndDate; set => SetProperty(ref _repeatEndDate, value); }
    public bool Sunday { get => _sunday; set => SetProperty(ref _sunday, value); }
    public bool Monday { get => _monday; set => SetProperty(ref _monday, value); }
    public bool Tuesday { get => _tuesday; set => SetProperty(ref _tuesday, value); }
    public bool Wednesday { get => _wednesday; set => SetProperty(ref _wednesday, value); }
    public bool Thursday { get => _thursday; set => SetProperty(ref _thursday, value); }
    public bool Friday { get => _friday; set => SetProperty(ref _friday, value); }
    public bool Saturday { get => _saturday; set => SetProperty(ref _saturday, value); }
    public Array RepeatOptions => Enum.GetValues<AlarmRepeat>();
    public AlarmRepeat AlarmRepeat { get => _alarmRepeat; set => SetProperty(ref _alarmRepeat, value); }
    public string TimerRemaining => TimerAlarmService.FormatDuration(_service.TimerRemaining);
    public double TimerProgress => _service.TimerProgress * 100;
    public double TimerRemainingProgress => Math.Clamp(100d - TimerProgress, 0d, 100d);
    public double TimerRingPerimeterUnits => 2d * Math.PI * 157d / 8d;
    public string TimerStateText => _service.State.Timer.Phase.ToString();
    public bool IsTimerControllable => _service.State.Timer.Phase is TimerPhase.Running or TimerPhase.Paused;
    public bool ShowLiveTimer => _service.State.Timer.Phase is TimerPhase.Running or TimerPhase.Paused;
    public string LiveTimerLabel
    {
        get
        {
            var timer = _service.State.Timer;
            if (!string.IsNullOrWhiteSpace(timer.Label)) return timer.Label;

            var total = TimeSpan.FromSeconds(Math.Max(1, timer.TotalSeconds));
            if (total.TotalMinutes >= 1 && Math.Abs(total.TotalMinutes - Math.Round(total.TotalMinutes)) < 0.01)
                return $"{Math.Round(total.TotalMinutes):0} minute timer";
            return $"{TimerAlarmService.FormatDuration(total)} timer";
        }
    }
    public string LiveTimerPrimaryGlyph => _service.State.Timer.Phase == TimerPhase.Paused ? "\uE768" : "\uE769";
    public string LiveTimerPrimaryTooltip => _service.State.Timer.Phase == TimerPhase.Paused ? "Resume timer" : "Pause timer";
    public string TimerFooterText => _service.State.Timer.Phase switch
    {
        TimerPhase.Running => string.IsNullOrWhiteSpace(_service.State.Timer.Label) ? "Timer is running" : _service.State.Timer.Label,
        TimerPhase.Paused => "Timer paused",
        TimerPhase.Completed => "Timer completed",
        _ => "No active timer"
    };
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
        AlarmPhase.Scheduled => $"Scheduled for {TimerAlarmService.FormatAlarmTime(_service.State.Alarm)}"
            + (_service.State.Alarm.Repeat == AlarmRepeat.Once ? "" : $" · {TimerAlarmService.FormatRepeat(_service.State.Alarm)}"),
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
    public ICommand EndTimerCommand { get; }
    public ICommand SetAlarmCommand { get; }
    public ICommand DeleteAlarmCommand { get; }
    public ICommand DismissAlarmCommand { get; }
    public ICommand Snooze5Command { get; }
    public ICommand Snooze10Command { get; }
    public ICommand UseTimerLabelForAlarmCommand { get; }

    public event EventHandler? ShowAlarmTabRequested;

    private void SetAlarm()
    {
        if (!int.TryParse(AlarmHour, out var hour) || !int.TryParse(AlarmMinute, out var minute) || minute is < 0 or > 59 || (Use24Hour ? hour is < 0 or > 23 : hour is < 1 or > 12))
        { ValidationMessage = "Enter a valid hour and minute."; return; }
        if (!Use24Hour)
        {
            hour = Math.Clamp(hour, 1, 12) % 12;
            if (string.Equals(AmPm, "PM", StringComparison.OrdinalIgnoreCase)) hour += 12;
        }
        var mask = (Sunday ? 1 : 0) | (Monday ? 2 : 0) | (Tuesday ? 4 : 0) | (Wednesday ? 8 : 0)
            | (Thursday ? 16 : 0) | (Friday ? 32 : 0) | (Saturday ? 64 : 0);
        var interval = int.TryParse(IntervalDays, out var parsed) ? Math.Clamp(parsed, 1, 365) : 1;
        try
        {
            _service.SetAlarm(hour, Math.Clamp(minute, 0, 59), Use24Hour, AlarmLabel, AlarmRepeat, mask, interval, RepeatEndDate, _editingAlarm);
            _editingAlarm = null; ValidationMessage = "Alarm saved"; RaisePropertyChanged(nameof(SaveAlarmText));
        }
        catch (ArgumentException ex) { ValidationMessage = ex.Message; }
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        RefreshCollections();
        RaisePropertyChanged(nameof(TimerRemaining));
        RaisePropertyChanged(nameof(TimerProgress));
        RaisePropertyChanged(nameof(TimerRemainingProgress));
        RaisePropertyChanged(nameof(TimerStateText));
        RaisePropertyChanged(nameof(IsTimerControllable));
        RaisePropertyChanged(nameof(ShowLiveTimer));
        RaisePropertyChanged(nameof(LiveTimerLabel));
        RaisePropertyChanged(nameof(LiveTimerPrimaryGlyph));
        RaisePropertyChanged(nameof(LiveTimerPrimaryTooltip));
        RaisePropertyChanged(nameof(TimerFooterText));
        RaisePropertyChanged(nameof(TimerPrimaryText));
        RaisePropertyChanged(nameof(AlarmStateText));
        RaisePropertyChanged(nameof(IsAlarmRinging));
    }

    public void Dispose() => _service.Changed -= OnChanged;
}
