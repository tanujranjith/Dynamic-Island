using Microsoft.Win32;
using System.Windows.Threading;
using DynamicIsland.Windows.Models;

namespace DynamicIsland.Windows.Services;

public sealed class ThemeService : IDisposable
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private bool _lastSystemDark;

    public event EventHandler? SystemThemeChanged;

    public ThemeService()
    {
        _lastSystemDark = IsSystemDark();
        _timer.Tick += (_, _) =>
        {
            var current = IsSystemDark();
            if (current == _lastSystemDark) return;
            _lastSystemDark = current;
            SystemThemeChanged?.Invoke(this, EventArgs.Empty);
        };
        _timer.Start();
    }

    public bool IsDark(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => IsSystemDark()
    };

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 0)) == 0;
        }
        catch { return true; }
    }

    public void Dispose() => _timer.Stop();
}
