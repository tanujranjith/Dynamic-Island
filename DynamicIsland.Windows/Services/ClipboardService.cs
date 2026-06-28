using Windows.ApplicationModel.DataTransfer;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace DynamicIsland.Windows.Services;

/// <summary>
/// Reads Windows clipboard history (Win+V) on demand and copies an item back. Requires clipboard history
/// to be enabled; otherwise returns nothing. Best-effort, no special capability needed.
/// </summary>
public sealed class ClipboardService(LoggingService log)
{
    public async Task<IReadOnlyList<string>> GetRecentTextAsync(int max = 6)
    {
        try
        {
            var result = await WinClipboard.GetHistoryItemsAsync();
            if (result.Status != ClipboardHistoryItemsResultStatus.Success) return [];
            var items = new List<string>();
            foreach (var item in result.Items)
            {
                if (items.Count >= max) break;
                if (item.Content.Contains(StandardDataFormats.Text))
                {
                    var text = await item.Content.GetTextAsync();
                    if (!string.IsNullOrWhiteSpace(text)) items.Add(text.Trim());
                }
            }
            return items;
        }
        catch (Exception ex) { log.Debug($"Clipboard history unavailable: {ex.Message}"); return []; }
    }

    public void CopyText(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            WinClipboard.SetContent(package);
        }
        catch (Exception ex) { log.Debug($"Clipboard set failed: {ex.Message}"); }
    }
}
