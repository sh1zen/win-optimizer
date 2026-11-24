using System.Globalization;
using System.Text.Json;

namespace WinOptimizer.Infrastructure.Features.Extensions;

public sealed record DesktopWeatherHour(DateTime LocalTime, double? Celsius, int? Code, bool? IsDay);
public sealed record DesktopWeatherDay(DateOnly Date, double? HighCelsius, double? LowCelsius, int? Code);
public sealed record DesktopWeatherReading(string Place, double Celsius, int Code, bool IsDay)
{
    public IReadOnlyList<DesktopWeatherHour> Hours { get; init; } = [];
    public IReadOnlyList<DesktopWeatherDay> Days { get; init; } = [];
}

/// <summary>Reads and caches current conditions and forecasts for the selected location.</summary>
public sealed class DesktopWeather(HttpClient http)
{
    private (DesktopWeatherLocation Location, DesktopWeatherReading Reading, DateTimeOffset RetrievedAt)? _latest;

    public async Task<DesktopWeatherReading> ReadAsync(DesktopWeatherLocation location, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!location.IsValid) throw new ArgumentException("The weather location is invalid.", nameof(location));
        if (_latest is { } cached && cached.Location == location &&
            DateTimeOffset.UtcNow - cached.RetrievedAt < TimeSpan.FromMinutes(15))
            return cached.Reading;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            const string fields = "current=temperature_2m,weather_code,is_day&timezone=auto&forecast_hours=5&forecast_days=7" +
                "&hourly=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min,weather_code";
            var forecast = new Uri(string.Create(CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/forecast?latitude={location.Latitude}&longitude={location.Longitude}&{fields}"));
            using var stream = await http.GetStreamAsync(forecast, deadline.Token).ConfigureAwait(false);
            using var data = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token).ConfigureAwait(false);
            var root = data.RootElement;
            var current = root.GetProperty("current");
            var reading = new DesktopWeatherReading(location.Label, current.GetProperty("temperature_2m").GetDouble(),
                current.GetProperty("weather_code").GetInt32(), current.GetProperty("is_day").GetInt32() == 1)
            {
                Hours = ReadHours(root.GetProperty("hourly"), current.GetProperty("time").GetDateTime()),
                Days = ReadDays(root.GetProperty("daily"))
            };
            _latest = (location, reading, DateTimeOffset.UtcNow);
            return reading;
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Open-Meteo did not respond in time.", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new JsonException("The weather provider returned an incomplete response.", ex);
        }
    }

    /// <summary>Uses the selected city's local time to discard the current hourly slot.</summary>
    private static List<DesktopWeatherHour> ReadHours(JsonElement hourly, DateTime now)
    {
        var temperatures = hourly.GetProperty("temperature_2m");
        var codes = hourly.GetProperty("weather_code");
        var daylight = hourly.GetProperty("is_day");
        var times = ForecastTimes(hourly, temperatures, codes, daylight);
        var hours = new List<DesktopWeatherHour>(4);
        for (int index = 0; index < times.GetArrayLength() && hours.Count < 4; index++)
        {
            var time = times[index].GetDateTime();
            if (time <= now) continue;
            bool? isDay = ReadCode(daylight[index]) is int phase ? phase == 1 : null;
            hours.Add(new(time, ReadTemperature(temperatures[index]), ReadCode(codes[index]), isDay));
        }
        return hours;
    }

    /// <summary>Pairs each local forecast date with its high, low and weather code.</summary>
    private static List<DesktopWeatherDay> ReadDays(JsonElement daily)
    {
        var highs = daily.GetProperty("temperature_2m_max");
        var lows = daily.GetProperty("temperature_2m_min");
        var codes = daily.GetProperty("weather_code");
        var dates = ForecastTimes(daily, highs, lows, codes);
        int count = dates.GetArrayLength();
        var days = new List<DesktopWeatherDay>(count);
        for (int index = 0; index < count; index++)
        {
            var date = DateOnly.ParseExact(dates[index].GetString() ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture);
            days.Add(new(date, ReadTemperature(highs[index]), ReadTemperature(lows[index]), ReadCode(codes[index])));
        }
        return days;
    }

    /// <summary>Rejects a provider response whose forecast values cannot be paired with their timestamps.</summary>
    private static JsonElement ForecastTimes(JsonElement section, params JsonElement[] series)
    {
        var times = section.GetProperty("time");
        var count = times.GetArrayLength();
        if (series.Any(values => values.GetArrayLength() != count))
            throw new JsonException("The weather provider returned mismatched forecast times and values.");
        return times;
    }

    private static double? ReadTemperature(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetDouble();
    private static int? ReadCode(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
}
