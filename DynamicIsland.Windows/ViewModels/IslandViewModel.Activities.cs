using System.Windows.Input;
using DynamicIsland.Windows.Infrastructure;
using DynamicIsland.Windows.Models;

namespace DynamicIsland.Windows.ViewModels;

public sealed partial class IslandViewModel
{
    private bool _timerEditorOpen;
    public bool TimerEditorOpen
    {
        get => _timerEditorOpen;
        set { if (SetProperty(ref _timerEditorOpen, value)) RaiseMany(nameof(ShowTimerOrb), nameof(ShowQSurface)); }
    }
    private bool _interactionProtected;
    public bool InteractionProtected
    {
        get => _interactionProtected;
        set { if (SetProperty(ref _interactionProtected, value)) { RaisePropertyChanged(nameof(ShowBanner)); RaisePropertyChanged(nameof(ShowNotification)); } }
    }
    public bool DeferOrdinaryBanners => InteractionProtected || TimerEditorOpen || IsQActive || HasUrgentAlert;
    public TimerState? DisplayTimer => _timerAlarmService.DisplayTimer(Settings.PinnedActivity == IslandActivity.Timer ? Settings.PinnedTimerId : null);
    private TimerState PrimaryTimer => _timerAlarmService.State.Timers.FirstOrDefault(t => t.Phase == TimerPhase.Completed && !t.CompletionAcknowledged) ?? DisplayTimer ?? _timerAlarmService.State.Timer;
    private AlarmState PrimaryAlarm => _timerAlarmService.State.Alarms.FirstOrDefault(a => a.Phase == AlarmPhase.Ringing)
        ?? _timerAlarmService.State.Alarms.Where(a => a.Phase is AlarmPhase.Scheduled or AlarmPhase.Snoozed).OrderBy(a => a.SnoozeUntil ?? a.TargetAt).FirstOrDefault() ?? _timerAlarmService.State.Alarm;
    public int ActiveTimerCount => _timerAlarmService.State.Timers.Count(t => t.Phase is TimerPhase.Running or TimerPhase.Paused);
    public string TimerCountText => ActiveTimerCount > 1 ? ActiveTimerCount.ToString() : "";
    public bool HasUrgentAlert => _timerAlarmService.State.Alarms.Any(a => a.Phase == AlarmPhase.Ringing) || _timerAlarmService.State.Timers.Any(t => t.Phase == TimerPhase.Completed && !t.CompletionAcknowledged);
    public bool CanSnoozeUrgent => _timerAlarmService.State.Alarms.Any(a => a.Phase == AlarmPhase.Ringing);
    public string UrgentAlertText => string.Join(" · ",
        _timerAlarmService.State.Alarms.Where(a => a.Phase == AlarmPhase.Ringing).Select(a => "Alarm: " + (string.IsNullOrWhiteSpace(a.Label) ? $"{a.Hour:00}:{a.Minute:00}" : a.Label))
        .Concat(_timerAlarmService.State.Timers.Where(t => t.Phase == TimerPhase.Completed && !t.CompletionAcknowledged).Select(t => "Done: " + TimerEngine.TimerLabel(t))));
    public string ActivityPinText => Settings.PinnedActivity switch { IslandActivity.Media => "Pinned: media", IslandActivity.Timer => "Pinned: timer", _ => "Activity: automatic" };
    public ICommand PinMediaCommand => new RelayCommand(() => SetPin(IslandActivity.Media, null));
    public ICommand PinTimerCommand => new RelayCommand(() => { if (_timerAlarmService.State.Timers.Count > 0) SetPin(IslandActivity.Timer, _timerAlarmService.State.Timer.Id); });
    public ICommand UnpinActivityCommand => new RelayCommand(() => SetPin(IslandActivity.None, null));
    public ICommand DismissUrgentCommand => new RelayCommand(() =>
    {
        if (_timerAlarmService.State.Alarms.FirstOrDefault(a => a.Phase == AlarmPhase.Ringing) is { } a) _timerAlarmService.DismissAlarm(a.Id);
        else if (_timerAlarmService.State.Timers.FirstOrDefault(t => t.Phase == TimerPhase.Completed && !t.CompletionAcknowledged) is { } t) _timerAlarmService.AcknowledgeTimer(t.Id);
    });
    public ICommand SnoozeUrgentCommand => new RelayCommand(() => { if (_timerAlarmService.State.Alarms.FirstOrDefault(a => a.Phase == AlarmPhase.Ringing) is { } a) _timerAlarmService.SnoozeAlarm(a.Id, 5); });
    private void SetPin(IslandActivity activity, Guid? timerId)
    {
        Settings.PinnedActivity = activity; Settings.PinnedTimerId = timerId;
        _ = PersistSettingsAsync(); RefreshActivities();
    }
    private void RefreshActivities()
    {
        if (Settings.PinnedActivity == IslandActivity.Timer && !_timerAlarmService.State.Timers.Any(t => t.Id == Settings.PinnedTimerId))
        { Settings.PinnedActivity = IslandActivity.None; Settings.PinnedTimerId = null; _ = PersistSettingsAsync(); }
        RaiseMany(nameof(PrimaryActivity), nameof(HasUrgentAlert), nameof(UrgentAlertText), nameof(CanSnoozeUrgent), nameof(TimerCountText), nameof(ActivityPinText),
            nameof(ShowBanner), nameof(ShowNotification), nameof(ShowTimerOrb), nameof(TimerText), nameof(TimerRemainingProgress), nameof(CompactPrimaryText), nameof(CompactSecondaryText));
    }
}
