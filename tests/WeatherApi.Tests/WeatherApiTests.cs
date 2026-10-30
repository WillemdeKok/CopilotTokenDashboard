using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace WeatherApi.Tests;

public class WeatherServiceTests
{
    private static readonly WeatherService Service = new(TimeProvider.System);

    [Fact]
    public void Same_date_and_city_always_returns_the_same_weather()
    {
        var date = new DateOnly(2026, 7, 15);

        Assert.Equal(
            JsonSerializer.Serialize(Service.GetWeather(date, "Amsterdam", includeHourly: true)),
            JsonSerializer.Serialize(Service.GetWeather(date, " amsterdam ", includeHourly: true)));
    }

    [Fact]
    public void Weather_is_nice_and_warm_all_year_round()
    {
        foreach (var city in new[] { "Amsterdam", "Oslo", "Reykjavik", "Barcelona" })
        {
            for (var date = new DateOnly(2026, 1, 1); date.Year == 2026; date = date.AddDays(1))
            {
                var report = Service.GetWeather(date, city, includeHourly: true);

                Assert.InRange(report.TemperatureMaxC, 24, 34);
                Assert.InRange(report.TemperatureMinC, 15, report.TemperatureMaxC);
                Assert.InRange(report.PrecipitationChancePercent, 0, 15);
                Assert.Equal(24, report.Hourly!.Count);
                Assert.All(report.Hourly, h => Assert.InRange(h.TemperatureC, report.TemperatureMinC - 1, report.TemperatureMaxC + 1));
            }
        }
    }

    [Fact]
    public void Relative_dates_are_resolved_against_the_clock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        var service = new WeatherService(clock);

        Assert.True(service.TryParseDate("tomorrow", out var tomorrow));
        Assert.Equal(new DateOnly(2026, 6, 2), tomorrow);
        Assert.True(service.TryParseDate(" Today ", out var today));
        Assert.Equal(new DateOnly(2026, 6, 1), today);
        Assert.True(service.TryParseDate("2026-12-24", out var christmasEve));
        Assert.Equal(new DateOnly(2026, 12, 24), christmasEve);
        Assert.False(service.TryParseDate("someday", out _));
    }
}

public class WeatherEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Rest_endpoint_returns_weather_for_a_date()
    {
        var client = factory.CreateClient();

        var report = await client.GetFromJsonAsync<WeatherReport>("/api/weather/2026-08-01?city=Barcelona");

        Assert.NotNull(report);
        Assert.Equal("2026-08-01", report.Date);
        Assert.Equal("Barcelona", report.City);
        Assert.Null(report.Hourly);
    }

    [Fact]
    public async Task Rest_endpoint_rejects_invalid_input()
    {
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/weather/not-a-date")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/weather/forecast?days=99")).StatusCode);
    }

    [Fact]
    public async Task Mcp_endpoint_exposes_and_executes_the_weather_tools()
    {
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(factory.Server.BaseAddress, "/mcp") },
            factory.CreateClient()));

        var tools = await mcp.ListToolsAsync();
        Assert.Equal(
            ["get_current_date", "get_weather_for_date"],
            tools.Select(t => t.Name).Order());

        var result = await mcp.CallToolAsync("get_weather_for_date",
            new Dictionary<string, object?> { ["date"] = "2026-08-01", ["city"] = "Barcelona" });
        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("°C", text);
        var report = JsonSerializer.Deserialize<WeatherReport>(text, JsonSerializerOptions.Web)!;
        Assert.Equal("Barcelona", report.City);

        var invalid = await mcp.CallToolAsync("get_weather_for_date",
            new Dictionary<string, object?> { ["date"] = "someday" });
        Assert.True(invalid.IsError);
    }
}
