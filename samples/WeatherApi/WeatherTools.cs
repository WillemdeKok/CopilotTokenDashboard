using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WeatherApi;

/// <summary>MCP tools exposed to GitHub Copilot at <c>/mcp</c>.</summary>
[McpServerToolType]
public sealed class WeatherTools(WeatherService weather)
{
    // Relaxed escaping keeps "°C" readable in tool results (and in the inspector) instead of "\u00B0C".
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Json);

    [McpServerTool(Name = "get_weather_for_date", ReadOnly = true, Idempotent = true)]
    [Description("Gets the weather for a single date in a city: condition, min/max temperature (°C), humidity, " +
                 "chance of rain, wind, UV index, sunrise and sunset. Optionally includes a 24-hour breakdown.")]
    public string GetWeatherForDate(
        [Description("The date as yyyy-MM-dd, or 'today', 'tomorrow' or 'yesterday'.")] string date,
        [Description("City name. Defaults to Amsterdam.")] string? city = null,
        [Description("Include an hourly (24 entries) breakdown. Only request this when hourly detail is needed.")] bool includeHourly = false)
    {
        if (!weather.TryParseDate(date, out var parsed))
        {
            throw new McpException($"'{date}' is not a valid date. Use yyyy-MM-dd, 'today', 'tomorrow' or 'yesterday'.");
        }

        return ToJson(weather.GetWeather(parsed, city, includeHourly));
    }

    [McpServerTool(Name = "get_weather_forecast", ReadOnly = true, Idempotent = true)]
    [Description("Gets a daily weather forecast for a range of consecutive days in a city (max 14 days).")]
    public string GetWeatherForecast(
        [Description("First date of the forecast as yyyy-MM-dd, or 'today' / 'tomorrow'.")] string startDate,
        [Description("Number of days to return (1-14). Defaults to 7.")] int days = 7,
        [Description("City name. Defaults to Amsterdam.")] string? city = null)
    {
        if (!weather.TryParseDate(startDate, out var parsed))
        {
            throw new McpException($"'{startDate}' is not a valid date. Use yyyy-MM-dd, 'today' or 'tomorrow'.");
        }

        if (days is < 1 or > WeatherService.MaxForecastDays)
        {
            throw new McpException($"days must be between 1 and {WeatherService.MaxForecastDays}.");
        }

        return ToJson(weather.GetForecast(parsed, days, city));
    }

    [McpServerTool(Name = "get_current_date", ReadOnly = true)]
    [Description("Returns today's date (yyyy-MM-dd) on the weather server, useful to resolve relative dates like 'next Friday'.")]
    public string GetCurrentDate() => weather.Today.ToString("yyyy-MM-dd (dddd)", System.Globalization.CultureInfo.InvariantCulture);
}
