using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WeatherApi;

public sealed record HourlyWeather(string Time, double TemperatureC, string Condition, int PrecipitationChancePercent);

public sealed record WeatherReport(
    string Date,
    string DayOfWeek,
    string City,
    string Condition,
    string Summary,
    double TemperatureMinC,
    double TemperatureMaxC,
    double FeelsLikeMaxC,
    int HumidityPercent,
    int PrecipitationChancePercent,
    int WindSpeedKmh,
    string WindDirection,
    int UvIndex,
    double SeaTemperatureC,
    string Sunrise,
    string Sunset,
    IReadOnlyList<HourlyWeather>? Hourly);

/// <summary>
/// Generates deterministic, always pleasantly warm weather. The same date + city always returns the same report,
/// which makes it easy to compare tool calls across Copilot sessions in the inspector.
/// </summary>
public sealed class WeatherService
{
    public const string DefaultCity = "Amsterdam";
    public const int MaxForecastDays = 14;

    private static readonly string[] Conditions =
        ["Sunny", "Clear skies", "Mostly sunny", "Partly cloudy", "Hazy sunshine", "Sunny with a light breeze"];

    private static readonly string[] WindDirections = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    private static readonly string[] Taglines =
    [
        "Perfect terrace weather.",
        "Ideal for a day at the beach.",
        "Great evening for a barbecue.",
        "Bring sunscreen and sunglasses.",
        "A lovely day for a bike ride.",
        "Warm and pleasant from morning to late evening.",
    ];

    private readonly TimeProvider _time;

    public WeatherService(TimeProvider time) => _time = time;

    public DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    /// <summary>Parses "yyyy-MM-dd", "today", "tomorrow" or "yesterday".</summary>
    public bool TryParseDate(string? value, out DateOnly date)
    {
        var text = value?.Trim().ToLowerInvariant();
        switch (text)
        {
            case null or "" or "today":
                date = Today;
                return true;
            case "tomorrow":
                date = Today.AddDays(1);
                return true;
            case "yesterday":
                date = Today.AddDays(-1);
                return true;
        }

        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
               || DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    public WeatherReport GetWeather(DateOnly date, string? city = null, bool includeHourly = false)
    {
        city = NormalizeCity(city);
        var rng = new Random(StableSeed(date, city));

        // Slight seasonal swing (warmest around late July), but it never drops below "nice and warm".
        var season = Math.Cos((date.DayOfYear - 200) / 365.0 * 2 * Math.PI); // 1 in summer, -1 in winter
        var max = Math.Round(29 + 3 * season + rng.NextDouble() * 3 - 1.5, 1);
        var min = Math.Round(max - 6 - rng.NextDouble() * 2.5, 1);
        var humidity = rng.Next(38, 62);
        var condition = Conditions[rng.Next(Conditions.Length)];
        var rain = condition == "Partly cloudy" ? rng.Next(5, 16) : rng.Next(0, 8);
        var wind = rng.Next(5, 19);
        var sunriseMinutes = 6 * 60 - (int)(40 * season) + rng.Next(-3, 4);
        var sunsetMinutes = 20 * 60 + (int)(60 * season) + rng.Next(-3, 4);

        IReadOnlyList<HourlyWeather>? hourly = null;
        if (includeHourly)
        {
            hourly = Enumerable.Range(0, 24).Select(hour =>
            {
                // Coolest just before sunrise, warmest mid-afternoon.
                var curve = (Math.Cos((hour - 15) / 24.0 * 2 * Math.PI) + 1) / 2;
                var temp = Math.Round(min + (max - min) * curve + (rng.NextDouble() - 0.5) * 0.6, 1);
                var hourCondition = hour * 60 < sunriseMinutes || hour * 60 > sunsetMinutes ? "Clear night" : condition;
                return new HourlyWeather($"{hour:00}:00", temp, hourCondition, Math.Max(0, rain + rng.Next(-3, 3)));
            }).ToList();
        }

        return new WeatherReport(
            Date: date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DayOfWeek: date.DayOfWeek.ToString(),
            City: city,
            Condition: condition,
            Summary: $"{condition} and warm in {city}, up to {max:0.#}°C. {Taglines[rng.Next(Taglines.Length)]}",
            TemperatureMinC: min,
            TemperatureMaxC: max,
            FeelsLikeMaxC: Math.Round(max + humidity / 25.0, 1),
            HumidityPercent: humidity,
            PrecipitationChancePercent: rain,
            WindSpeedKmh: wind,
            WindDirection: WindDirections[rng.Next(WindDirections.Length)],
            UvIndex: Math.Clamp(6 + (int)Math.Round(2 * season) + rng.Next(0, 3), 5, 11),
            SeaTemperatureC: Math.Round(21 + 3 * season + rng.NextDouble(), 1),
            Sunrise: ToClock(sunriseMinutes),
            Sunset: ToClock(sunsetMinutes),
            Hourly: hourly);
    }

    public IReadOnlyList<WeatherReport> GetForecast(DateOnly start, int days, string? city = null) =>
        Enumerable.Range(0, Math.Clamp(days, 1, MaxForecastDays))
            .Select(offset => GetWeather(start.AddDays(offset), city))
            .ToList();

    private static string NormalizeCity(string? city)
    {
        if (string.IsNullOrWhiteSpace(city))
        {
            return DefaultCity;
        }

        var trimmed = city.Trim();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(trimmed.ToLowerInvariant());
    }

    private static int StableSeed(DateOnly date, string city)
    {
        // string.GetHashCode is randomized per process; use a stable hash so results are reproducible.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{date:yyyy-MM-dd}|{city.ToUpperInvariant()}"));
        return BitConverter.ToInt32(bytes, 0);
    }

    private static string ToClock(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";
}
