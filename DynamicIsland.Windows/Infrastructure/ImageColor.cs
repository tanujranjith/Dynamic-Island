namespace DynamicIsland.Windows.Infrastructure;

/// <summary>Extracts a vivid accent colour from album art for adaptive theming.</summary>
public static class ImageColor
{
    public static string? Dominant(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 16) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            using var source = new System.Drawing.Bitmap(stream);
            using var small = new System.Drawing.Bitmap(source, new System.Drawing.Size(16, 16));

            double r = 0, g = 0, b = 0, weight = 0;
            for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
            {
                var c = small.GetPixel(x, y);
                int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
                // Weight by saturation so muted/grey pixels don't wash out the accent.
                double w = (max - min) / 255.0;
                w *= w;
                r += c.R * w; g += c.G * w; b += c.B * w; weight += w;
            }
            if (weight < 0.05) return null; // near-greyscale art — keep the user's accent

            byte R = (byte)(r / weight), G = (byte)(g / weight), B = (byte)(b / weight);
            // Lift toward a usable, bright accent.
            var peak = Math.Max(R, Math.Max(G, B));
            if (peak < 170 && peak > 0)
            {
                var scale = 170.0 / peak;
                R = (byte)Math.Min(255, R * scale);
                G = (byte)Math.Min(255, G * scale);
                B = (byte)Math.Min(255, B * scale);
            }
            return $"#{R:X2}{G:X2}{B:X2}";
        }
        catch { return null; }
    }
}
