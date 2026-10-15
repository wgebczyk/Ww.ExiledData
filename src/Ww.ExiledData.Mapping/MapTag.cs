using System.Globalization;

namespace Ww.ExiledData.Mapping;

/// <summary>
/// A free-text tag on a session or a run, optionally <c>name: value</c> (MAPPING_PLAN.md 3.5): <c>rituals: 6</c>,
/// <c>died</c>. The value is any text; it compares as a number only when it parses as one.
/// </summary>
public sealed record MapTag(string Name, string? Value = null)
{
    /// <summary>The value as a number, when it parses as one (invariant culture, either decimal separator).</summary>
    public double? Number => ParseNumber(Value);

    /// <summary>Text as a number (invariant culture, either decimal separator), or null.</summary>
    public static double? ParseNumber(string? text) =>
        text is not null && double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    /// <summary>Parses typed text: the name is before the first colon, the value after it. Null for blank text or name.</summary>
    public static MapTag? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var colon = text.IndexOf(':');
        if (colon < 0)
            return new MapTag(text.Trim());

        var name = text[..colon].Trim();
        var value = text[(colon + 1)..].Trim();
        return name.Length == 0 ? null : new MapTag(name, value.Length == 0 ? null : value);
    }

    /// <summary>The tag as typed back: <c>name: value</c> or <c>name</c>.</summary>
    public override string ToString() => Value is null ? Name : $"{Name}: {Value}";
}
