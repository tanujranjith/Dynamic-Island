using Windows.ApplicationModel.DataTransfer;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace DynamicIsland.Windows.Services;

/// <summary>
/// Reads Windows clipboard history (Win+V) on demand and copies an item back. Requires clipboard history
/// to be enabled; otherwise returns nothing. Best-effort, no special capability needed.
/// </summary>
public sealed class ClipboardService(LoggingService log)
{
    public event EventHandler? StatusChanged;
    private bool _enabled;
    private int _generation;
    public Models.IntegrationStatus Status { get; private set; } = Models.IntegrationStatus.Disabled;
    private void SetStatus(Models.IntegrationStatus status) { if (Status == status) return; Status = status; StatusChanged?.Invoke(this, EventArgs.Empty); }
    public void Configure(bool enabled)
    { _enabled = enabled; _generation++; SetStatus(enabled ? new(Models.IntegrationState.Ready, "Open clipboard history to view recent items") : Models.IntegrationStatus.Disabled); }
    public async Task<IReadOnlyList<string>> GetRecentTextAsync(int max = 6)
    {
        if (!_enabled) return [];
        var generation = _generation;
        try
        {
            var result = await WinClipboard.GetHistoryItemsAsync();
            if (!_enabled || generation != _generation) return [];
            if (result.Status != ClipboardHistoryItemsResultStatus.Success)
            {
                SetStatus(new(Models.IntegrationState.PermissionRequired, "Enable clipboard history in Windows Settings.", "ms-settings:clipboard"));
                return [];
            }
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
            if (!_enabled || generation != _generation) return [];
            SetStatus(new(Models.IntegrationState.Ready, items.Count == 0 ? "Clipboard history is empty" : "Ready"));
            return items;
        }
        catch (Exception ex) { log.Debug($"Clipboard history unavailable: {ex.Message}"); if (_enabled && generation == _generation) SetStatus(new(Models.IntegrationState.Error, "Clipboard history could not load. Retry.")); return []; }
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
