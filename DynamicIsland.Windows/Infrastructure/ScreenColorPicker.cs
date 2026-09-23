using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DynamicIsland.Windows.Infrastructure;

internal static class ScreenColorPicker
{
    internal static string Sample(Drawing.Bitmap bitmap, Drawing.Rectangle bounds, Drawing.Point point)
    {
        var color = bitmap.GetPixel(Math.Clamp(point.X - bounds.X, 0, bitmap.Width - 1), Math.Clamp(point.Y - bounds.Y, 0, bitmap.Height - 1));
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }
    // Per-monitor native windows use physical pixels, including negative monitor coordinates.
    // Screenshots exist only in memory for this interaction; nothing is stored or sent to Q.
    public static async Task<string?> PickAsync()
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlays = new List<Forms.Form>();
        var captures = new List<Drawing.Bitmap>();
        try
        {
            // Capture all screens before displaying any overlay.
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var bounds = screen.Bounds;
                var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height);
                captures.Add(bitmap);
                using (var graphics = Drawing.Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(bounds.Location, Drawing.Point.Empty, bounds.Size);
                var overlay = new Forms.Form
                {
                    FormBorderStyle = Forms.FormBorderStyle.None, StartPosition = Forms.FormStartPosition.Manual,
                    AutoScaleMode = Forms.AutoScaleMode.None, Bounds = bounds, TopMost = true, ShowInTaskbar = false,
                    BackgroundImage = bitmap, BackgroundImageLayout = Forms.ImageLayout.None,
                    Cursor = Forms.Cursors.Cross, KeyPreview = true
                };
                overlay.MouseDown += (_, e) =>
                {
                    if (e.Button == Forms.MouseButtons.Right) { result.TrySetResult(null); return; }
                    if (e.Button != Forms.MouseButtons.Left) return;
                    result.TrySetResult(Sample(bitmap, bounds, Forms.Cursor.Position));
                };
                overlay.KeyDown += (_, e) => { if (e.KeyCode == Forms.Keys.Escape) { e.Handled = true; result.TrySetResult(null); } };
                overlay.FormClosed += (_, _) => result.TrySetResult(null);
                overlays.Add(overlay);
            }
            if (overlays.Count == 0) return null;
            foreach (var overlay in overlays) overlay.Show();
            overlays.FirstOrDefault(o => o.Bounds.Contains(Forms.Cursor.Position))?.Activate();
            return await result.Task;
        }
        finally
        {
            foreach (var overlay in overlays) { overlay.BackgroundImage = null; overlay.Close(); overlay.Dispose(); }
            foreach (var bitmap in captures) bitmap.Dispose();
        }
    }
}
