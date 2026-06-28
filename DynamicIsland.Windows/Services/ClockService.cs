using System.Windows.Threading;

namespace DynamicIsland.Windows.Services;

public sealed class ClockService : IDisposable
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    public event EventHandler<DateTimeOffset>? Tick;

    public ClockService()
    {
        _timer.Tick += OnTick;
        ScheduleNext();
    }

    public void Start()
    {
        Tick?.Invoke(this, DateTimeOffset.Now);
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Tick?.Invoke(this, DateTimeOffset.Now);
        ScheduleNext();
    }

    private void ScheduleNext()
    {
        var now = DateTimeOffset.Now;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, 1000 - now.Millisecond));
    }

    public void Dispose() => _timer.Stop();
}
