namespace DynamicIsland.Windows.Models;

public enum VisionLevel { Gray, Green, Red }

public readonly record struct VisionDecision(VisionLevel Level, string Status, bool Alert);

/// <summary>
/// Pure, framework-independent port of the Python project's <c>decide_ui_state</c>. Deterministic,
/// no I/O, no OpenCV — this is the single source of truth for what the island shows, and is covered
/// 1:1 by unit tests. Branch order is preserved exactly from the original policy.
/// </summary>
public static class VisionPolicy
{
    public const string RedHex = "#FF3B30";
    public const string GreenHex = "#30D158";
    public const string GrayHex = "#636366";

    public static string ColorHex(VisionLevel level) => level switch
    {
        VisionLevel.Red => RedHex,
        VisionLevel.Green => GreenHex,
        _ => GrayHex
    };

    public static VisionDecision Decide(bool detectorReady, bool privacyOn, bool enrolled,
        bool enrolling, int peopleCount, IReadOnlySet<int> ownerIndexes)
    {
        var n = Math.Max(0, peopleCount);
        var owners = ownerIndexes.Where(i => i >= 0 && i < n).ToHashSet();

        // Privacy needs a registered owner before it can tell "you" from "someone else".
        if (privacyOn && !enrolled)
            return new(VisionLevel.Gray, enrolling ? "Registering your face..." : "Register your face", false);

        // No reliable model — degraded mode. We can still warn, but with lower confidence.
        if (!detectorReady)
            return n > 0
                ? new(VisionLevel.Red, $"{n} person/people detected (fallback)", true)
                : new(VisionLevel.Gray, "Install person model for reliable monitoring", false);

        // Privacy off: a plain presence monitor.
        if (!privacyOn)
        {
            if (n == 0) return new(VisionLevel.Green, "No person detected", false);
            if (n == 1) return new(VisionLevel.Red, "Person detected", true);
            return new(VisionLevel.Red, $"{n} people detected", true);
        }

        // Privacy on and enrolled: distinguish the owner from strangers.
        if (n == 0) return new(VisionLevel.Green, "All clear", false);
        if (n == 1 && owners.Count == 1 && owners.Contains(0)) return new(VisionLevel.Green, "Just you", false);
        if (n > 1) return new(VisionLevel.Red, $"{n} people detected", true);
        return new(VisionLevel.Red, "Unknown person detected", true);
    }
}
