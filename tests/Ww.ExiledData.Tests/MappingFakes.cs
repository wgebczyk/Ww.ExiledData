using System.Globalization;
using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Parsing;
using Ww.ExiledData.Core.Recording;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Core.Text;
using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.Tests;

/// <summary>Item texts for mapping tests, in the game's format.</summary>
internal static class MapItems
{
    public static string Currency(string name, int count, int max = 40) => $"""
        Item Class: Stackable Currency
        Rarity: Currency
        {name}
        --------
        Stack Size: {count.ToString("#,0", CultureInfo.InvariantCulture)}/{max}
        --------
        Right click this item then left click an item to apply it.
        """;

    public static string Waystone(int tier, string suffix = "of Hexwarding") => $"""
        Item Class: Waystones
        Rarity: Magic
        Waystone (Tier {tier}) {suffix}
        --------
        Item Level: {58 + tier}
        --------
        Can be used in a Map Device, allowing you to enter a Map. Waystones can only be used once.
        """;

    public static string Tablet(string name, string itemClass = "Tablet") => $"""
        Item Class: {itemClass}
        Rarity: Magic
        {name}
        --------
        Uses Remaining: 10
        """;

    public static string Unknown(string name) => $"""
        Item Class: Mystery Things
        Rarity: Normal
        {name}
        """;

    public static string Rare(string name) => $"""
        Item Class: Body Armours
        Rarity: Rare
        {name}
        Garment
        """;

    public static string KeyOf(string name, int max = 40) => IdentityKeys.Stackable("Stackable Currency", "Currency", name, max);
}

/// <summary>Feeds a <see cref="MapSession"/> the way the seam does: captures and sweep events.</summary>
internal sealed class MapFeed
{
    private int _sweepCount;
    private int _nextCell;

    public MapSession Session { get; init; } = new(Guid.NewGuid());
    public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);
    public MappingOptions Options { get; } = new();
    public Guid? SweepId { get; private set; }

    public MappingOutcome Copy(string text, bool logged = true, GridCell? cell = null)
    {
        Now += TimeSpan.FromSeconds(1);
        return Session.Apply(Capture(Session.SessionId, text, Now, logged, cell is null ? null : SweepId, cell), Options);
    }

    public Scan StartScan(ScanKind? kind = null)
    {
        Now += TimeSpan.FromSeconds(1);
        SweepId = Guid.NewGuid();
        _nextCell = 0;
        return Session.Apply(Sweep(SweepStatus.Active), kind)!;
    }

    /// <summary>Copies a currency stack into the next cell of the running scan (or the given one).</summary>
    public MappingOutcome Stack(string name, int count, GridCell? cell = null) =>
        Copy(MapItems.Currency(name, count), cell: cell ?? new GridCell(_nextCell++, 0));

    public Scan End(SweepStatus status = SweepStatus.Finished)
    {
        Now += TimeSpan.FromSeconds(1);
        var scan = Session.Apply(Sweep(status))!;
        SweepId = null;
        return scan;
    }

    /// <summary>A whole scan: one stack per entry, each in its own cell.</summary>
    public Scan Scan(ScanKind kind, params (string Name, int Count)[] stacks)
    {
        StartScan(kind);
        foreach (var (name, count) in stacks)
            Stack(name, count);
        return End();
    }

    public Scan Swipe(params (string Name, int Count)[] stacks) => Scan(ScanKind.Swipe, stacks);

    public Scan Stash(params (string Name, int Count)[] stacks) => Scan(ScanKind.Stash, stacks);

    public Scan Final(params (string Name, int Count)[] stacks) => Scan(ScanKind.Final, stacks);

    private SweepChanged Sweep(SweepStatus status) =>
        new(Session.SessionId, SessionKind.Mapping, SweepId!.Value, $"Inventory #{++_sweepCount}", status, Now);

    /// <summary>A published capture, built the way the session controller builds it.</summary>
    public static ProcessedCapture Capture(Guid sessionId, string text, DateTimeOffset at, bool logged = true, Guid? sweepId = null, GridCell? cell = null)
    {
        var normalized = ItemText.Normalize(text);
        var parse = StackableParser.Parse(normalized);
        var stack = parse.Status == StackParseStatus.Parsed
            ? new CapturedStack(IdentityKeys.Stackable(parse.ItemClass!, parse.Rarity!, parse.Name!, parse.MaxStack),
                parse.ItemClass!, parse.Rarity!, parse.Name!, parse.Count, parse.MaxStack)
            : null;
        return new ProcessedCapture(sessionId, SessionKind.Mapping, logged ? Guid.NewGuid() : null, normalized, new ScreenPoint(0, 0), at,
            stack, sweepId, cell);
    }
}
