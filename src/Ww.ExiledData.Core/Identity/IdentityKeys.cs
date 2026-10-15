using System.Security.Cryptography;
using System.Text;
using Ww.ExiledData.Core.Text;

namespace Ww.ExiledData.Core.Identity;

/// <summary>
/// Identity keys are computed once at capture time and stored; never recompute them for existing records.
/// </summary>
public static class IdentityKeys
{
    /// <summary>Regular items: hash of the normalized raw text.</summary>
    public static string Regular(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ItemText.Normalize(text))));

    /// <summary>Stackables: class + rarity + name + max stack. Count and descriptive text are excluded.</summary>
    public static string Stackable(string itemClass, string rarity, string name, int maxStack) =>
        $"{itemClass}|{rarity}|{name}|{maxStack}";
}
