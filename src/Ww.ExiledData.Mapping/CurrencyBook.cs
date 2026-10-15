namespace Ww.ExiledData.Mapping;

/// <summary>How a currency is shown: a short code plus an L / G / P badge for Lesser / Greater / Perfect variants.</summary>
/// <param name="BaseName">The name without the Lesser / Greater / Perfect prefix.</param>
/// <param name="Image">The icon's path below the local icons folder (catalog), or null when not known.</param>
public sealed record CurrencyDisplay(string Name, string BaseName, string Code, string? Badge, string? Image = null);

/// <summary>
/// The condensed loot grid (MAPPING_PLAN.md 5), defined in the settings: rows of <see cref="Columns"/> equally wide
/// cells. A cell is a currency name, or null for a place that is left empty (not drawn). Build it with <see cref="From"/>.
/// </summary>
public sealed record LootLayout
{
    public const int Columns = 9;

    private LootLayout(IReadOnlyList<IReadOnlyList<string?>> rows) => Rows = rows;

    /// <summary>No grid: nothing is configured.</summary>
    public static LootLayout Empty { get; } = new([]);

    /// <summary>Each row has exactly <see cref="Columns"/> cells; the last row has at least one currency.</summary>
    public IReadOnlyList<IReadOnlyList<string?>> Rows { get; }

    /// <summary>Every currency that has a place in the grid.</summary>
    public IEnumerable<string> Names => Rows.SelectMany(r => r).OfType<string>();

    /// <summary>
    /// The layout of <paramref name="rows"/>: names trimmed (blank is empty), each row cut or padded to
    /// <see cref="Columns"/> cells, empty rows at the end dropped. Empty rows in between stay as gaps.
    /// </summary>
    public static LootLayout From(IEnumerable<IEnumerable<string?>?>? rows)
    {
        var cells = (rows ?? [])
            .Select(r => (IReadOnlyList<string?>)(r ?? []).Select(n => string.IsNullOrWhiteSpace(n) ? null : n.Trim())
                .Concat(Enumerable.Repeat<string?>(null, Columns)).Take(Columns).ToArray())
            .ToList();
        while (cells.Count > 0 && cells[^1].All(n => n is null))
            cells.RemoveAt(cells.Count - 1);
        return cells.Count == 0 ? Empty : new LootLayout(cells);
    }

    public bool SameAs(LootLayout other) =>
        Rows.Count == other.Rows.Count && Rows.Zip(other.Rows).All(p => p.First.SequenceEqual(p.Second));
}

/// <summary>
/// How currencies are shown, from the <see cref="CurrencyCatalog"/> (codes and icons; a currency it does not know gets
/// a code generated from its name), and the loot grid layout from the settings (<see cref="WithLayout"/>).
/// </summary>
public sealed class CurrencyBook
{
    private static readonly string[] SkippedWords = ["of", "the", "orb"];
    private static readonly (string Prefix, string Letter)[] Tiers = [("Lesser ", "L"), ("Greater ", "G"), ("Perfect ", "P")];
    private readonly CurrencyCatalog _catalog;

    public CurrencyBook(LootLayout? layout = null, CurrencyCatalog? catalog = null, PriceBook? prices = null, PriceUnit unit = PriceUnit.Adaptive)
    {
        Unit = unit;
        Layout = layout ?? LootLayout.Empty;
        _catalog = catalog ?? CurrencyCatalog.Default;
        Prices = prices ?? PriceBook.Empty;
    }

    public LootLayout Layout { get; }

    public CurrencyCatalog Catalog => _catalog;

    /// <summary>The last poe.ninja prices (<see cref="PriceBook.Empty"/> until the first pull).</summary>
    public PriceBook Prices { get; }

    /// <summary>How values are shown (settings).</summary>
    public PriceUnit Unit { get; }

    /// <summary>The same catalog with another grid.</summary>
    public CurrencyBook WithLayout(LootLayout layout) => new(layout, _catalog, Prices, Unit);

    /// <summary>The same catalog and grid with other prices.</summary>
    public CurrencyBook WithPrices(PriceBook prices) => new(Layout, _catalog, prices, Unit);

    /// <summary>The same catalog, grid and prices, shown in another unit.</summary>
    public CurrencyBook WithUnit(PriceUnit unit) => new(Layout, _catalog, Prices, unit);

    /// <summary>The value of <paramref name="count"/> <paramref name="name"/>, or null when it has no price.</summary>
    public CurrencyValue? ValueOf(string name, int count) => Prices.UnitValue(name) * count;

    /// <summary>The built-in catalog, no grid.</summary>
    public static CurrencyBook Default { get; } = new();

    /// <summary>
    /// The code from the catalog, else generated. A variant's catalog code ends with its tier letter (<c>ChOg</c>),
    /// which becomes the badge (<c>ChO</c> + G); a code that does not is shown whole.
    /// </summary>
    public CurrencyDisplay Describe(string name)
    {
        var (baseName, badge) = Tiers.FirstOrDefault(t => name.StartsWith(t.Prefix, StringComparison.Ordinal)) is ({ } prefix, { } letter)
            ? (name[prefix.Length..], letter)
            : (name, (string?)null);
        var entry = _catalog.Find(name);
        var image = entry?.Image ?? (badge is null ? null : _catalog.Find(baseName)?.Image);

        if (entry is not null)
        {
            return badge is not null && entry.Code.Length > 1 && entry.Code.EndsWith(badge, StringComparison.OrdinalIgnoreCase)
                ? new CurrencyDisplay(name, baseName, entry.Code[..^1], badge, image)
                : new CurrencyDisplay(name, name, entry.Code, null, image);
        }
        return new CurrencyDisplay(name, baseName, _catalog.Find(baseName)?.Code ?? GenerateCode(baseName), badge, image);
    }

    /// <summary>
    /// A code for a currency without one: the first three letters of its first significant word ("Exalted Orb" →
    /// EXA, "Orb of Augmentation" → AUG), continued with the next words when that word is shorter.
    /// </summary>
    public static string GenerateCode(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetter).ToArray()))
            .Where(w => w.Length > 0)
            .ToList();
        var significant = words.Where(w => !SkippedWords.Contains(w, StringComparer.OrdinalIgnoreCase)).ToList();
        var letters = string.Concat(significant.Count > 0 ? significant : words);
        return letters.Length == 0 ? "???" : letters[..Math.Min(3, letters.Length)].ToUpperInvariant();
    }
}
