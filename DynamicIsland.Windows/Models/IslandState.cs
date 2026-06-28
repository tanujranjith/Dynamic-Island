namespace DynamicIsland.Windows.Models;

public enum IslandDisplayState { Compact, Expanded }
public enum IslandActivity { None, Media, Muted, Audio, Timer, Alarm, Charging }

public sealed record IslandState(
    IslandDisplayState DisplayState,
    IslandActivity Activity,
    string PrimaryText,
    string SecondaryText);
