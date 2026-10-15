using System.Globalization;
using Ww.ExiledData.Core.Text;

namespace Ww.ExiledData.Core.Parsing;

public enum StackParseStatus
{
    /// <summary>No <c>Stack Size:</c> line: a regular item.</summary>
    NotStackable,
    Parsed,
    /// <summary>Stack line present, but class/rarity/name could not be read. Keep capture, flag for review.</summary>
    HeaderUnparsed,
    /// <summary>Stack line present, but count/max could not be read. Treat as regular item.</summary>
    CountUnparsed,
}

public sealed record StackParseResult(
    StackParseStatus Status,
    string? ItemClass = null,
    string? Rarity = null,
    string? Name = null,
    int Count = 0,
    int MaxStack = 0,
    string? Warning = null);

/// <summary>
/// The single place holding stackable parsing rules. Bump <see cref="Version"/> whenever the rules change,
/// so stored records can be re-parsed.
/// </summary>
public static class StackableParser
{
    public const int Version = 1;
    public const string StackSizePrefix = "Stack Size:";

    public static StackParseResult Parse(string text)
    {
        var sections = ItemText.Sections(ItemText.Normalize(text));
        var stackValue = sections.Select(s => ItemText.FindValue(s, StackSizePrefix)).FirstOrDefault(v => v is not null);
        if (stackValue is null)
            return new StackParseResult(StackParseStatus.NotStackable);

        if (!TryParseStackSize(stackValue, out var count, out var max))
            return new StackParseResult(StackParseStatus.CountUnparsed,
                Warning: $"Could not parse stack size '{stackValue}'; recorded as a regular item.");

        var header = sections[0];
        var itemClass = ItemText.FindValue(header, ItemText.ItemClassPrefix);
        var rarity = ItemText.FindValue(header, ItemText.RarityPrefix);
        var rarityIndex = Array.FindIndex(header, l => l.StartsWith(ItemText.RarityPrefix, StringComparison.Ordinal));
        var name = rarityIndex >= 0
            ? string.Join(' ', header.Skip(rarityIndex + 1).Select(l => l.Trim()).Where(l => l.Length > 0))
            : "";

        if (string.IsNullOrEmpty(itemClass) || string.IsNullOrEmpty(rarity) || name.Length == 0)
            return new StackParseResult(StackParseStatus.HeaderUnparsed, Count: count, MaxStack: max,
                Warning: "Stackable header (class/rarity/name) could not be parsed; capture needs review.");

        return new StackParseResult(StackParseStatus.Parsed, itemClass, rarity, name, count, max);
    }

    private static bool TryParseStackSize(string value, out int count, out int max)
    {
        count = max = 0;
        var parts = value.Split('/');
        return parts.Length == 2
            && TryParseNumber(parts[0], out count)
            && TryParseNumber(parts[1], out max)
            && count >= 0 && max > 0;
    }

    private static bool TryParseNumber(string s, out int n) =>
        int.TryParse(s.Trim().Replace(",", "").Replace(" ", "").Replace(" ", ""),
            NumberStyles.None, CultureInfo.InvariantCulture, out n);
}
