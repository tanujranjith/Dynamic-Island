using System.Text.Json;
using DynamicIsland.Windows.Models;

namespace DynamicIsland.Windows.Services;

/// <summary>Small local-only notification history used by the island's optional history popover.</summary>
public sealed class NotificationHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private const int MaxItems = 100;
    private readonly LoggingService _log;
    private readonly string _path;
    private readonly List<NotificationHistoryItem> _items = [];

    public NotificationHistoryService(LoggingService log)
    {
        _log = log;
        var directory = DynamicIsland.Windows.Infrastructure.AppDataPaths.Root;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "notification-history.json");
        Load();
    }

    public IReadOnlyList<NotificationHistoryItem> Items => _items;

    public NotificationHistoryItem Add(string app, string title, string body, DateTimeOffset? createdAt = null, string appId = "")
    {
        Prune();
        var item = new NotificationHistoryItem(Guid.NewGuid(), app, title, body, createdAt ?? DateTimeOffset.Now, AppId: appId);
        _items.Insert(0, item);
        Trim();
        Save();
        return item;
    }

    public void Dismiss(Guid id)
    {
        var index = _items.FindIndex(x => x.Id == id);
        if (index < 0) return;
        _items.RemoveAt(index);
        Save();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Save();
    }

    public void PruneAndSave()
    {
        if (Prune()) Save();
    }

    private bool Prune()
    {
        var cutoff = DateTimeOffset.Now - Retention;
        var before = _items.Count;
        _items.RemoveAll(x => x.CreatedAt < cutoff);
        Trim();
        return before != _items.Count;
    }

    private void Trim()
    {
        if (_items.Count > MaxItems) _items.RemoveRange(MaxItems, _items.Count - MaxItems);
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<NotificationHistoryItem>>(File.ReadAllText(_path), JsonOptions);
            if (loaded is not null) _items.AddRange(loaded.OrderByDescending(x => x.CreatedAt));
            Prune();
            Trim();
        }
        catch (Exception ex)
        {
            _log.Debug($"Notification history could not be loaded: {ex.Message}");
            _items.Clear();
        }
    }

    private void Save()
    {
        try
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_items, JsonOptions));
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) { _log.Debug($"Notification history could not be saved: {ex.Message}"); }
    }
}
