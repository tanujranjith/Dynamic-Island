namespace DynamicIsland.Windows.Infrastructure;

/// <summary>Positions visible content, allowing the transparent canvas to extend offscreen.</summary>
public static class IslandPlacement
{
    public static (double X, double Y) Place(double left, double top, double screenWidth, double screenHeight,
        double canvasWidth, double contentWidth, double contentHeight, double insetTop,
        int horizontalAnchor, int verticalAnchor, double sideOffset, double topOffset,
        double? manualX = null, double? manualY = null)
    {
        var width = Math.Clamp(contentWidth, 1, Math.Max(1, screenWidth));
        var height = Math.Clamp(contentHeight, 1, Math.Max(1, screenHeight));
        var overhang = (canvasWidth - width) / 2;
        var x = manualX is { } mx ? mx + overhang + sideOffset :
            left + (screenWidth - width) * horizontalAnchor / 2 + (horizontalAnchor == 2 ? -sideOffset : sideOffset);
        var y = manualY is { } my ? my + insetTop + topOffset :
            top + (screenHeight - height) * verticalAnchor / 2 + (verticalAnchor == 2 ? -topOffset : topOffset);
        return (Math.Clamp(x, left, left + Math.Max(0, screenWidth - width)) - overhang,
            Math.Clamp(y, top, top + Math.Max(0, screenHeight - height)) - insetTop);
    }
}
