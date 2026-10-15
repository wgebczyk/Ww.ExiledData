using Avalonia.Media.Imaging;

namespace Ww.ExiledData.MockGame;

/// <summary>Currency icons from the app's local icons folder (tools/currency-catalog/fetch-icons); null when missing.</summary>
internal static class MockIcons
{
    private static readonly string Directory = Path.Combine(GridSource.DataDirectory(), "icons");
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? Load(string? image)
    {
        if (string.IsNullOrEmpty(image))
            return null;
        if (!Cache.TryGetValue(image, out var bitmap))
        {
            var file = Path.Combine(Directory, image);
            try
            {
                bitmap = File.Exists(file) ? new Bitmap(file) : null;
            }
            catch (Exception) // a broken file is just no icon
            {
                bitmap = null;
            }
            Cache[image] = bitmap;
        }
        return bitmap;
    }
}
