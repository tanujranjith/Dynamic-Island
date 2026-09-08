namespace DynamicIsland.Windows.Infrastructure;

public static class WindowSizingPolicy
{
    public static double BoundedDimension(double desired, double available, double margin = 24) =>
        Math.Min(Math.Max(1, desired), Math.Max(1, available - margin));

    public static double WidgetViewport(double lane, bool airPods) => Math.Max(1, lane - (airPods ? Math.Min(340, lane * 0.5) + 8 : 0));

    public static double WidgetWidth(double viewport, int count, double minimum) =>
        count <= 0 ? minimum : Math.Max(minimum, (viewport - count * 8) / count);
    public static double EffectiveDimension(double requested, double actual)
    {
        if (!double.IsNaN(requested) && !double.IsInfinity(requested) && requested > 0)
            return requested;
        return actual > 0 ? actual : 1d;
    }

    public static double AntiClippingDimension(double configured, double desired, double maximum)
    {
        var safeMaximum = Math.Max(configured, maximum);
        return Math.Clamp(Math.Max(configured, desired), configured, safeMaximum);
    }
}
