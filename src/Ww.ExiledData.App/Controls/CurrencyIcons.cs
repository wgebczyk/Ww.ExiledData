using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Ww.ExiledData.App.Controls;

/// <summary>
/// Currency icons from the local icons folder (fetched by tools/currency-catalog/fetch-icons; game art, never in the
/// repo). A path is the catalog's <c>image</c>, relative to the folder. A missing or unreadable file is no icon, and
/// the views fall back to the code's letter.
/// </summary>
public static class CurrencyIcons
{
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The icons folder; set once at startup (null: no icons).</summary>
    public static string? Directory { get; set; }

    public static Bitmap? Load(string? image)
    {
        if (string.IsNullOrEmpty(image) || Directory is not { } directory)
            return null;
        return Cache.GetOrAdd(image, path =>
        {
            var file = Path.GetFullPath(Path.Combine(directory, path));
            // The catalog is trusted data, but a path must still stay inside the folder.
            if (!file.StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                return null;
            try
            {
                return new Bitmap(file);
            }
            catch (Exception) // a broken or unsupported file is just no icon
            {
                return null;
            }
        });
    }
}

/// <summary>Binds a catalog image path to its icon (<see cref="CurrencyIcons"/>), or null.</summary>
public sealed class CurrencyIconConverter : IValueConverter
{
    public static CurrencyIconConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => CurrencyIcons.Load(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
