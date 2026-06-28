namespace DynamicIsland.Windows.Models;

public enum VisionAvailability { Disabled, Initializing, Running, CameraError, Unsupported }

/// <summary>
/// Immutable snapshot published by <c>VisionService</c>. A value-equatable record so the service can
/// publish only on change. Owner person-indexes are stored as a canonical string (not a list) so two
/// equal frames compare equal — a list/array would compare by reference and fire the event every tick.
/// </summary>
public sealed record VisionState
{
    public static readonly VisionState Disabled = new() { Availability = VisionAvailability.Disabled };

    public VisionAvailability Availability { get; init; } = VisionAvailability.Disabled;
    public bool DetectorReady { get; init; }
    public bool PrivacyOn { get; init; }
    public bool Enrolled { get; init; }
    public bool Enrolling { get; init; }
    public int PeopleCount { get; init; }

    /// <summary>Sorted, comma-joined owner person-indexes, e.g. "0". Empty when no owner is recognised.</summary>
    public string OwnerSignature { get; init; } = string.Empty;
    public DateTimeOffset LastFrameUtc { get; init; }

    private static readonly IReadOnlySet<int> EmptyOwners = new HashSet<int>();
    private IReadOnlySet<int> Owners => string.IsNullOrEmpty(OwnerSignature)
        ? EmptyOwners
        : OwnerSignature.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();

    public VisionDecision Decision =>
        VisionPolicy.Decide(DetectorReady, PrivacyOn, Enrolled, Enrolling, PeopleCount, Owners);

    public VisionLevel Level => Availability == VisionAvailability.Running ? Decision.Level : VisionLevel.Gray;
    public bool Alert => Availability == VisionAvailability.Running && Decision.Alert;
    public string ColorHex => VisionPolicy.ColorHex(Level);

    public string StatusText => Availability switch
    {
        VisionAvailability.Running => Decision.Status,
        VisionAvailability.Initializing => "Starting camera…",
        VisionAvailability.CameraError => "Camera unavailable",
        VisionAvailability.Unsupported => "Camera not supported",
        _ => "Camera off"
    };

    public static string BuildSignature(IEnumerable<int> ownerIndexes) =>
        string.Join(',', ownerIndexes.Distinct().OrderBy(i => i));
}
