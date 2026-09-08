using System.Collections.ObjectModel;
using System.Windows.Input;
using DynamicIsland.Windows.Infrastructure;
using DynamicIsland.Windows.Models;
using DynamicIsland.Windows.Services;

namespace DynamicIsland.Windows.ViewModels;

public sealed partial class TimerAlarmViewModel
{
    public ObservableCollection<TimerChoice> Timers { get; } = [];
    public ObservableCollection<TimerChoice> Alarms { get; } = [];
    public ObservableCollection<TimerPreset> Presets { get; } = [];
    private Guid? _editingAlarm;
    private string _validationMessage = "";
    public string ValidationMessage { get => _validationMessage; set => SetProperty(ref _validationMessage, value); }
    public bool HasMissedAlerts => _service.State.MissedAlerts.Count > 0;
    public string MissedSummary => string.Join(" · ", _service.State.MissedAlerts);
    public string StorageWarning => _service.StorageWarning;
    public Guid? SelectedTimerId
    {
        get => _service.State.Timer.Id;
        set { if (value is Guid id && id != _service.State.Timer.Id) _service.SelectTimer(id); }
    }
    public Guid? SelectedAlarmId
    {
        get => _service.State.Alarm.Id;
        set { if (value is Guid id && id != _service.State.Alarm.Id) _service.SelectAlarm(id); }
    }
    public TimerPreset? SelectedPreset { get; set; }
    public string SaveAlarmText => _editingAlarm is null ? "Add alarm" : "Save changes";
    public ICommand EditAlarmCommand { get; private set; } = null!;
    public ICommand NewAlarmCommand { get; private set; } = null!;
    public ICommand SavePresetCommand { get; private set; } = null!;
    public ICommand StartSavedPresetCommand { get; private set; } = null!;
    public ICommand DeletePresetCommand { get; private set; } = null!;
    public ICommand ClearMissedCommand { get; private set; } = null!;
    private void InitializeCollections()
    {
        EditAlarmCommand = new RelayCommand(() =>
        {
            if (_service.State.Alarms.Count == 0) return;
            var a = _service.State.Alarm;
            _editingAlarm = a.Id;
            Use24Hour = a.Use24Hour;
            AlarmHour = (Use24Hour ? a.Hour : (a.Hour % 12 == 0 ? 12 : a.Hour % 12)).ToString();
            AlarmMinute = a.Minute.ToString("00"); AmPm = a.Hour < 12 ? "AM" : "PM";
            AlarmLabel = a.Label; AlarmRepeat = a.Repeat; IntervalDays = a.RepeatIntervalDays.ToString(); RepeatEndDate = a.RepeatEndDate;
            Sunday = (a.RepeatWeekdayMask & 1) != 0; Monday = (a.RepeatWeekdayMask & 2) != 0; Tuesday = (a.RepeatWeekdayMask & 4) != 0;
            Wednesday = (a.RepeatWeekdayMask & 8) != 0; Thursday = (a.RepeatWeekdayMask & 16) != 0; Friday = (a.RepeatWeekdayMask & 32) != 0; Saturday = (a.RepeatWeekdayMask & 64) != 0;
            RaisePropertyChanged(nameof(SaveAlarmText));
        });
        NewAlarmCommand = new RelayCommand(() => { _editingAlarm = null; ValidationMessage = ""; RaisePropertyChanged(nameof(SaveAlarmText)); });
        SavePresetCommand = new RelayCommand(() =>
        {
            if (double.TryParse(CustomMinutes, out var minutes) && double.IsFinite(minutes) && minutes > 0 && minutes <= 1440)
            { _service.SavePreset(TimerLabel, minutes * 60); ValidationMessage = "Preset saved"; }
            else ValidationMessage = "Enter a duration from 1 second to 1440 minutes.";
        });
        StartSavedPresetCommand = new RelayCommand(() => { if (SelectedPreset is { } p) _service.StartTimer(TimeSpan.FromSeconds(p.Seconds), p.Label); });
        DeletePresetCommand = new RelayCommand(() => { if (SelectedPreset is { } p) _service.DeletePreset(p.Id); });
        ClearMissedCommand = new RelayCommand(_service.ClearMissed);
        RefreshCollections();
    }
    private void RefreshCollections()
    {
        Sync(Timers, _service.State.Timers.Select(t => (t.Id, $"{TimerEngine.TimerLabel(t)} · {TimerAlarmService.FormatDuration(_service.Remaining(t))} · {t.Phase}")));
        Sync(Alarms, _service.State.Alarms.Select(a => (a.Id, $"{TimerAlarmService.FormatAlarmTime(a)} · {a.Label} · {a.Phase}")));
        if (!Presets.SequenceEqual(_service.State.Presets)) { Presets.Clear(); foreach (var p in _service.State.Presets) Presets.Add(p); }
        if (_editingAlarm is Guid id && !_service.State.Alarms.Any(a => a.Id == id)) { _editingAlarm = null; RaisePropertyChanged(nameof(SaveAlarmText)); }
        RaisePropertyChanged(nameof(SelectedTimerId)); RaisePropertyChanged(nameof(SelectedAlarmId));
        RaisePropertyChanged(nameof(MissedSummary)); RaisePropertyChanged(nameof(HasMissedAlerts)); RaisePropertyChanged(nameof(StorageWarning));
    }
    private static void Sync(ObservableCollection<TimerChoice> choices, IEnumerable<(Guid Id, string Text)> values)
    {
        var items = values.ToArray();
        foreach (var old in choices.Where(c => !items.Any(i => i.Id == c.Id)).ToArray()) choices.Remove(old);
        foreach (var (id, text) in items)
        { var choice = choices.FirstOrDefault(c => c.Id == id); if (choice is null) choices.Add(new(id, text)); else choice.Text = text; }
    }
}
public sealed class TimerChoice(Guid id, string text) : ObservableObject
{
    public Guid Id { get; } = id;
    private string _text = text;
    public string Text { get => _text; set => SetProperty(ref _text, value); }
}
