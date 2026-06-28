using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DynamicIsland.Windows.Interop;

namespace DynamicIsland.Windows.Views;

public partial class TimerAlarmWindow : Window
{
    private readonly Window _owner;

    public TimerAlarmWindow(Window owner)
    {
        InitializeComponent();
        _owner = owner;
        Owner = owner;
        Deactivated += (_, _) => { if (IsVisible && !IsMouseOver) Hide(); };
    }

    public void PositionBelowOwner()
    {
        var ownerHandle = new WindowInteropHelper(_owner).Handle;
        if (!NativeMethods.GetWindowRect(ownerHandle, out var rect)) return;
        var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(ownerHandle));
        Left = (rect.Left + (rect.Width - Width * dpi / 96d) / 2) * 96d / dpi;
        Top = (rect.Bottom + 8) * 96d / dpi;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}
