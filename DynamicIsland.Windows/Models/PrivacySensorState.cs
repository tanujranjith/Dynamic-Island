namespace DynamicIsland.Windows.Models;

/// <summary>
/// Snapshot of webcam/microphone usage. Carries the friendly display names of the
/// apps currently holding each sensor (resolved from the Windows consent store),
/// so the expanded island can tell the user *where* the sensor is being used.
/// </summary>
public sealed class PrivacySensorState : IEquatable<PrivacySensorState>
{
    public static readonly PrivacySensorState None =
        new(Array.Empty<string>(), Array.Empty<string>());

    public IReadOnlyList<string> CameraApps { get; }
    public IReadOnlyList<string> MicrophoneApps { get; }

    public bool Camera => CameraApps.Count > 0;
    public bool Microphone => MicrophoneApps.Count > 0;
    public bool Any => Camera || Microphone;

    public PrivacySensorState(IReadOnlyList<string>? cameraApps, IReadOnlyList<string>? microphoneApps)
    {
        CameraApps = Normalize(cameraApps);
        MicrophoneApps = Normalize(microphoneApps);
    }

    // Legacy bool shape (kept for compat/tests): maps to placeholder-free empty lists
    // when false, or a single "Unknown app" entry when true and no name is known.
    public PrivacySensorState(bool camera, bool microphone)
        : this(
            camera ? new[] { "Unknown app" } : Array.Empty<string>(),
            microphone ? new[] { "Unknown app" } : Array.Empty<string>())
    {
    }

    private static IReadOnlyList<string> Normalize(IReadOnlyList<string>? apps)
    {
        if (apps is null || apps.Count == 0) return Array.Empty<string>();
        var cleaned = apps
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return cleaned;
    }

    public bool Equals(PrivacySensorState? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        return CameraApps.SequenceEqual(other.CameraApps, StringComparer.OrdinalIgnoreCase) &&
               MicrophoneApps.SequenceEqual(other.MicrophoneApps, StringComparer.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) => Equals(obj as PrivacySensorState);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var app in CameraApps) hash.Add(app.ToUpperInvariant());
        hash.Add("|");
        foreach (var app in MicrophoneApps) hash.Add(app.ToUpperInvariant());
        return hash.ToHashCode();
    }

    public static bool operator ==(PrivacySensorState? left, PrivacySensorState? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(PrivacySensorState? left, PrivacySensorState? right) => !(left == right);
}
