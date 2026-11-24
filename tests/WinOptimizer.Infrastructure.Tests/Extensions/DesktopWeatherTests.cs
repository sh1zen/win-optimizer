using System.Net;
using System.Text.Json;
using WinOptimizer.Infrastructure.Features.Extensions;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Extensions;

public sealed class DesktopWeatherTests
{
    private const string Forecast = """
        {
          "current":{"time":"2026-11-23T22:45","temperature_2m":9,"weather_code":51,"is_day":0},
          "hourly":{
            "time":["2026-11-23T22:00","2026-11-23T23:00","2026-11-24T00:00","2026-11-24T01:00","2026-11-24T02:00"],
            "temperature_2m":[9,8.6,8,null,6],"weather_code":[51,61,3,null,0],"is_day":[0,0,0,null,0]
          },
          "daily":{
            "time":["2026-11-23","2026-11-24","2026-11-25","2026-11-26","2026-11-27","2026-11-28","2026-11-29"],
            "temperature_2m_max":[14,12,7,6,8,9,10],"temperature_2m_min":[8,3,-1,-2,1,4,null],"weather_code":[51,3,71,71,1,2,0]
          }
        }
        """;

    [Fact]
    public async Task ReadsConfiguredCityAndReusesConditionsAndForecast()
    {
        using var handler = new RecordingHandler(Forecast);
        using var client = new HttpClient(handler);
        var source = new DesktopWeather(client);

        var place = new DesktopWeatherLocation("Roma, Italia", 41.9, 12.5);
        var reading = await source.ReadAsync(place, CancellationToken.None);
        Assert.Equal(("Roma, Italia", 9d, 51, false), (reading.Place, reading.Celsius, reading.Code, reading.IsDay));
        Assert.Collection(reading.Hours,
            hour => Assert.Equal(new DesktopWeatherHour(new(2026, 11, 23, 23, 0, 0), 8.6, 61, false), hour),
            hour => Assert.Equal(new DesktopWeatherHour(new(2026, 11, 24, 0, 0, 0), 8, 3, false), hour),
            hour => Assert.Equal(new DesktopWeatherHour(new(2026, 11, 24, 1, 0, 0), null, null, null), hour),
            hour => Assert.Equal(new DesktopWeatherHour(new(2026, 11, 24, 2, 0, 0), 6, 0, false), hour));
        Assert.Equal(Enumerable.Range(23, 7).Select(day => new DateOnly(2026, 11, day)), reading.Days.Select(day => day.Date));
        Assert.Equal(new DesktopWeatherDay(new(2026, 11, 25), 7, -1, 71), reading.Days[2]);
        Assert.Null(reading.Days[^1].LowCelsius);
        Assert.Same(reading, await source.ReadAsync(place, CancellationToken.None));
        var uri = Assert.Single(handler.Requests);
        Assert.Equal("api.open-meteo.com", uri.Host);
        Assert.Contains("latitude=41.9&longitude=12.5", uri.Query);
        Assert.Contains("timezone=auto", uri.Query);
        Assert.Contains("forecast_hours=5&forecast_days=7", uri.Query);
        Assert.Contains("hourly=temperature_2m,weather_code,is_day", uri.Query);
        Assert.Contains("daily=temperature_2m_max,temperature_2m_min,weather_code", uri.Query);
    }

    [Fact]
    public async Task RejectsForecastValuesWithoutMatchingTimes()
    {
        using var handler = new RecordingHandler(Forecast.Replace("[51,61,3,null,0]", "[51,61,3]", StringComparison.Ordinal));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<JsonException>(() =>
            new DesktopWeather(client).ReadAsync(new("Roma", 41.9, 12.5), CancellationToken.None));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(41.9, null)]
    [InlineData(91d, 12.5)]
    [InlineData(41.9, -181d)]
    public async Task InvalidCoordinatesDoNotRequestForecast(double? latitude, double? longitude)
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new DesktopWeather(client).ReadAsync(new("Roma", latitude, longitude), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PhotonPreservesHomonymsAndForecastUsesTheChosenCoordinates()
    {
        using var handler = new RecordingHandler(
            """
            {"features":[
                {"properties":{"name":"Springfield","state":"Illinois","country":"United States"},"geometry":{"type":"Point","coordinates":[-89.65,39.78]}},
                {"properties":{"name":"Springfield","state":"Massachusetts","country":"United States"},"geometry":{"type":"Point","coordinates":[-72.59,42.10]}},
                {"properties":{"name":"Springfield","state":"Illinois","country":"United States"},"geometry":{"type":"Point","coordinates":[-89.7,39.8]}},
                {"properties":{"name":"Springfield","state":"Invalid"},"geometry":{"type":"Point","coordinates":[-72.59,95]}}
            ]}
            """,
            Forecast);
        using var client = new HttpClient(handler);
        var places = await new DesktopPlaceSearch(client).SearchAsync(" Springfield ", CancellationToken.None);

        Assert.Collection(places,
            place => Assert.Equal(new DesktopWeatherLocation("Springfield, Illinois, United States", 39.78, -89.65), place),
            place => Assert.Equal(new DesktopWeatherLocation("Springfield, Massachusetts, United States", 42.1, -72.59), place));
        var selected = places[1];
        var source = new DesktopWeather(client);
        var reading = await source.ReadAsync(selected, CancellationToken.None);
        Assert.Equal(selected.Label, reading.Place);
        Assert.Same(reading, await source.ReadAsync(selected, CancellationToken.None));
        Assert.Collection(handler.Requests,
            uri => Assert.Equal("https://photon.komoot.io/api/?q=Springfield&limit=8", uri.AbsoluteUri),
            uri =>
            {
                Assert.Equal("api.open-meteo.com", uri.Host);
                Assert.Contains("latitude=42.1&longitude=-72.59", uri.Query);
            });
    }

    private sealed class RecordingHandler(params string[] payloads) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri ?? throw new InvalidOperationException("The weather URI is missing."));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payloads[Requests.Count - 1]) });
        }
    }
}
