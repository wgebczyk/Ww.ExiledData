using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ww.ExiledData.Mapping;

/// <summary>A currency-like item of poe2db: its short code and its icon path below the local icons folder.</summary>
public sealed record CatalogEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("image")] string? Image,
    [property: JsonPropertyName("maxStackSize")] int? MaxStackSize);

/// <summary>
/// The currency catalog (<c>tools/currency-catalog/catalog.json</c>, embedded; its README has the code rules). An item
/// listed on several poe2db pages is found by its first entry, which is the Currency page's.
/// </summary>
public sealed class CurrencyCatalog
{
    public const string FileName = "catalog.json";

    private readonly Dictionary<string, CatalogEntry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CatalogEntry> _entries = [];

    public CurrencyCatalog(IEnumerable<CatalogEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (_byName.TryAdd(entry.Name, entry))
                _entries.Add(entry);
        }
    }

    /// <summary>One entry per name, in catalog order.</summary>
    public IReadOnlyList<CatalogEntry> Entries => _entries;

    /// <summary>The catalog shipped with the app.</summary>
    public static CurrencyCatalog Default { get; } = Parse(ReadEmbedded());

    public static CurrencyCatalog Empty { get; } = new([]);

    public CatalogEntry? Find(string name) => _byName.GetValueOrDefault(name);

    public static CurrencyCatalog Parse(string json) =>
        new((JsonSerializer.Deserialize<List<CatalogEntry>>(json) ?? throw new InvalidDataException("Empty catalog."))
            .Where(e => !string.IsNullOrEmpty(e.Name) && !string.IsNullOrEmpty(e.Code)));

    private static string ReadEmbedded()
    {
        using var stream = typeof(CurrencyCatalog).Assembly.GetManifestResourceStream($"Ww.ExiledData.Mapping.{FileName}")
            ?? throw new InvalidOperationException($"Embedded {FileName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
