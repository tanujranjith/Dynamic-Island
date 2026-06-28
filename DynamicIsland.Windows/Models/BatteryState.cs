namespace DynamicIsland.Windows.Models;

public sealed record BatteryState(bool IsAvailable, int Percentage, bool IsCharging, bool IsPluggedIn, int MinutesRemaining = -1)
{
    public static readonly BatteryState Unavailable = new(false, 0, false, false);

    public string TimeRemainingText => MinutesRemaining <= 0
        ? string.Empty
        : MinutesRemaining >= 60 ? $"{MinutesRemaining / 60}h {MinutesRemaining % 60}m left" : $"{MinutesRemaining}m left";
}
