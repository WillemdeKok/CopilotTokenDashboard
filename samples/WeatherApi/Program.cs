using System.Text.Json.Serialization;
using WeatherApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<WeatherService>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

// MCP server (Streamable HTTP) so GitHub Copilot can call the weather tools.
// Stateless: restarting this API never invalidates a session held by VS Code or the Copilot CLI.
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "warm-weather", Version = "1.0.0" })
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<WeatherTools>();

var app = builder.Build();

app.MapGet("/", () => Results.Text(
    """
    Warm Weather API - it's always nice and warm here.

    REST
      GET /api/weather/{date}?city=Amsterdam&hourly=false   date = yyyy-MM-dd | today | tomorrow | yesterday
      GET /api/weather/forecast?start=today&days=7&city=Amsterdam

    MCP (for GitHub Copilot)
      POST /mcp   tools: get_weather_for_date (one date per call), get_current_date
    """));

var api = app.MapGroup("/api/weather");

api.MapGet("/forecast", (WeatherService weather, string? start, int? days, string? city) =>
{
    if (!weather.TryParseDate(start, out var startDate))
    {
        return Results.Problem($"'{start}' is not a valid date.", statusCode: StatusCodes.Status400BadRequest);
    }

    var count = days ?? 7;
    return count is < 1 or > WeatherService.MaxForecastDays
        ? Results.Problem($"days must be between 1 and {WeatherService.MaxForecastDays}.", statusCode: StatusCodes.Status400BadRequest)
        : Results.Ok(weather.GetForecast(startDate, count, city));
});

api.MapGet("/{date}", (WeatherService weather, string date, string? city, bool? hourly) =>
    weather.TryParseDate(date, out var parsed)
        ? Results.Ok(weather.GetWeather(parsed, city, hourly ?? false))
        : Results.Problem($"'{date}' is not a valid date. Use yyyy-MM-dd, today, tomorrow or yesterday.",
            statusCode: StatusCodes.Status400BadRequest));

app.MapMcp("/mcp");

app.Run();

public partial class Program;
