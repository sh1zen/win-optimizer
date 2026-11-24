using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>A selected weather location with a display label and geographic coordinates.</summary>
public sealed record DesktopWeatherLocation(string Label, double? Latitude, double? Longitude)
{
    [JsonIgnore] public bool IsValid => Label is { Length: >= 2 and <= 500 } && !Label.Any(char.IsControl) &&
        Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}

/// <summary>Searches the same Photon GeoJSON endpoint as Astro.</summary>
public sealed class DesktopPlaceSearch(HttpClient http)
{
    public const int MaximumQueryLength = 200;
    private const int MaximumResults = 8;
    private const int MaximumResponseBytes = 512 * 1024;

    public async Task<IReadOnlyList<DesktopWeatherLocation>> SearchAsync(string query, CancellationToken token)
    {
        query = query.Trim();
        if (query.Length is < 2 or > MaximumQueryLength || query.Any(char.IsControl))
            throw new ArgumentException("Search for a location with 2–200 characters.", nameof(query));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var address = new Uri($"https://photon.komoot.io/api/?q={Uri.EscapeDataString(query)}&limit={MaximumResults}");
            using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, deadline.Token).ConfigureAwait(false);
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var data = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token).ConfigureAwait(false);
            if (!data.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                throw new JsonException("Photon returned an invalid feature collection.");

            var places = new List<DesktopWeatherLocation>(MaximumResults);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var feature in features.EnumerateArray().Take(100))
            {
                if (ReadPlace(feature) is { } place && seen.Add(place.Label)) places.Add(place);
                if (places.Count == MaximumResults) break;
            }
            return places;
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Photon did not respond in time.", ex);
        }
    }

    private static DesktopWeatherLocation? ReadPlace(JsonElement feature)
    {
        if (feature.ValueKind != JsonValueKind.Object ||
            !feature.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return null;
        var name = FirstLabel(properties, "name", "city", "town", "village", "county");
        if (name is null) return null;
        if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object ||
            FirstLabel(geometry, "type") != "Point" ||
            !geometry.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array ||
            coordinates.GetArrayLength() < 2 ||
            coordinates[0].ValueKind != JsonValueKind.Number || coordinates[1].ValueKind != JsonValueKind.Number ||
            !coordinates[0].TryGetDouble(out var longitude) || !coordinates[1].TryGetDouble(out var latitude))
            return null;
        var label = string.Join(", ", new[] { name, FirstLabel(properties, "state", "county"),
            FirstLabel(properties, "country") }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase));
        var place = new DesktopWeatherLocation(label, latitude, longitude);
        return place.IsValid ? place : null;
    }

    private static string? FirstLabel(JsonElement properties, params string[] keys)
    {
        foreach (var key in keys)
            if (properties.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String &&
                field.GetString()?.Trim() is { Length: > 0 } text)
                return text;
        return null;
    }
}
