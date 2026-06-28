using System.Runtime.InteropServices;
using System.Windows.Threading;
using DynamicIsland.Windows.Models;

namespace DynamicIsland.Windows.Services;

public sealed class BatteryService : IDisposable
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(2)
    };

    private bool _warned;

    public event EventHandler<BatteryState>? Changed;
    public event EventHandler<int>? LowBattery; // fires once per low episode with the percentage
    public BatteryState Current { get; private set; } = BatteryState.Unavailable;
    public int LowThreshold { get; set; } = 15;
    public bool WarningsEnabled { get; set; } = true;

    public BatteryService() => _timer.Tick += (_, _) => Refresh();

    public void Start()
    {
        Refresh();
        _timer.Start();
    }

    public void Refresh()
    {
        var status = new SystemPowerStatus();
        BatteryState next;
        if (!GetSystemPowerStatus(ref status) || status.BatteryFlag == 128 || status.BatteryLifePercent == 255)
        {
            next = BatteryState.Unavailable;
        }
        else
        {
            var pluggedIn = status.ACLineStatus == 1;
            var minutes = status.BatteryLifeTime >= 0 ? status.BatteryLifeTime / 60 : -1;
            next = new BatteryState(true, Math.Clamp((int)status.BatteryLifePercent, 0, 100),
                pluggedIn && status.BatteryLifePercent < 100, pluggedIn, minutes);
        }

        // Low-battery alert: fire once when crossing below the threshold on battery; reset on charge/recover.
        if (next.IsAvailable && WarningsEnabled && !next.IsCharging && next.Percentage <= LowThreshold)
        {
            if (!_warned) { _warned = true; LowBattery?.Invoke(this, next.Percentage); }
        }
        else if (next.IsCharging || next.Percentage > LowThreshold + 5) _warned = false;

        if (next == Current) return;
        Current = next;
        Changed?.Invoke(this, next);
    }

    public void Dispose() => _timer.Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(ref SystemPowerStatus status);
}
