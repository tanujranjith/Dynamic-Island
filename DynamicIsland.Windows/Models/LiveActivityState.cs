namespace DynamicIsland.Windows.Models;

public sealed record WeatherInfo(string TempText, string Glyph, string Description, string City);

public sealed record SystemStats(int CpuPercent, int RamPercent, string NetworkText, double NetBytesPerSec = 0)
{
    public static readonly SystemStats Empty = new(0, 0, "—");
}
