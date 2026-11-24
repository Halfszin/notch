using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Orbi;

// Clima pelo Open-Meteo (grátis, sem cadastro). Só a cidade escolhida nas configurações é enviada.
public static class Weather
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<(double Lat, double Lon, string Name)?> FindCity(string query)
    {
        string url = $"https://geocoding-api.open-meteo.com/v1/search?count=1&language=pt&name={Uri.EscapeDataString(query.Trim())}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var r = results[0];
        string name = string.Join(", ", new[] { "name", "admin1", "country" }
            .Select(k => r.TryGetProperty(k, out var v) ? v.GetString() : null).Where(v => !string.IsNullOrEmpty(v)).Distinct());
        return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), name);
    }

    public static async Task<(double Temp, int Code)> Now(double lat, double lon)
    {
        string url = string.Create(CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code");
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var cur = doc.RootElement.GetProperty("current");
        return (cur.GetProperty("temperature_2m").GetDouble(), cur.GetProperty("weather_code").GetInt32());
    }

    // Códigos WMO do Open-Meteo -> símbolo (fonte Segoe UI Symbol) e descrição.
    public static (string Symbol, string Text) Describe(int code) => code switch
    {
        0 => ("☀", "Céu limpo"),
        1 or 2 => ("⛅", "Parcialmente nublado"),
        3 => ("☁", "Nublado"),
        45 or 48 => ("🌫", "Neblina"),
        >= 51 and <= 67 or >= 80 and <= 82 => ("🌧", "Chuva"),
        >= 71 and <= 77 or 85 or 86 => ("❄", "Neve"),
        >= 95 => ("⛈", "Tempestade"),
        _ => ("☁", "Tempo"),
    };
}
