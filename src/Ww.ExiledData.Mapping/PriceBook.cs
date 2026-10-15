using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ww.ExiledData.Mapping;

/// <summary>How a value is shown: always in one unit, or <see cref="Adaptive"/> (the biggest unit it is at least 1 of).</summary>
public enum PriceUnit
{
    /// <summary>Divine Orbs, stepping down to Chaos, then Exalted Orbs while the value is under 1: <c>23.1 DIV</c>, <c>4.20 ChO</c>.</summary>
    Adaptive,
    Divine,
    Exalted,
    Chaos,
}

/// <summary>A value in the three units the app shows: Divine, Exalted and Chaos Orbs.</summary>
public readonly record struct CurrencyValue(double Divine, double Exalted, double Chaos)
{
    public static CurrencyValue Zero { get; } = new(0, 0, 0);

    public static CurrencyValue operator *(CurrencyValue v, double factor) => new(v.Divine * factor, v.Exalted * factor, v.Chaos * factor);
    public static CurrencyValue operator +(CurrencyValue a, CurrencyValue b) => new(a.Divine + b.Divine, a.Exalted + b.Exalted, a.Chaos + b.Chaos);

    /// <summary>The amount and fixed unit to show it in; <see cref="PriceUnit.Adaptive"/> picks the biggest unit it is at least 1 of.</summary>
    public (double Amount, PriceUnit Unit) In(PriceUnit unit) => unit switch
    {
        PriceUnit.Divine => (Divine, PriceUnit.Divine),
        PriceUnit.Exalted => (Exalted, PriceUnit.Exalted),
        PriceUnit.Chaos => (Chaos, PriceUnit.Chaos),
        _ when Divine >= 1 => (Divine, PriceUnit.Divine),
        _ when Chaos >= 1 => (Chaos, PriceUnit.Chaos),
        _ => (Exalted, PriceUnit.Exalted),
    };
}

/// <summary>
/// Unit prices of one league from poe.ninja, in Divine Orbs, with the Exalted and Chaos rates of that pull. Saved as
/// JSON next to the database (<see cref="Save"/> / <see cref="Load"/>), so a restart shows the last pull at once.
/// </summary>
public sealed record PriceBook
{
    public static PriceBook Empty { get; } = new();

    [JsonPropertyName("league")] public string League { get; init; } = "";
    [JsonPropertyName("fetchedAt")] public DateTimeOffset? FetchedAt { get; init; }
    [JsonPropertyName("exaltedPerDivine")] public double ExaltedPerDivine { get; init; }
    [JsonPropertyName("chaosPerDivine")] public double ChaosPerDivine { get; init; }
    /// <summary>In-game name → price of one in Divine Orbs.</summary>
    [JsonPropertyName("divine")] public IReadOnlyDictionary<string, double> Divine { get; init; } = new Dictionary<string, double>();

    [JsonIgnore] public bool HasPrices => Divine.Count > 0 && FetchedAt is not null;

    /// <summary>The value of one <paramref name="name"/>, or null when poe.ninja has no price for it.</summary>
    public CurrencyValue? UnitValue(string name) =>
        Divine.TryGetValue(name, out var divine) ? new CurrencyValue(divine, divine * ExaltedPerDivine, divine * ChaosPerDivine) : null;

    public TimeSpan? Age(DateTimeOffset now) => FetchedAt is { } at ? now - at : null;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static PriceBook Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<PriceBook>(File.ReadAllText(path), Options) ?? Empty : Empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, path, overwrite: true);
    }
}
