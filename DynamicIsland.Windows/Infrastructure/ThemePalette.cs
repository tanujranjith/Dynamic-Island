namespace DynamicIsland.Windows.Infrastructure;

public readonly record struct ThemeColor(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
    public static bool TryParse(string? hex, out ThemeColor color)
    {
        color = default;
        var text = hex?.Trim().TrimStart('#');
        if (text?.Length == 3) text = string.Concat(text.Select(c => new string(c, 2)));
        if (text?.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return false;
        color = new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); return true;
    }
    public static ThemeColor Parse(string hex) => TryParse(hex, out var color) ? color : new(36, 28, 60);
    public ThemeColor Blend(ThemeColor other, double amount) => new(
        (byte)Math.Round(R + (other.R - R) * amount), (byte)Math.Round(G + (other.G - G) * amount), (byte)Math.Round(B + (other.B - B) * amount));
    public double Luminance
    {
        get
        {
            static double Linear(byte component) { var s = component / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
            return .2126 * Linear(R) + .7152 * Linear(G) + .0722 * Linear(B);
        }
    }
    public double Contrast(ThemeColor other) => (Math.Max(Luminance, other.Luminance) + .05) / (Math.Min(Luminance, other.Luminance) + .05);
}

public sealed record ThemePalette(string Surface, string Card, string Control, string Hover, string Border, string Text, string Muted, string Accent, string Selected)
{
    public const string DefaultCustomColor = "#241C3C";
    public bool IsDark => Text == "#FFFFFF" || ThemeColor.Parse(Text).Luminance > .5;
    public static ThemePalette Custom(string hex)
    {
        var surface = ThemeColor.Parse(hex);
        var white = ThemeColor.Parse("#FFFFFF"); var black = ThemeColor.Parse("#000000");
        var text = surface.Contrast(white) >= surface.Contrast(black) ? white : black;
        // Surfaces move away from the foreground, preserving its contrast even for middle-tone colors.
        var backing = text == white ? black : white;
        var card = surface.Blend(backing, .12);
        var control = surface.Blend(backing, .22);
        var accent = ThemeColor.Parse(text == white ? "#8BC4FF" : "#003E85");
        if (accent.Contrast(surface) < 4.5 || accent.Contrast(card) < 4.5) accent = text;
        var muted = text.Blend(surface, .18);
        if (muted.Contrast(surface) < 4.5) muted = text;
        return new(surface.Hex, card.Hex, control.Hex, surface.Blend(backing, .32).Hex,
            surface.Blend(text, .35).Hex, text.Hex, muted.Hex, accent.Hex, control.Hex);
    }
    public static ThemePalette Dark { get; } = new("#080C12", "#0F1722", "#121923", "#172131", "#344156", "#E8EDF5", "#AAB5C6", "#63ACFF", "#173B65");
    public static ThemePalette Light { get; } = new("#F5F5F7", "#FFFFFF", "#E8EDF4", "#DCE5F0", "#ABB9CB", "#172033", "#526078", "#075AAF", "#D5E6FA");
}
