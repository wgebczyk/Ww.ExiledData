namespace Ww.ExiledData.Core.Text;

/// <summary>Helpers for the raw text the game puts on the clipboard on Ctrl+C.</summary>
public static class ItemText
{
    public const string ItemClassPrefix = "Item Class:";
    public const string RarityPrefix = "Rarity:";
    public const string SectionSeparator = "--------";

    /// <summary>Unifies line endings to <c>\n</c> and trims trailing whitespace.</summary>
    public static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();

    /// <summary>Hard requirement: copied item text starts with <c>Item Class:</c> (English client).</summary>
    public static bool IsItem(string? text) =>
        text is not null && text.TrimStart().StartsWith(ItemClassPrefix, StringComparison.Ordinal);

    public static IReadOnlyList<string[]> Sections(string normalizedText)
    {
        var sections = new List<string[]>();
        var current = new List<string>();
        foreach (var line in normalizedText.Split('\n'))
        {
            if (line.Trim() == SectionSeparator)
            {
                sections.Add([.. current]);
                current.Clear();
            }
            else
            {
                current.Add(line);
            }
        }
        sections.Add([.. current]);
        return sections;
    }

    /// <summary>Returns the trimmed value after <paramref name="prefix"/> in the first matching line, or null.</summary>
    public static string? FindValue(IEnumerable<string> lines, string prefix)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line[prefix.Length..].Trim();
        }
        return null;
    }
}
