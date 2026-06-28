namespace DynamicIsland.Windows.Models;

public enum AudioAvailability { Available, Unsupported, Unknown }

public sealed record AudioState
{
    public static readonly AudioState Unknown = new() { Availability = AudioAvailability.Unknown };
    public AudioAvailability Availability { get; init; } = AudioAvailability.Available;
    public int MasterVolumePercent { get; init; }
    public bool SystemMuted { get; init; }
    public bool AnySessionMuted { get; init; }
    public bool ActiveAudioOutput { get; init; }
    public int ActiveSessionCount { get; init; }
    public int AudibleSessionCount { get; init; }
    public string OutputDeviceName { get; init; } = string.Empty;
    public string StatusText => Availability != AudioAvailability.Available
        ? "Audio unavailable"
        : SystemMuted ? "System muted"
        : AnySessionMuted && ActiveSessionCount > 0 ? "App muted"
        : ActiveAudioOutput ? "Audio active"
        : "Audio idle";
}
