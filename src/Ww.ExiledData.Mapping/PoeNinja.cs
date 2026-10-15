using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ww.ExiledData.Mapping;

/// <summary>
/// Reads PoE2 currency exchange prices from poe.ninja: one overview per category, each with its items (id → name),
/// lines (id → value in Divine Orbs) and the Divine → Exalted / Chaos rates. Gems are skipped: they are not stacked
/// currency.
/// </summary>
public sealed class PoeNinja(HttpClient http)
{
    public const string BaseUrl = "https://poe.ninja/poe2/api/";
    /// <summary>Pause between the requests of one pull, so a pull is not a burst.</summary>
    public TimeSpan RequestGap { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The poe.ninja overview categories that hold catalog items.</summary>
    public static IReadOnlyList<string> Categories { get; } =
        ["Currency", "Fragments", "Abyss", "Breach", "Delirium", "Essences", "Ritual", "SoulCores", "Idols", "Runes", "Expedition", "Verisium"];

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Ww.ExiledData/1.0 (personal loot tracker)");
        return http;
    }

    /// <summary>The current softcore challenge league (the first economy league that is not hardcore or Standard).</summary>
    public async Task<string> CurrentLeague(CancellationToken cancel = default)
    {
        var state = await http.GetFromJsonAsync<IndexState>("data/index-state", cancel)
            ?? throw new InvalidDataException("poe.ninja index state is empty.");
        return state.EconomyLeagues.FirstOrDefault(l => !l.Hardcore && l.Name != "Standard")?.Name
            ?? throw new InvalidDataException("poe.ninja lists no current league.");
    }

    /// <summary>Every category of <paramref name="league"/>; a category that fails is skipped unless all fail.</summary>
    public async Task<PriceBook> Fetch(string league, DateTimeOffset now, CancellationToken cancel = default)
    {
        var prices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["Divine Orb"] = 1 };
        double exalted = 0, chaos = 0;
        Exception? lastError = null;
        var fetched = 0;
        foreach (var category in Categories)
        {
            if (fetched > 0 || lastError is not null)
                await Task.Delay(RequestGap, cancel);
            try
            {
                var url = $"economy/exchange/current/overview?league={Uri.EscapeDataString(league)}&type={category}";
                var overview = await http.GetFromJsonAsync<Overview>(url, cancel);
                if (overview is null)
                    continue;
                if (overview.Core is { Primary: "divine" } core)
                {
                    exalted = core.Rates?.GetValueOrDefault("exalted", exalted) ?? exalted;
                    chaos = core.Rates?.GetValueOrDefault("chaos", chaos) ?? chaos;
                }
                Merge(overview, prices);
                fetched++;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancel.IsCancellationRequested)
            {
                lastError = ex;
            }
        }
        if (fetched == 0 || exalted <= 0 || chaos <= 0)
            throw new InvalidDataException($"poe.ninja gave no prices for {league}.", lastError);
        return new PriceBook { League = league, FetchedAt = now, ExaltedPerDivine = exalted, ChaosPerDivine = chaos, Divine = prices };
    }

    /// <summary>Adds the prices of one overview (by item name); the first category to price a name wins.</summary>
    public static void Merge(Overview overview, Dictionary<string, double> prices)
    {
        var names = (overview.Items ?? []).Where(i => i.Name is { Length: > 0 }).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First().Name!);
        foreach (var line in overview.Lines ?? [])
        {
            if (line.PrimaryValue > 0 && names.TryGetValue(line.Id, out var name))
                prices.TryAdd(name, line.PrimaryValue);
        }
    }

    public sealed record IndexState([property: JsonPropertyName("economyLeagues")] IReadOnlyList<League> EconomyLeagues);

    public sealed record League([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("hardcore")] bool Hardcore);

    public sealed record Overview(
        [property: JsonPropertyName("core")] Core? Core,
        [property: JsonPropertyName("items")] IReadOnlyList<Item> Items,
        [property: JsonPropertyName("lines")] IReadOnlyList<Line> Lines);

    public sealed record Core(
        [property: JsonPropertyName("primary")] string Primary,
        [property: JsonPropertyName("rates")] IReadOnlyDictionary<string, double> Rates);

    public sealed record Item([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("name")] string? Name);

    public sealed record Line([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("primaryValue")] double PrimaryValue);
}
