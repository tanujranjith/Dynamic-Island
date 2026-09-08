using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using DynamicIsland.Windows.Models;
using DynamicIsland.Windows.Infrastructure;

namespace DynamicIsland.Windows.Services;

public sealed class NotificationListenerService : IDisposable
{
    private readonly LoggingService _log;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly NotificationSnapshotTracker _tracker = new();
    private UserNotificationListener? _listener;
    private int _generation;
    private bool _enabled;
    private bool _polling;
    public event EventHandler<IReadOnlyList<NotificationInfo>>? BatchReceived;
    public event EventHandler? StatusChanged;
    public IntegrationStatus Status { get; private set; } = IntegrationStatus.Disabled;
    public bool IsActive => Status.State == IntegrationState.Ready;
    public NotificationListenerService(LoggingService log) { _log = log; _timer.Tick += async (_, _) => await PollAsync(); }
    private void SetStatus(IntegrationStatus value) { if (Status == value) return; Status = value; StatusChanged?.Invoke(this, EventArgs.Empty); }
    public async Task StartAsync(bool requestPermission = false)
    {
        if (_enabled) return;
        _enabled = true; var generation = ++_generation;
        SetStatus(new(IntegrationState.Connecting, "Connecting…"));
        try
        {
            _listener = UserNotificationListener.Current;
            var access = requestPermission ? await _listener.RequestAccessAsync() : _listener.GetAccessStatus();
            if (!_enabled || generation != _generation) return;
            if (access != UserNotificationListenerAccessStatus.Allowed)
            { SetStatus(new(IntegrationState.PermissionRequired, "Allow notification access in Windows Settings.", "ms-settings:privacy-notifications")); return; }
            await PollAsync();
            if (_enabled && generation == _generation) _timer.Start();
        }
        catch (Exception ex)
        { if (generation == _generation) { SetStatus(new(IntegrationState.Unavailable, "Notification access is unavailable in this Windows/app configuration.")); _log.Debug(ex.Message); } }
    }
    public async Task RetryAsync() { Stop(); await StartAsync(true); }
    public void Stop() { _enabled = false; _generation++; _timer.Stop(); _tracker.Reset(); SetStatus(IntegrationStatus.Disabled); }
    private async Task PollAsync()
    {
        if (!_enabled || _listener is null || _polling) return;
        _polling = true; var generation = _generation;
        try
        {
            if (_listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            { _timer.Stop(); SetStatus(new(IntegrationState.PermissionRequired, "Notification access was revoked.", "ms-settings:privacy-notifications")); return; }
            var notes = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (!_enabled || generation != _generation) return;
            var fresh = _tracker.Observe(notes.Select(Extract).OfType<NotificationInfo>());
            SetStatus(new(IntegrationState.Ready, "Connected"));
            if (fresh.Count > 0) BatchReceived?.Invoke(this, fresh);
        }
        catch (Exception ex) { if (generation == _generation) { SetStatus(new(IntegrationState.Error, "Notifications could not refresh. Retry.")); _log.Debug(ex.Message); } }
        finally { _polling = false; }
    }
    private static NotificationInfo? Extract(UserNotification n)
    {
        try
        {
            var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding is null) return null;
            var text = binding.GetTextElements();
            var app = n.AppInfo?.DisplayInfo?.DisplayName ?? "Notification";
            return new(app, text.Count > 0 ? text[0].Text : app, string.Join("  ", text.Skip(1).Select(t => t.Text)), n.Id, n.CreationTime, n.AppInfo?.AppUserModelId ?? app);
        }
        catch { return null; }
    }
    public void Dispose() => Stop();
}
