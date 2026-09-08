using DynamicIsland.Windows.Models;
namespace DynamicIsland.Windows.Infrastructure;

public static class ActivityPolicy
{
    public static IslandActivity Select(bool alarmRinging, bool timerDone, bool qActive, IslandActivity pin,
        bool pinAvailable, bool muted, bool media, bool audio, bool charging)
    {
        if (alarmRinging) return IslandActivity.Alarm;
        if (timerDone) return IslandActivity.Timer;
        if (qActive) return IslandActivity.Q;
        if (pinAvailable && pin is IslandActivity.Media or IslandActivity.Timer) return pin;
        if (muted) return IslandActivity.Muted;
        if (media) return IslandActivity.Media;
        if (audio) return IslandActivity.Audio;
        return charging ? IslandActivity.Charging : IslandActivity.None;
    }
}
