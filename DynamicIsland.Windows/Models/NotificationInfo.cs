namespace DynamicIsland.Windows.Models;

public sealed record NotificationInfo(string App, string Title, string Body, uint? Id = null,
    DateTimeOffset? CreatedAt = null, string AppId = "")
{
    public string Identity => $"{AppId}|{Id}|{CreatedAt:O}";
}
public sealed record NotificationGroup(IReadOnlyList<NotificationHistoryItem> Items, DateTimeOffset EnqueuedAt, bool Summary = false)
{
    public NotificationHistoryItem Latest => Items[^1];
    public string Title => Summary ? $"{Items.Count} notifications received" : Items.Count > 1 ? $"{Latest.Title} (+{Items.Count - 1})" : Latest.Title;
}
