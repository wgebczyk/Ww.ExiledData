using System.Globalization;
using Ww.ExiledData.Capture;
using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.App.Services;

/// <summary>
/// Sample runs for <c>--screenshot</c>: a temporary mapping session fed through the real capture path (copies and
/// sweeps go through the controller and the seam), for the screenshots in docs/design-ui/screens.
/// </summary>
internal static class SampleMapping
{
    private static readonly GridKind Inventory = new("Inventory", 12, 5);
    private static readonly GridGeometry Grid = new(1000, 600, 50, 12, 5);

    public const string Name = "Screenshot · farming rituals";

    public static async Task<Guid> Create(SessionController controller, MappingService mapping)
    {
        var id = await controller.StartNewSession(Name, SessionKind.Mapping);
        await mapping.AddTag(id, id, "rituals: 6");
        await mapping.AddTag(id, id, "lot of rare");

        const ScanKind Swipe = ScanKind.Swipe, Stash = ScanKind.Stash, Final = ScanKind.Final;

        // Run 1: 5 Exalted and 3 Chaos kept (the Initial swipe), two stash trips, a Final that matches.
        Copy(controller, Waystone(15, "of Hexwarding"));
        Copy(controller, Tablet("Ritual Tablet"));
        Copy(controller, Tablet("Breach Tablet"));
        await Scan(controller, mapping, Swipe, ("Exalted Orb", 5, 20), ("Chaos Orb", 3, 20));
        await Scan(controller, mapping, Stash, ("Orb of Transmutation", 40, 40), ("Orb of Transmutation", 38, 40), ("Chaos Orb", 58, 20),
            ("Orb of Alchemy", 12, 20), ("Divine Orb", 1, 20));
        await Scan(controller, mapping, Stash, ("Orb of Transmutation", 133, 40), ("Chaos Orb", 72, 20), ("Greater Orb of Transmutation", 18, 40),
            ("Exalted Orb", 2, 20));
        await Scan(controller, mapping, Final, ("Exalted Orb", 5, 20), ("Chaos Orb", 3, 20));
        var first = (await mapping.Load(id)).Runs[0].Id;
        await mapping.AddTag(id, first, "rituals: 6");

        // Run 2: an unknown item as a possible tablet; the Final shows 2 Exalted never stashed (a warning to review).
        Copy(controller, Waystone(14, "of Frenzy"));
        Copy(controller, Tablet("Delirium Tablet"));
        Copy(controller, Unknown("Strange Carving"));
        await Scan(controller, mapping, Swipe, ("Exalted Orb", 5, 20));
        await Scan(controller, mapping, Stash, ("Orb of Transmutation", 198, 40), ("Chaos Orb", 133, 20), ("Orb of Annulment", 6, 20),
            ("Vaal Orb", 9, 20), ("Armourer's Scrap", 14, 40));
        await Scan(controller, mapping, Final, ("Exalted Orb", 7, 20));
        var second = (await mapping.Load(id)).Runs[1].Id;
        await mapping.AddTag(id, second, "died");
        await mapping.SetNote(id, second, "Boss twice, lost the portal.");

        // Runs 3-9: one stash scan each, rituals from 3 to 9 (for the Analyse chart and compare).
        for (var i = 0; i < 7; i++)
        {
            var rituals = 3 + i;
            Copy(controller, Waystone(15, i % 2 == 0 ? "of Hexwarding" : "of Frenzy"));
            Copy(controller, Tablet("Ritual Tablet"));
            Copy(controller, Tablet("Breach Tablet"));
            await Scan(controller, mapping, Stash, ("Orb of Transmutation", 150 + 13 * rituals + (i % 3) * 20, 40),
                ("Chaos Orb", 90 + 11 * rituals - (i % 2) * 25, 20), ("Orb of Alchemy", 20 + 3 * i, 20), ("Divine Orb", i % 3 == 0 ? 2 : 0, 20),
                ("Vaal Orb", 5 + i, 20));
            var run = (await mapping.Load(id)).Runs[^1].Id;
            await mapping.AddTag(id, run, $"rituals: {rituals}");
        }

        // The last run: running; the Initial swipe holds a spare waystone and tablet (for the recall lists), then a
        // stash scan, a swipe and a second stash scan.
        Copy(controller, Waystone(15, "of Hexwarding"));
        Copy(controller, Tablet("Ritual Tablet"));
        Copy(controller, Tablet("Breach Tablet"));
        Copy(controller, Tablet("Delirium Tablet"));
        await ScanItems(controller, mapping, Swipe, Currency("Exalted Orb", 5, 20), Waystone(16, "of Splinters"), Tablet("Expedition Tablet"));
        await Scan(controller, mapping, Stash, ("Orb of Transmutation", 120, 40), ("Greater Orb of Transmutation", 41, 40),
            ("Orb of Alchemy", 38, 20), ("Chaos Orb", 90, 20), ("Greater Chaos Orb", 22, 20), ("Orb of Annulment", 7, 20), ("Vaal Orb", 9, 20),
            ("Divine Orb", 2, 20), ("Regal Orb", 4, 20));
        await Scan(controller, mapping, Swipe, ("Exalted Orb", 5, 20));
        await Scan(controller, mapping, Stash, ("Orb of Transmutation", 92, 40), ("Perfect Orb of Transmutation", 3, 40), ("Chaos Orb", 61, 20),
            ("Arcanist's Etcher", 5, 20), ("Armourer's Scrap", 14, 40), ("Blacksmith's Whetstone", 31, 40), ("Glassblower's Bauble", 12, 20),
            ("Gemcutter's Prism", 6, 20), ("Artificer's Orb", 3, 20), ("Chance Shard", 17, 20), ("Verisium", 31, 1000));
        var third = (await mapping.Load(id)).Runs[^1].Id;
        await mapping.AddTag(id, third, "rituals: 6");
        await mapping.AddTag(id, third, "lot of rare");
        return id;
    }

    private static async Task Scan(SessionController controller, MappingService mapping, ScanKind kind, params (string Name, int Count, int Max)[] stacks) =>
        await ScanItems(controller, mapping, kind, stacks.Select(s => Currency(s.Name, s.Count, s.Max)).ToArray());

    private static async Task ScanItems(SessionController controller, MappingService mapping, ScanKind kind, params string[] items)
    {
        await mapping.SetNextScanKind(kind);
        await controller.StartSweep(Inventory, Grid);
        for (var i = 0; i < items.Length; i++)
        {
            var cell = new GridCell(i % Inventory.Columns, i / Inventory.Columns);
            Copy(controller, items[i], new ScreenPoint((int)(Grid.X + (cell.Column + 0.5) * Grid.CellSize),
                (int)(Grid.Y + (cell.Row + 0.5) * Grid.CellSize)));
        }
        await controller.FinishSweep();
    }

    private static void Copy(SessionController controller, string text, ScreenPoint? at = null) =>
        controller.OnItemCopied(new CopiedItem(text, at ?? new ScreenPoint(200, 200), DateTimeOffset.UtcNow, TimeSpan.Zero));

    private static string Currency(string name, int count, int max) =>
        $"Item Class: Stackable Currency\nRarity: Currency\n{name}\n--------\nStack Size: {count.ToString("#,0", CultureInfo.InvariantCulture)}/{max}\n";

    private static string Waystone(int tier, string suffix) =>
        $"Item Class: Waystones\nRarity: Magic\nWaystone (Tier {tier}) {suffix}\n--------\nItem Level: {65 + tier}\n";

    private static string Tablet(string name) => $"Item Class: Tablet\nRarity: Magic\n{name}\n--------\nUses Remaining: 10\n";

    private static string Unknown(string name) => $"Item Class: Relics\nRarity: Normal\n{name}\n";
}
