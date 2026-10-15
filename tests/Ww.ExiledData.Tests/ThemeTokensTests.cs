using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ww.ExiledData.Tests;

/// <summary>Guards for the theme tokens (docs/design-ui/principles.md, Color).</summary>
public sealed partial class ThemeTokensTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string AppDir = Path.Combine(RepoRoot(), "src", "Ww.ExiledData.App");

    private static readonly Dictionary<string, Dictionary<string, string>> Themes = LoadThemes();

    [Fact]
    public void Both_themes_define_the_same_keys()
    {
        Assert.Equal(["Dark", "Light"], Themes.Keys.Order());
        Assert.Equal(Themes["Dark"].Keys.Order(), Themes["Light"].Keys.Order());
    }

    [Theory]
    [InlineData("TextBrush", "PanelBrush", 4.5)]
    [InlineData("TextSecondaryBrush", "PanelBrush", 4.5)]
    [InlineData("TextMutedBrush", "PanelBrush", 4.5)]
    [InlineData("TextMutedBrush", "PanelRaisedBrush", 4.5)]
    [InlineData("TextMutedBrush", "BgChromeBrush", 4.5)]
    [InlineData("AmberTextBrush", "PanelBrush", 4.5)]
    [InlineData("AmberTextBrush", "AmberTintBrush", 4.5)]
    [InlineData("OnAmberBrush", "AmberFillBrush", 4.5)]
    [InlineData("BlueTextBrush", "BlueTintBrush", 4.5)]
    [InlineData("DangerTextBrush", "PanelBrush", 4.5)]
    [InlineData("DangerTextBrush", "DangerTintBrush", 4.5)]
    [InlineData("DangerTextBrush", "BgChromeBrush", 4.5)]
    [InlineData("SuccessTextBrush", "PanelBrush", 4.5)]
    [InlineData("SuccessTextBrush", "SuccessTintBrush", 4.5)]
    [InlineData("SuccessTextBrush", "BgChromeBrush", 4.5)]
    [InlineData("InfoTextBrush", "InfoTintBrush", 4.5)]
    [InlineData("AmberTextBrush", "PanelRaisedBrush", 4.5)]
    [InlineData("DeltaDownBrush", "PanelBrush", 4.5)]
    [InlineData("BlueFillBrush", "PanelBrush", 3.0)]
    public void Text_and_indicator_pairs_meet_WCAG_contrast(string foreground, string background, double minimum)
    {
        foreach (var (theme, tokens) in Themes)
        {
            var ratio = Contrast(tokens[foreground], tokens[background]);
            Assert.True(ratio >= minimum, $"{theme}: {foreground} on {background} is {ratio:F2}, needs {minimum}");
        }
    }

    [Fact]
    public void Light_amber_accent_is_visible_on_raised_panels()
    {
        var ratio = Contrast(Themes["Light"]["AmberAccentBrush"], Themes["Light"]["PanelRaisedBrush"]);
        Assert.True(ratio >= 3.0, $"AmberAccent on PanelRaised is {ratio:F2}");
    }

    /// <summary>The UI takes every color from the token dictionary (docs/design-ui/principles.md, Color).</summary>
    [Theory]
    [InlineData("Theme/Controls.axaml")]
    [InlineData("Views/MainWindow.axaml")]
    [InlineData("ViewModels/MainWindowViewModel.cs")]
    [InlineData("Views/SessionListView.axaml")]
    [InlineData("Views/SessionView.axaml")]
    [InlineData("Views/SettingsWindow.axaml")]
    [InlineData("Views/ConfirmDialog.cs")]
    [InlineData("Views/PromptDialog.cs")]
    [InlineData("Views/NewSessionDialog.cs")]
    [InlineData("ViewModels/SessionListViewModel.cs")]
    [InlineData("ViewModels/SessionViewModel.cs")]
    [InlineData("ViewModels/SweepControlViewModel.cs")]
    [InlineData("ViewModels/SettingsViewModel.cs")]
    [InlineData("ViewModels/Settings/SettingFields.cs")]
    [InlineData("Theme/SettingsControls.axaml")]
    [InlineData("Controls/GridPreview.cs")]
    [InlineData("Controls/KeyRecorderBox.cs")]
    [InlineData("Controls/PlacementPreview.cs")]
    [InlineData("Controls/TimingBar.cs")]
    [InlineData("Controls/CurrencyCell.cs")]
    [InlineData("Theme/MappingControls.axaml")]
    [InlineData("Views/GatherView.axaml")]
    [InlineData("Views/CapturesView.axaml")]
    [InlineData("ViewModels/GatherViewModel.cs")]
    [InlineData("ViewModels/GatherRows.cs")]
    [InlineData("ViewModels/AnalyzeViewModel.cs")]
    [InlineData("Views/AnalyzeView.axaml")]
    [InlineData("Controls/AnalyzeChart.cs")]
    [InlineData("Views/AddChartDialog.cs")]
    [InlineData("Controls/StrokeIcon.cs")]
    [InlineData("ViewModels/GatherDialogs.cs")]
    [InlineData("Views/RecallDialog.axaml")]
    [InlineData("Views/ReviewDialog.axaml")]
    public void New_UI_files_have_no_hard_coded_colors(string file)
    {
        var text = File.ReadAllText(Path.Combine(AppDir, file));
        Assert.Empty(HardCodedColor().Matches(text).Select(m => m.Value));
    }

    /// <summary>
    /// docs/design-ui/tokens.json is generated from Tokens.axaml; with UPDATE_DESIGN_UI=1 this test rewrites it
    /// (tools/design-ui/update does that).
    /// </summary>
    [Fact]
    public void Design_tokens_file_matches_the_token_dictionary()
    {
        var path = Path.Combine(RepoRoot(), "docs", "design-ui", "tokens.json");
        var expected = DesignTokensJson();
        if (Environment.GetEnvironmentVariable("UPDATE_DESIGN_UI") == "1")
        {
            File.WriteAllText(path, expected);
            return;
        }
        var actual = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : "";
        Assert.True(actual == expected, "docs/design-ui/tokens.json is out of date: run tools/design-ui/update");
    }

    private static string DesignTokensJson()
    {
        var root = XDocument.Load(Path.Combine(AppDir, "Theme", "Tokens.axaml")).Root!;
        var themes = root.Elements().Single(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries").Elements()
            .ToDictionary(
                d => (string)d.Attribute(X + "Key")!,
                d => d.Elements().Where(e => e.Name.LocalName is "SolidColorBrush" or "Color")
                    .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => ((string?)e.Attribute("Color") ?? e.Value).Trim().ToLowerInvariant()));
        Dictionary<string, string> Plain(string element) => root.Elements().Where(e => e.Name.LocalName == element)
            .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => e.Value.Trim());
        var document = new Dictionary<string, object>
        {
            ["generatedFrom"] = "src/Ww.ExiledData.App/Theme/Tokens.axaml",
            ["colors"] = themes,
            ["fonts"] = Plain("FontFamily"),
            ["cornerRadius"] = Plain("CornerRadius"),
        };
        var options = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            NewLine = "\n",
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return System.Text.Json.JsonSerializer.Serialize(document, options) + "\n";
    }

    [GeneratedRegex(@"#[0-9a-fA-F]{6,8}\b|Color\.(Parse|FromArgb|FromRgb)|Brushes\.(?!Transparent)\w+")]
    private static partial Regex HardCodedColor();

    private static Dictionary<string, Dictionary<string, string>> LoadThemes()
    {
        var doc = XDocument.Load(Path.Combine(AppDir, "Theme", "Tokens.axaml"));
        return doc.Root!.Elements().Single(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries")
            .Elements()
            .ToDictionary(
                d => (string)d.Attribute(X + "Key")!,
                d => d.Elements().Where(e => e.Name.LocalName == "SolidColorBrush")
                    .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => (string)e.Attribute("Color")!));
    }

    private static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int i)
        {
            var c = int.Parse(hex.AsSpan(1 + i * 2, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ww.ExiledData.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
