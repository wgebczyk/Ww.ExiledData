namespace Ww.ExiledData.Core.Text;

/// <summary>Display label derived on the fly from the name lines (never stored as identity).</summary>
public static class ItemLabel
{
    public static string From(string text)
    {
        var header = ItemText.Sections(ItemText.Normalize(text))[0];
        var rarityIndex = Array.FindIndex(header, l => l.StartsWith(ItemText.RarityPrefix, StringComparison.Ordinal));
        var nameLines = header
            .Skip(rarityIndex + 1)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith(ItemText.ItemClassPrefix, StringComparison.Ordinal));
        var label = string.Join(' ', nameLines);
        return label.Length > 0 ? label : "(unnamed item)";
    }
}
