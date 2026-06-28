using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using DynamicIsland.Windows.Models;
using Forms = System.Windows.Forms;

namespace DynamicIsland.Windows.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _alwaysOnTop;
    private readonly Forms.ToolStripMenuItem _clickThrough;
    private readonly AppSettings _settings;
    private readonly Action _openSettings;
    private readonly Action _recenter;
    private readonly Func<Task> _save;
    private readonly Action _quit;

    public TrayService(AppSettings settings, Action openSettings, Action recenter,
        Func<Task> save, Action quit)
    {
        _settings = settings;
        _openSettings = openSettings;
        _recenter = recenter;
        _save = save;
        _quit = quit;

        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(),
            ShowImageMargin = false
        };
        menu.Items.Add("Open Settings", null, (_, _) => OnUi(_openSettings));
        menu.Items.Add("Recenter Island", null, (_, _) => OnUi(_recenter));
        menu.Items.Add(new Forms.ToolStripSeparator());
        _alwaysOnTop = new Forms.ToolStripMenuItem("Always on Top") { Checked = settings.AlwaysOnTop, CheckOnClick = true };
        _alwaysOnTop.CheckedChanged += (_, _) => OnUi(async () =>
        {
            _settings.AlwaysOnTop = _alwaysOnTop.Checked;
            await _save();
        });
        menu.Items.Add(_alwaysOnTop);
        _clickThrough = new Forms.ToolStripMenuItem("Click-through when Compact") { Checked = settings.ClickThroughWhenCompact, CheckOnClick = true };
        _clickThrough.CheckedChanged += (_, _) => OnUi(async () =>
        {
            _settings.ClickThroughWhenCompact = _clickThrough.Checked;
            await _save();
        });
        menu.Items.Add(_clickThrough);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Restart App", null, (_, _) => OnUi(Restart));
        menu.Items.Add("Quit", null, (_, _) => OnUi(_quit));

        _icon = new Forms.NotifyIcon
        {
            Text = "Dynamic Island",
            Icon = CreateIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => OnUi(_openSettings);
    }

    public void SyncChecks()
    {
        _alwaysOnTop.Checked = _settings.AlwaysOnTop;
        _clickThrough.Checked = _settings.ClickThroughWhenCompact;
    }

    public void ShowNotification(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
    }

    private void Restart()
    {
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        _quit();
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var shadow = new SolidBrush(Color.FromArgb(80, 0, 0, 0));
        using var body = new SolidBrush(Color.FromArgb(255, 13, 17, 24));
        using var accent = new SolidBrush(Color.FromArgb(255, 64, 153, 255));
        graphics.FillEllipse(shadow, 7, 19, 52, 30);
        graphics.FillEllipse(body, 5, 15, 54, 30);
        graphics.FillEllipse(accent, 41, 23, 10, 10);
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static void OnUi(Action action) => System.Windows.Application.Current.Dispatcher.BeginInvoke(action);
    private static void OnUi(Func<Task> action) => System.Windows.Application.Current.Dispatcher.BeginInvoke(async () => await action());

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}
