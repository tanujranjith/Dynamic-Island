using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland.Windows.Services;

public sealed record NotificationInfo(string App, string Title, string Body);

/// <summary>
/// Mirrors incoming Windows toast notifications using UserNotificationListener. Requires user consent and
/// may be unavailable to unpackaged apps — in that case it simply stays inactive. Raises on the UI thread.
/// </summary>
public sealed class NotificationListenerService(LoggingService log) : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private UserNotificationListener? _listener;
    private uint _lastSeenId;
    private bool _primed;

    public event EventHandler<NotificationInfo>? Notified;
    public bool IsActive { get; private set; }

    public async Task StartAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                log.Info($"Notification mirroring not available (access {access}).");
                return;
            }
            IsActive = true;
            _timer.Tick += async (_, _) => await PollAsync();
            _timer.Start();
        }
        catch (Exception ex) { log.Info($"Notification listener unavailable: {ex.Message}"); }
    }

    private async Task PollAsync()
    {
        if (_listener is null) return;
        try
        {
            var notes = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            UserNotification? newest = null;
            uint maxId = _lastSeenId;
            foreach (var n in notes)
                if (n.Id > maxId) { maxId = n.Id; newest = n; }

            // Skip the very first poll so we don't replay the whole existing tray.
            if (_primed && newest is not null)
            {
                var info = Extract(newest);
                if (info is not null) Notified?.Invoke(this, info);
            }
            _primed = true;
            _lastSeenId = Math.Max(_lastSeenId, maxId);
        }
        catch (Exception ex) { log.Debug($"Notification poll failed: {ex.Message}"); }
    }

    private static NotificationInfo? Extract(UserNotification n)
    {
        try
        {
            var app = n.AppInfo?.DisplayInfo?.DisplayName ?? "Notification";
            var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding is null) return null;
            var text = binding.GetTextElements();
            var title = text.Count > 0 ? text[0].Text : app;
            var body = string.Join("  ", text.Skip(1).Select(t => t.Text));
            return new NotificationInfo(app, title, body);
        }
        catch { return null; }
    }

    public void Dispose() => _timer.Stop();
}
