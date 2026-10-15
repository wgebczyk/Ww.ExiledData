namespace Ww.ExiledData.Tests;

/// <summary>Sample clipboard texts copied from the game (see PLAN.md appendix).</summary>
internal static class Fixture
{
    public const string ChanceShard = "chance-shard";
    public const string Verisium = "verisium";
    public const string ArtificersOrb = "artificers-orb";
    public const string Transmutation = "transmutation";
    public const string Waystone = "waystone";
    public const string BodyArmourRare = "body-armour-rare";

    /// <summary>MAPPING_PLAN.md appendix B: several currencies, separated by <c>===</c> lines.</summary>
    public const string CurrencySamples = "currency-samples";

    public static string[] Samples(string name) =>
        Load(name).Replace("\r\n", "\n").Split("\n===\n", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static string Load(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".txt"));

    /// <summary>The game copies with Windows line endings.</summary>
    public static string LoadCrLf(string name) => Load(name).Replace("\r\n", "\n").Replace("\n", "\r\n");
}

internal sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}
