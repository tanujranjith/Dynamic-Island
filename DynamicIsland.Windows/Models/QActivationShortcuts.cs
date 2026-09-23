namespace DynamicIsland.Windows.Models;

[Flags]
public enum QActivationShortcuts
{
    None = 0,
    CtrlAltQ = 1,
    ShiftA = 2,
    VarSequence = 4,
    ShiftComma = 8,
    ShiftPeriod = 16,
    All = CtrlAltQ | ShiftA | VarSequence | ShiftComma | ShiftPeriod
}

public static class QActivationPolicy
{
    public static QActivationShortcuts Resolve(QActivationShortcuts? selected, string? legacy) =>
        selected is { } value ? value & QActivationShortcuts.All :
        string.Equals(legacy, "Shift+A", StringComparison.OrdinalIgnoreCase)
            ? QActivationShortcuts.ShiftA : QActivationShortcuts.CtrlAltQ;
}
