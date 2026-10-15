using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Ww.ExiledData.Capture;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.App;

public enum OverlayPlacement
{
    NearCursor,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

public enum AppTheme
{
    /// <summary>Follows the OS light/dark mode, live.</summary>
    System,
    Dark,
    Light,
}

public sealed record AppSettings
{
    public AppTheme Theme { get; init; } = AppTheme.Dark;
    /// <summary>Pixels; well below one inventory/stash cell.</summary>
    public int PositionTolerance { get; init; } = 10;
    public int ClipboardTimeoutMs { get; init; } = 100;
    public string GameWindowTitle { get; init; } = "Path of Exile 2";
    public string HotkeyNewSession { get; init; } = "Ctrl+Alt+N";
    public string HotkeyUndo { get; init; } = "Ctrl+Alt+Z";
    public string HotkeyRedo { get; init; } = "Ctrl+Alt+Y";
    // Avoid letters that AltGr (= Ctrl+Alt) turns into characters on some layouts, e.g. Polish S, X, Z, C, N.
    /// <summary>Opens the grid editor; while it is open, confirms the grid and starts the sweep.</summary>
    public string HotkeySweepStart { get; init; } = "Ctrl+Alt+G";
    /// <summary>Finishes the running sweep; while the grid editor is open, cancels it.</summary>
    public string HotkeySweepFinish { get; init; } = "Ctrl+Alt+F";
    public string HotkeySkipCell { get; init; } = "Ctrl+Alt+K";
    // Mapping (recording mapping session only). Ergonomics to be revisited; same AltGr caution as above.
    public string HotkeyMapStashScan { get; init; } = "Ctrl+Alt+D";
    public string HotkeyMapSwipe { get; init; } = "Ctrl+Alt+W";
    public string HotkeyMapFinalScan { get; init; } = "Ctrl+Alt+Q";
    public string HotkeyMapStartRun { get; init; } = "Ctrl+Alt+R";
    public string HotkeyMapEndRun { get; init; } = "Ctrl+Alt+T";
    /// <summary>Area kinds for sweeps, as <c>Name=CxR; ...</c>.</summary>
    public string GridKinds { get; init; } = GridKind.FormatList(GridKind.Presets);
    /// <summary>The area kind picked last for a sweep.</summary>
    public string? LastGridKind { get; init; }
    public OverlayPlacement OverlayPlacement { get; init; } = OverlayPlacement.NearCursor;
    /// <summary>Clock-hand sweep length; independent of the clipboard wait.</summary>
    public int FeedbackSweepMs { get; init; } = 300;
    /// <summary>How long the outcome glyph stays visible after the sweep.</summary>
    public int FeedbackResultMs { get; init; } = 700;
    public LogLevel LogLevel { get; init; } = LogLevel.Debug;
    /// <summary>
    /// The condensed loot grid of mapping sessions: rows of up to 9 currency names, null for an empty place. Missing or
    /// empty means <see cref="DefaultLootGrid"/>.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string?>> LootGrid { get; init; } = DefaultLootGrid;

    /// <summary>The grid used until one is defined: the common orbs, their Greater / Perfect tiers and the crafting currencies.</summary>
    public static IReadOnlyList<IReadOnlyList<string?>> DefaultLootGrid { get; } =
    [
        ["Orb of Transmutation", "Greater Orb of Transmutation", "Perfect Orb of Transmutation", "Orb of Alchemy", "Vaal Orb",
         "Orb of Annulment", "Lesser Jeweller's Orb", "Greater Jeweller's Orb", "Perfect Jeweller's Orb"],
        ["Orb of Augmentation", "Greater Orb of Augmentation", "Perfect Orb of Augmentation", "Orb of Chance", "Fracturing Orb",
         "Divine Orb", null, null, "Artificer's Orb"],
        ["Regal Orb", "Greater Regal Orb", "Perfect Regal Orb", "Mirror of Kalandra", "Hinekora's Lock",
         null, "Arcanist's Etcher", "Armourer's Scrap", "Blacksmith's Whetstone"],
        ["Exalted Orb", "Greater Exalted Orb", "Perfect Exalted Orb", null, null, null, null, "Glassblower's Bauble", "Gemcutter's Prism"],
        ["Chaos Orb", "Greater Chaos Orb", "Perfect Chaos Orb"],
    ];

    /// <summary>The loot grid; <see cref="DefaultLootGrid"/> when none is defined.</summary>
    public LootLayout ParsedLootGrid() =>
        LootLayout.From(LootGrid) is { Rows.Count: > 0 } layout ? layout : LootLayout.From(DefaultLootGrid);

    public IReadOnlyList<GridKind> ParsedGridKinds() =>
        GridKind.TryParseList(GridKinds, out var kinds, out _) ? kinds : GridKind.Presets;

    private (string Name, HotkeyAction Action, string Value)[] HotkeyEntries() =>
    [
        ("New session", HotkeyAction.NewSession, HotkeyNewSession),
        ("Undo", HotkeyAction.Undo, HotkeyUndo),
        ("Redo", HotkeyAction.Redo, HotkeyRedo),
        ("Sweep start", HotkeyAction.SweepStart, HotkeySweepStart),
        ("Sweep finish", HotkeyAction.SweepFinish, HotkeySweepFinish),
        ("Skip cell", HotkeyAction.SkipCell, HotkeySkipCell),
        ("Stash scan", HotkeyAction.MapStashScan, HotkeyMapStashScan),
        ("Swipe", HotkeyAction.MapSwipe, HotkeyMapSwipe),
        ("Final scan", HotkeyAction.MapFinalScan, HotkeyMapFinalScan),
        ("Start new run", HotkeyAction.MapStartRun, HotkeyMapStartRun),
        ("End run", HotkeyAction.MapEndRun, HotkeyMapEndRun),
    ];

    public CaptureOptions ToCaptureOptions() => new()
    {
        ClipboardTimeout = TimeSpan.FromMilliseconds(ClipboardTimeoutMs),
        GameWindowTitle = string.IsNullOrWhiteSpace(GameWindowTitle) ? null : GameWindowTitle,
        Hotkeys = HotkeyEntries().ToDictionary(h => h.Action, h => Hotkey.Parse(h.Value)),
    };

    /// <summary>Returns problems that would make the settings unusable; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (PositionTolerance is < 0 or > 200)
            errors.Add("Position tolerance must be 0-200 px.");
        if (ClipboardTimeoutMs is < 10 or > 2000)
            errors.Add("Clipboard timeout must be 10-2000 ms.");
        if (FeedbackSweepMs is < 50 or > 3000 || FeedbackResultMs is < 50 or > 5000)
            errors.Add("Feedback durations are out of range.");
        if (!GridKind.TryParseList(GridKinds, out _, out var gridError))
            errors.Add(gridError!);
        foreach (var (name, _, value) in HotkeyEntries())
        {
            if (!Hotkey.TryParse(value, out _))
                errors.Add($"{name} hotkey '{value}' is not valid (e.g. Ctrl+Alt+Z).");
        }
        var hotkeys = HotkeyEntries()
            .Select(h => Hotkey.TryParse(h.Value, out var k) ? k : null).OfType<Hotkey>().ToList();
        if (hotkeys.Distinct().Count() != hotkeys.Count)
            errors.Add("Hotkeys must be different from each other.");
        if (hotkeys.Any(h => h == new Hotkey(KeyModifiers.Ctrl, 'C')))
            errors.Add("Ctrl+C is reserved for item capture.");
        return errors;
    }
}

public static class SettingsFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string path, out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(path))
                return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
            var errors = settings.Validate();
            if (errors.Count == 0)
                return settings;
            problem = $"Settings in {path} are invalid ({string.Join(" ", errors)}); using defaults.";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            problem = $"Could not read settings {path}: {ex.Message}; using defaults.";
        }
        return new AppSettings();
    }

    public static void Save(string path, AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
    }
}

public sealed class AppPaths(string dataDirectory)
{
    /// <summary>%LocalAppData%\Ww.ExiledData, or WW_EXILEDDATA_HOME when set (handy for testing).</summary>
    public static AppPaths Default() => new(Environment.GetEnvironmentVariable("WW_EXILEDDATA_HOME") is { Length: > 0 } home
        ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ww.ExiledData"));

    public string DataDirectory { get; } = dataDirectory;
    public string Database => Path.Combine(DataDirectory, "exileddata.db");
    public string Settings => Path.Combine(DataDirectory, "settings.json");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    /// <summary>Currency icons, fetched by tools/currency-catalog/fetch-icons.</summary>
    public string IconsDirectory => Path.Combine(DataDirectory, "icons");
}
