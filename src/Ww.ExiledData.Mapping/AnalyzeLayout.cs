using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ww.ExiledData.Mapping;

/// <summary>A chart added by hand in the Analyze tab: the value of a set of currencies, by name.</summary>
public sealed record CustomChart
{
    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("currencies")] public IReadOnlyList<string> Currencies { get; init; } = [];
}

/// <summary>
/// What the Analyze tab keeps per session: the currencies whose Use box is unticked and the charts added by hand.
/// Stored as JSON (<see cref="ToJson"/> / <see cref="FromJson"/>); a view preference, not undoable.
/// </summary>
public sealed record AnalyzeLayout
{
    public static AnalyzeLayout Empty { get; } = new();

    /// <summary>Currency names left out of the values (everything else is used, also currencies that drop later).</summary>
    [JsonPropertyName("unused")] public IReadOnlyList<string> UnusedCurrencies { get; init; } = [];
    [JsonPropertyName("charts")] public IReadOnlyList<CustomChart> Charts { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>The layout in <paramref name="json"/>; <see cref="Empty"/> when it is not one.</summary>
    public static AnalyzeLayout FromJson(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? Empty : JsonSerializer.Deserialize<AnalyzeLayout>(json) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }
}
