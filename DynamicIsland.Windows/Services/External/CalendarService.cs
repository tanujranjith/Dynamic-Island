using System.Text.RegularExpressions;
using System.Windows.Threading;
using Windows.ApplicationModel.Appointments;

namespace DynamicIsland.Windows.Services;

public sealed record MeetingInfo(string Title, DateTimeOffset Start, string JoinUrl)
{
    public string CountdownText
    {
        get
        {
            var mins = (int)Math.Round((Start - DateTimeOffset.Now).TotalMinutes);
            if (mins <= 0) return "now";
            if (mins < 60) return $"in {mins}m";
            return $"in {mins / 60}h {mins % 60}m";
        }
    }
}

/// <summary>
/// Surfaces your next calendar appointment via the Windows AppointmentStore (read-only). Pulls a join URL
/// (Teams/Zoom/Meet) out of the body when present. Requires calendar access; unavailable unpackaged → idle.
/// </summary>
public sealed partial class CalendarService : IDisposable
{
    private readonly LoggingService _log;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(2) };
    private AppointmentStore? _store;
    private int _generation;
    private bool _enabled, _refreshing;
    public event EventHandler<MeetingInfo?>? Changed;
    public event EventHandler? StatusChanged;
    public MeetingInfo? Current { get; private set; }
    public DynamicIsland.Windows.Models.IntegrationStatus Status { get; private set; } = DynamicIsland.Windows.Models.IntegrationStatus.Disabled;
    public CalendarService(LoggingService log) { _log = log; _timer.Tick += async (_, _) => await RefreshAsync(); }
    private void SetStatus(DynamicIsland.Windows.Models.IntegrationState state, string message, string? uri = null)
    { var value = new Models.IntegrationStatus(state, message, uri); if (Status == value) return; Status = value; StatusChanged?.Invoke(this, EventArgs.Empty); }
    public async Task StartAsync(bool requestPermission = false)
    {
        if (_enabled) return;
        _enabled = true; var generation = ++_generation;
        SetStatus(Models.IntegrationState.Connecting, "Connecting…");
        try
        {
            if (!requestPermission && (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) || global::Windows.Security.Authorization.AppCapabilityAccess.AppCapability.Create("appointments").CheckAccess() != global::Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed))
            { SetStatus(Models.IntegrationState.PermissionRequired, "Set up calendar access.", "ms-settings:privacy-calendar"); return; }
            var store = await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);
            if (!_enabled || generation != _generation) return;
            _store = store;
            if (_store is null) { SetStatus(Models.IntegrationState.PermissionRequired, "Allow calendar access in Windows Settings.", "ms-settings:privacy-calendar"); return; }
            await RefreshAsync();
            if (_enabled && generation == _generation) _timer.Start();
        }
        catch (UnauthorizedAccessException) { if (generation == _generation) SetStatus(Models.IntegrationState.PermissionRequired, "Calendar access is required.", "ms-settings:privacy-calendar"); }
        catch (Exception ex) { if (generation == _generation) { SetStatus(Models.IntegrationState.Unavailable, "Calendar access is unavailable in this Windows/app configuration."); _log.Debug(ex.Message); } }
    }
    public async Task RetryAsync() { Stop(); await StartAsync(true); }
    public void Stop()
    {
        _enabled = false; _generation++; _timer.Stop(); _store = null; Current = null;
        SetStatus(Models.IntegrationState.Disabled, "Disabled"); Changed?.Invoke(this, null);
    }
    public async Task RefreshAsync()
    {
        if (!_enabled || _store is null || _refreshing) return;
        _refreshing = true; var generation = _generation;
        try
        {
            var appts = await _store.FindAppointmentsAsync(DateTimeOffset.Now.AddMinutes(-5), TimeSpan.FromHours(18));
            if (!_enabled || generation != _generation) return;
            var next = appts.Where(a => a.StartTime + a.Duration > DateTimeOffset.Now && !a.AllDay).OrderBy(a => a.StartTime).FirstOrDefault();
            Current = next is null ? null : new(string.IsNullOrWhiteSpace(next.Subject) ? "Meeting" : next.Subject, next.StartTime, ExtractUrl((next.Details ?? "") + " " + (next.Location ?? "")));
            SetStatus(Models.IntegrationState.Ready, Current is null ? "No upcoming meetings" : "Connected"); Changed?.Invoke(this, Current);
        }
        catch (UnauthorizedAccessException) { if (generation == _generation) { Current = null; SetStatus(Models.IntegrationState.PermissionRequired, "Calendar access was revoked.", "ms-settings:privacy-calendar"); Changed?.Invoke(this, null); } }
        catch (Exception ex) { if (generation == _generation) { Current = null; SetStatus(Models.IntegrationState.Error, "Calendar could not refresh. Retry."); Changed?.Invoke(this, null); _log.Debug(ex.Message); } }
        finally { _refreshing = false; }
    }
    private static string ExtractUrl(string text) { var m = MeetingLinkRegex().Match(text); return m.Success ? m.Value : ""; }
    [GeneratedRegex(@"https?://[^\s""'<>]*(teams\.microsoft|zoom\.us|meet\.google|webex)[^\s""'<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex MeetingLinkRegex();
    public void Dispose() => Stop();
}
