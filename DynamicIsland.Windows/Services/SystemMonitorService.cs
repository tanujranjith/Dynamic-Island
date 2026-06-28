using System.Net.NetworkInformation;
using DynamicIsland.Windows.Interop;
using DynamicIsland.Windows.Models;

namespace DynamicIsland.Windows.Services;

/// <summary>
/// Lightweight system stats. CPU is computed as Task Manager does: raw busy% (from GetSystemTimes,
/// time-averaged so it can't spike) scaled by the real current/base frequency ratio (CallNtPowerInformation).
/// On a downclocked-idle CPU that scaling is what makes "busy 23% of the time" read as ~6% utility.
/// RAM = GlobalMemoryStatusEx; network = sum of physical adapter throughput.
/// </summary>
public sealed class SystemMonitorService : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly int _cores = Math.Max(1, Environment.ProcessorCount);
    private Thread? _thread;
    private long _lastIdle, _lastKernel, _lastUser;
    private long _lastBytes;
    private long _lastTicks;
    private double _cpuEma = -1;

    public event EventHandler<SystemStats>? Changed;
    public SystemStats Current { get; private set; } = SystemStats.Empty;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "DynamicIsland.SysMon" };
        _thread.Start();
    }

    private void Loop()
    {
        Prime();
        var sincePublish = 0;
        while (!_shutdown.IsCancellationRequested)
        {
            _shutdown.Token.WaitHandle.WaitOne(700);
            if (_shutdown.IsCancellationRequested) break;
            var next = Read();
            if (++sincePublish >= 2)
            {
                sincePublish = 0;
                if (next != Current) { Current = next; Changed?.Invoke(this, next); }
            }
        }
    }

    private void Prime()
    {
        NativeMethods.GetSystemTimes(out _lastIdle, out _lastKernel, out _lastUser);
        _lastBytes = TotalBytes();
        _lastTicks = Environment.TickCount64;
    }

    private SystemStats Read()
    {
        int cpu = (int)Math.Round(Math.Max(0, _cpuEma));
        int ram = 0;
        try
        {
            if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                var idleDelta = idle - _lastIdle;
                var totalDelta = (kernel - _lastKernel) + (user - _lastUser); // kernel already includes idle
                _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
                if (totalDelta > 0)
                {
                    var busy = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
                    var raw = Math.Clamp(busy * FrequencyRatio(), 0, 100); // -> Task Manager's "utility"
                    _cpuEma = _cpuEma < 0 ? raw : _cpuEma * 0.8 + raw * 0.2; // steady like Task Manager
                    cpu = (int)Math.Round(_cpuEma);
                }
            }

            var mem = new NativeMethods.MemoryStatusEx
            { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
            if (NativeMethods.GlobalMemoryStatusEx(ref mem)) ram = (int)mem.MemoryLoad;
        }
        catch { }

        var net = "—";
        double netPerSec = 0;
        try
        {
            var now = Environment.TickCount64;
            var bytes = TotalBytes();
            var seconds = Math.Max(0.001, (now - _lastTicks) / 1000.0);
            var perSec = Math.Max(0, (bytes - _lastBytes) / seconds);
            _lastBytes = bytes; _lastTicks = now;
            netPerSec = perSec;
            net = FormatRate(perSec);
        }
        catch { }

        return new SystemStats(cpu, ram, net, netPerSec);
    }

    // Average current/base frequency ratio across cores. >1 under turbo, <1 when downclocked (idle).
    private double FrequencyRatio()
    {
        try
        {
            var info = new NativeMethods.ProcessorPowerInformation[_cores];
            var size = (uint)(System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.ProcessorPowerInformation>() * _cores);
            if (NativeMethods.CallNtPowerInformation(11, nint.Zero, 0, info, size) != 0) return 1.0;
            double cur = 0, max = 0;
            foreach (var p in info) { cur += p.CurrentMhz; max += p.MaxMhz; }
            if (max <= 0) return 1.0;
            return Math.Clamp(cur / max, 0.05, 3.0);
        }
        catch { return 1.0; }
    }

    private static readonly string[] VirtualMarkers =
        ["virtual", "hyper-v", "vmware", "vethernet", "pseudo", "loopback", "tap", "tunnel", "bluetooth", "wan miniport"];

    private static long TotalBytes()
    {
        long total = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)) continue;
            var desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
            if (VirtualMarkers.Any(m => desc.Contains(m))) continue;
            var s = nic.GetIPv4Statistics();
            total += s.BytesReceived + s.BytesSent;
        }
        return total;
    }

    private static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024) return $"{bytesPerSec / (1024 * 1024):0.0} MB/s";
        if (bytesPerSec >= 1024) return $"{bytesPerSec / 1024:0.0} KB/s";
        return $"{bytesPerSec:0} B/s";
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _shutdown.Dispose();
    }
}
