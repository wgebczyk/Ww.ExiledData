using System.Globalization;
using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.MockGame;

internal enum ItemKind
{
    Currency,
    Gear,
    Waystone,
    Tablet,
}

/// <summary>An item as the game shows it: a footprint in cells, a short label and the Ctrl+C text.</summary>
/// <param name="Image">The icon path below the local icons folder (catalog), or null.</param>
internal sealed record MockItem(ItemKind Kind, string Label, string Corner, int Width, int Height, string Text, string? Image = null);

/// <summary>An item placed in the inventory, top-left cell zero-based.</summary>
internal sealed record Placed(MockItem Item, int Column, int Row)
{
    public bool Covers(int column, int row) =>
        column >= Column && column < Column + Item.Width && row >= Row && row < Row + Item.Height;
}

/// <summary>Generates item texts in the game's copy format.</summary>
internal static class MockItems
{
    private const string Separator = "--------";

    /// <summary>Code, icon and max stack come from the app's currency catalog.</summary>
    private sealed record CurrencyInfo(string Name, string Effect)
    {
        public int MaxStack => CurrencyCatalog.Default.Find(Name)?.MaxStackSize ?? 20;
    }

    private static readonly CurrencyInfo[] Currencies =
    [
        new("Orb of Transmutation", "Upgrades a Normal item to a Magic item with 1 modifier"),
        new("Orb of Augmentation", "Augments a Magic item with a new random modifier"),
        new("Orb of Alchemy", "Upgrades a Normal item to a Rare item with 4 random modifiers"),
        new("Regal Orb", "Upgrades a Magic item to a Rare item, adding 1 random modifier"),
        new("Exalted Orb", "Augments a Rare item with a new random modifier"),
        new("Chaos Orb", "Removes a random modifier and augments a Rare item with a new random modifier"),
        new("Orb of Annulment", "Removes a random modifier from an item"),
        new("Vaal Orb", "Corrupts an item, modifying it unpredictably"),
        new("Divine Orb", "Randomises the numeric values of the random modifiers on an item"),
        new("Fracturing Orb", "Fractures a random modifier on a Rare item with at least 4 modifiers, locking it in place"),
        new("Artificer's Orb", "Adds an Augment Socket to a Martial Weapon, wand, staff or Armour"),
        new("Gemcutter's Prism", "Improves the quality of a gem"),
        new("Glassblower's Bauble", "Improves the quality of a flask or charm"),
        new("Arcanist's Etcher", "Improves the quality of a wand, staff or sceptre"),
        new("Armourer's Scrap", "Improves the quality of an armour"),
        new("Blacksmith's Whetstone", "Improves the quality of a weapon"),
        new("Chance Shard", "Combine 20 Shards to create an Orb of Chance"),
    ];

    /// <summary>Common currencies weigh more, so random stacks look like real loot.</summary>
    private static readonly string[] CommonCurrencies =
        ["Orb of Transmutation", "Orb of Augmentation", "Armourer's Scrap", "Blacksmith's Whetstone", "Chance Shard", "Orb of Alchemy"];

    /// <summary>The left side of the inventory: stacks the player keeps, never randomized.</summary>
    public static IReadOnlyList<Placed> KeptItems() =>
    [
        new(Currency("Exalted Orb", 14), 0, 0),
        new(Currency("Chaos Orb", 9), 0, 1),
        new(Currency("Divine Orb", 2), 0, 2),
        new(Currency("Regal Orb", 6), 0, 3),
        new(Currency("Orb of Annulment", 3), 0, 4),
        new(Currency("Orb of Transmutation", 31), 1, 0),
    ];

    /// <summary>The first column random items may use; columns before it belong to the kept items.</summary>
    public const int RandomFromColumn = 2;

    public static IReadOnlyList<Placed> RandomInventory(Random rng, int columns, int rows)
    {
        var placed = new List<Placed>(KeptItems());
        var taken = new bool[columns, rows];
        foreach (var p in placed)
            Mark(taken, p, true);

        for (var column = RandomFromColumn; column < columns; column++)
        {
            for (var row = 0; row < rows; row++)
            {
                if (taken[column, row] || rng.NextDouble() > 0.55)
                    continue;
                var item = rng.NextDouble() < 0.75 ? RandomCurrency(rng) : RandomGear(rng);
                if (!Fits(taken, column, row, item))
                    item = RandomCurrency(rng);
                var p = new Placed(item, column, row);
                Mark(taken, p, true);
                placed.Add(p);
            }
        }
        return placed;
    }

    private static bool Fits(bool[,] taken, int column, int row, MockItem item)
    {
        if (column + item.Width > taken.GetLength(0) || row + item.Height > taken.GetLength(1))
            return false;
        for (var c = column; c < column + item.Width; c++)
            for (var r = row; r < row + item.Height; r++)
                if (taken[c, r])
                    return false;
        return true;
    }

    private static void Mark(bool[,] taken, Placed p, bool value)
    {
        for (var c = p.Column; c < p.Column + p.Item.Width; c++)
            for (var r = p.Row; r < p.Row + p.Item.Height; r++)
                taken[c, r] = value;
    }

    public static MockItem Currency(string name, int count)
    {
        var info = Currencies.First(c => c.Name == name);
        var text = Lines(
            "Item Class: Stackable Currency",
            "Rarity: Currency",
            info.Name,
            Separator,
            $"Stack Size: {count.ToString("N0", CultureInfo.InvariantCulture)}/{info.MaxStack}",
            Separator,
            info.Effect,
            Separator,
            "Right click this item then left click an item to apply it.",
            "Shift click to unstack.");
        var display = CurrencyBook.Default.Describe(info.Name);
        var label = display.Badge is { } badge ? $"{display.Code}/{badge}" : display.Code;
        return new MockItem(ItemKind.Currency, label, count.ToString(CultureInfo.InvariantCulture), 1, 1, text, display.Image);
    }

    private static MockItem RandomCurrency(Random rng)
    {
        var common = Pick(rng, CommonCurrencies);
        var info = rng.NextDouble() < 0.6 ? Currencies.First(c => c.Name == common) : Pick(rng, Currencies);
        // Rare currencies come in small stacks.
        var count = CommonCurrencies.Contains(info.Name) ? rng.Next(1, info.MaxStack + 1) : rng.Next(1, 6);
        return Currency(info.Name, count);
    }

    private sealed record GearBase(string ItemClass, string Name, int Width, int Height, string Property);

    private static readonly GearBase[] GearBases =
    [
        new("Rings", "Iron Ring", 1, 1, ""),
        new("Rings", "Sapphire Ring", 1, 1, ""),
        new("Amulets", "Jade Amulet", 1, 1, ""),
        new("Helmets", "Soldier Greathelm", 2, 2, "Armour: 124"),
        new("Gloves", "Ringmail Gauntlets", 2, 2, "Armour: 61"),
        new("Boots", "Laced Boots", 2, 2, "Evasion Rating: 58"),
        new("Body Armours", "Ironclad Vestments", 2, 3, "Armour: 164"),
        new("Body Armours", "Quilted Vest", 2, 3, "Evasion Rating: 120"),
        new("Wands", "Withered Wand", 1, 2, "Physical Damage: 4-9"),
        new("Bows", "Recurve Bow", 2, 4, "Physical Damage: 19-35"),
        new("Two Hand Maces", "Temple Maul", 2, 4, "Physical Damage: 35-72"),
    ];

    private static readonly string[] Prefixes = ["Doom", "Rapture", "Horror", "Storm", "Grim", "Gale", "Dusk", "Corpse"];
    private static readonly string[] Suffixes = ["Salvation", "Coil", "Bane", "Shroud", "Clasp", "Spiral", "Song", "Ward"];

    private static readonly string[] GearMods =
    [
        "+{0}(50-69) to maximum Life", "+{0}(20-30)% to Fire Resistance", "+{0}(20-30)% to Cold Resistance",
        "+{0}(20-30)% to Lightning Resistance", "+{0}(10-15) to Strength", "+{0}(10-15) to Dexterity",
        "{0}(10-20)% increased Rarity of Items found", "+{0}(25-40) to maximum Mana",
    ];

    private static MockItem RandomGear(Random rng)
    {
        var b = Pick(rng, GearBases);
        var rarity = Pick(rng, ["Normal", "Magic", "Rare"]);
        var lines = new List<string> { $"Item Class: {b.ItemClass}", $"Rarity: {rarity}" };
        if (rarity == "Rare")
            lines.Add($"{Pick(rng, Prefixes)} {Pick(rng, Suffixes)}");
        lines.Add(b.Name);
        if (b.Property.Length > 0)
            lines.AddRange([Separator, b.Property]);
        lines.AddRange([Separator, $"Item Level: {rng.Next(40, 82)}"]);
        var modCount = rarity switch { "Normal" => 0, "Magic" => rng.Next(1, 3), _ => rng.Next(3, 7) };
        if (modCount > 0)
        {
            lines.Add(Separator);
            lines.AddRange(GearMods.OrderBy(_ => rng.Next()).Take(modCount).Select(m => string.Format(m, rng.Next(10, 60))));
        }
        return new MockItem(ItemKind.Gear, Short(b.Name), rarity[..1], b.Width, b.Height, Lines([.. lines]));
    }

    private static readonly string[] WaystoneMods =
    [
        "Monsters deal {0}% extra Damage as Fire", "{0}% increased Monster Movement Speed", "Monsters have {0}% increased Area of Effect",
        "Players have {0}% less Recovery Rate of Life and Energy Shield", "{0}% increased number of Rare Monsters",
        "Monsters are Armoured", "Area contains {0} additional packs of Undead", "Monsters gain {0}% of their Maximum Life as Extra Energy Shield",
    ];

    public static MockItem RandomWaystone(Random rng)
    {
        var tier = rng.Next(1, 17);
        var rarity = Pick(rng, ["Normal", "Magic", "Rare"]);
        var baseName = $"Waystone (Tier {tier})";
        var lines = new List<string> { "Item Class: Waystones", $"Rarity: {rarity}" };
        switch (rarity)
        {
            case "Rare":
                lines.AddRange([$"{Pick(rng, Prefixes)} {Pick(rng, Suffixes)}", baseName]);
                break;
            case "Magic":
                lines.Add($"{baseName} of {Pick(rng, ["Hexwarding", "Frenzy", "Splinters", "Ruin"])}");
                break;
            default:
                lines.Add(baseName);
                break;
        }
        lines.AddRange([
            Separator,
            $"Waystone Tier: {tier}",
            $"Revives Available: {rng.Next(0, 6)}",
            $"Item Rarity: +{rng.Next(0, 60)}% (augmented)",
            $"Waystone Drop Chance: +{rng.Next(0, 120)}% (augmented)",
            Separator,
            $"Item Level: {Math.Min(82, 64 + tier + rng.Next(0, 3))}",
        ]);
        var modCount = rarity switch { "Normal" => 0, "Magic" => rng.Next(1, 3), _ => rng.Next(3, 7) };
        if (modCount > 0)
        {
            lines.Add(Separator);
            lines.AddRange(WaystoneMods.OrderBy(_ => rng.Next()).Take(modCount).Select(m => string.Format(m, rng.Next(10, 41))));
        }
        lines.AddRange([Separator, "Can be used in a Map Device, allowing you to enter a Map. Waystones can only be used once."]);
        return new MockItem(ItemKind.Waystone, $"T{tier}", rarity[..1], 1, 1, Lines([.. lines]));
    }

    private sealed record TabletBase(string Name, string Code, string Implicit);

    private static readonly TabletBase[] TabletBases =
    [
        new("Breach Tablet", "BRE", "Adds a Breach to a Map"),
        new("Delirium Tablet", "DEL", "Adds a Mirror of Delirium to a Map"),
        new("Ritual Tablet", "RIT", "Adds Ritual Altars to a Map"),
        new("Expedition Tablet", "EXP", "Adds an Expedition Encounter to a Map"),
        new("Abyss Tablet", "ABY", "Adds Abysses to a Map"),
        new("Overseer Tablet", "OVR", "Map Boss is accompanied by a Pinnacle Boss"),
    ];

    private static readonly string[] TabletMods =
    [
        "{0}% increased Quantity of Items found in your Maps", "{0}% increased Rarity of Items found in your Maps",
        "{0}% increased Experience gain in your Maps", "{0}% increased number of Rare Monsters in your Maps",
        "{0}% increased Pack size in your Maps", "Your Maps contain an additional Shrine",
    ];

    public static MockItem RandomTablet(Random rng)
    {
        var b = Pick(rng, TabletBases);
        var magic = rng.NextDouble() < 0.7;
        var uses = rng.Next(1, 11);
        var lines = new List<string>
        {
            "Item Class: Tablet",
            $"Rarity: {(magic ? "Magic" : "Normal")}",
            magic ? $"{Pick(rng, ["Fortunate", "Hallowed", "Teeming", "Abundant"])} {b.Name}" : b.Name,
            Separator,
            $"Uses Remaining: {uses}",
            Separator,
            $"Item Level: {rng.Next(65, 83)}",
            Separator,
            $"{b.Implicit} (implicit)",
        };
        if (magic)
        {
            lines.Add(Separator);
            lines.AddRange(TabletMods.OrderBy(_ => rng.Next()).Take(rng.Next(1, 3)).Select(m => string.Format(m, rng.Next(5, 21))));
        }
        lines.AddRange([Separator, "Can be used in a personal Map Device to add modifiers to Maps."]);
        return new MockItem(ItemKind.Tablet, b.Code, uses.ToString(), 1, 1, Lines([.. lines]));
    }

    private static string Short(string name) =>
        string.Concat(name.Split(' ').Select(w => w[0]));

    private static T Pick<T>(Random rng, IReadOnlyList<T> items) => items[rng.Next(items.Count)];

    // The game copies with CRLF line ends.
    private static string Lines(params string[] lines) => string.Join("\r\n", lines);
}
