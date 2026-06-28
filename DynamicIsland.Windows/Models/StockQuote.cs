namespace DynamicIsland.Windows.Models;

public sealed record StockQuote(string Symbol, double Price, double ChangePercent)
{
    public bool Up => ChangePercent >= 0;
    public string PriceText => Price >= 1000 ? Price.ToString("N0") : Price.ToString("0.##");
    public string ChangeText => (Up ? "+" : "") + ChangePercent.ToString("0.0") + "%";
}
