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
public sealed partial class CalendarService(LoggingService log) : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(2) };
    private AppointmentStore? _store;

    public event EventHandler<MeetingInfo?>? Changed;
    public MeetingInfo? Current { get; private set; }

    public async Task StartAsync()
    {
        try
        {
            _store = await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);
            _timer.Tick += async (_, _) => await RefreshAsync();
            _timer.Start();
            await RefreshAsync();
        }
        catch (Exception ex) { log.Info($"Calendar access unavailable: {ex.Message}"); }
    }

    public async Task RefreshAsync()
    {
        if (_store is null) return;
        try
        {
            var appts = await _store.FindAppointmentsAsync(DateTimeOffset.Now.AddMinutes(-5), TimeSpan.FromHours(18));
            var next = appts
                .Where(a => a.StartTime + a.Duration > DateTimeOffset.Now && !a.AllDay)
                .OrderBy(a => a.StartTime).FirstOrDefault();
            MeetingInfo? info = next is null ? null
                : new MeetingInfo(string.IsNullOrWhiteSpace(next.Subject) ? "Meeting" : next.Subject,
                    next.StartTime, ExtractUrl((next.Details ?? "") + " " + (next.Location ?? "")));
            if (info != Current) { Current = info; Changed?.Invoke(this, info); }
        }
        catch (Exception ex) { log.Debug($"Calendar refresh failed: {ex.Message}"); }
    }

    private static string ExtractUrl(string text)
    {
        var m = MeetingLinkRegex().Match(text ?? "");
        return m.Success ? m.Value : "";
    }

    [GeneratedRegex(@"https?://[^\s""'<>]*(teams\.microsoft|zoom\.us|meet\.google|webex)[^\s""'<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex MeetingLinkRegex();

    public void Dispose() => _timer.Stop();
}
