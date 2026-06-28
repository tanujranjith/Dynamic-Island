using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using DynamicIsland.Windows.Models;

namespace DynamicIsland.Windows.Services;

/// <summary>
/// Polls quotes for a few symbols (stocks or crypto like BTC-USD) from Yahoo Finance's public chart
/// endpoint — no API key. Best-effort: on any failure the symbol is skipped. Raises on the UI thread.
/// </summary>
public sealed class StocksService(LoggingService log) : IDisposable
{
    private readonly HttpClient _http = CreateClient();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(3) };
    private string[] _symbols = [];

    public event EventHandler<IReadOnlyList<StockQuote>>? Changed;
    public IReadOnlyList<StockQuote> Current { get; private set; } = [];

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DynamicIsland");
        return c;
    }

    public void Start()
    {
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Configure(string symbolsCsv)
    {
        var symbols = (symbolsCsv ?? "")
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant()).Distinct().Take(6).ToArray();
        if (symbols.SequenceEqual(_symbols)) return;
        _symbols = symbols;
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_symbols.Length == 0) { Publish([]); return; }
        var quotes = new List<StockQuote>();
        foreach (var symbol in _symbols)
        {
            try
            {
                var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?interval=1d&range=1d";
                using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
                var meta = doc.RootElement.GetProperty("chart").GetProperty("result")[0].GetProperty("meta");
                var price = meta.GetProperty("regularMarketPrice").GetDouble();
                var prev = meta.TryGetProperty("chartPreviousClose", out var cpc) ? cpc.GetDouble()
                    : meta.TryGetProperty("previousClose", out var pc) ? pc.GetDouble() : price;
                var change = prev > 0 ? (price - prev) / prev * 100 : 0;
                quotes.Add(new StockQuote(symbol, price, change));
            }
            catch (Exception ex) { log.Debug($"Stock {symbol} fetch failed: {ex.Message}"); }
        }
        Publish(quotes);
    }

    private void Publish(IReadOnlyList<StockQuote> quotes)
    {
        if (quotes.SequenceEqual(Current)) return;
        Current = quotes;
        Changed?.Invoke(this, quotes);
    }

    public void Dispose()
    {
        _timer.Stop();
        _http.Dispose();
    }
}
