using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using DynamicIsland.Windows.Models;

namespace DynamicIsland.Windows.Services;

/// <summary>
/// Fetches current conditions from Open-Meteo (no API key required). Geocodes a free-text location once,
/// then polls the forecast every half hour. Raises Changed on the UI thread.
/// </summary>
public sealed class WeatherService(LoggingService log) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(30) };
    private string _location = "";
    private bool _fahrenheit;
    private (double Lat, double Lon, string City)? _geo;
    private string _geoKey = "";

    // Glyphs built from code points so no literal icon characters live in the source.
    private static readonly string Sun = char.ConvertFromUtf32(0xE706);   // Brightness
    private static readonly string Cloud = char.ConvertFromUtf32(0xE753); // Cloud
    private static readonly string Degree = "°";

    public event EventHandler<WeatherInfo?>? Changed;
    public WeatherInfo? Current { get; private set; }

    public void Start()
    {
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Configure(string location, bool fahrenheit)
    {
        var changed = !string.Equals(location, _location, StringComparison.OrdinalIgnoreCase) || fahrenheit != _fahrenheit;
        _location = location ?? "";
        _fahrenheit = fahrenheit;
        if (changed) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_location)) { Publish(null); return; }
            if (_geo is null || !string.Equals(_geoKey, _location, StringComparison.OrdinalIgnoreCase))
            {
                _geo = await GeocodeAsync(_location);
                _geoKey = _location;
            }
            if (_geo is not { } g) { Publish(null); return; }

            var unit = _fahrenheit ? "fahrenheit" : "celsius";
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={g.Lat:0.####}&longitude={g.Lon:0.####}"
                      + $"&current=temperature_2m,weather_code&temperature_unit={unit}";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            var current = doc.RootElement.GetProperty("current");
            var temp = current.GetProperty("temperature_2m").GetDouble();
            var code = current.GetProperty("weather_code").GetInt32();
            var (glyph, desc) = Describe(code);
            Publish(new WeatherInfo($"{Math.Round(temp)}{Degree}", glyph, desc, g.City));
        }
        catch (Exception ex) { log.Error("Weather refresh failed", ex); }
    }

    private async Task<(double, double, string)?> GeocodeAsync(string name)
    {
        try
        {
            var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name)}&count=1";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
            var first = results[0];
            return (first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble(),
                first.GetProperty("name").GetString() ?? name);
        }
        catch (Exception ex) { log.Error("Geocoding failed", ex); return null; }
    }

    private static (string Glyph, string Desc) Describe(int code) => code switch
    {
        0 => (Sun, "Clear"),
        1 or 2 => (Sun, "Partly cloudy"),
        3 => (Cloud, "Cloudy"),
        45 or 48 => (Cloud, "Fog"),
        51 or 53 or 55 or 56 or 57 => (Cloud, "Drizzle"),
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => (Cloud, "Rain"),
        71 or 73 or 75 or 77 or 85 or 86 => (Cloud, "Snow"),
        95 or 96 or 99 => (Cloud, "Storm"),
        _ => (Cloud, "Weather")
    };

    private void Publish(WeatherInfo? info)
    {
        if (info == Current) return;
        Current = info;
        Changed?.Invoke(this, info);
    }

    public void Dispose()
    {
        _timer.Stop();
        _http.Dispose();
    }
}
