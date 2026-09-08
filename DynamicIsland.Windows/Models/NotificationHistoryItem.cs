namespace DynamicIsland.Windows.Models;

public sealed record NotificationHistoryItem(
    Guid Id,
    string App,
    string Title,
    string Body,
    DateTimeOffset CreatedAt,
    bool IsDismissed = false,
    string AppId = "");

public sealed record FocusModeState(bool Enabled);

public sealed record AudioOutputDevice(string Id, string Name, bool IsCurrent);
