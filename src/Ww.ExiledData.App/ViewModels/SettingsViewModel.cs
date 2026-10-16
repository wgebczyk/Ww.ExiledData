using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ww.ExiledData.App.ViewModels.Settings;
using Ww.ExiledData.Capture;
using Ww.ExiledData.Mapping;
using Unit = Ww.ExiledData.Mapping.PriceUnit;

namespace Ww.ExiledData.App.ViewModels;

/// <summary>What the Settings dialog needs to know about the running app.</summary>
/// <param name="FailedHotkeys">Hotkeys that another app held when capture last started.</param>
/// <param name="SettingsUnreadable">settings.json could not be read at start, so the defaults are shown.</param>
/// <param name="Currencies">Codes and icons for the loot grid editor; the built-in book when null.</param>
public sealed record SettingsEnvironment(
    string DataDirectory,
    IReadOnlyCollection<HotkeyAction> FailedHotkeys,
    bool SettingsUnreadable,
    CurrencyBook? Currencies = null);

/// <summary>
/// Settings dialog: a draft of <see cref="AppSettings"/> edited page by page,
/// validated as it is edited, applied on Save. Theme previews live and reverts on Cancel.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public const string RestartsCapture = "↻ Restarts capture when saved";

    private readonly AppSettings _saved;
    private readonly SettingsEnvironment _env;
    private readonly List<SettingField> _fields = [];

    public SettingsViewModel(AppSettings saved, SettingsEnvironment env)
    {
        _saved = saved;
        _env = env;
        var defaults = new AppSettings();

        Theme = new ChoiceField("Theme", "Dark suits play next to the game. System follows the Windows light/dark mode.",
            [(AppTheme.System, "System"), (AppTheme.Dark, "Dark"), (AppTheme.Light, "Light")], saved.Theme, defaults.Theme);
        LogLevel = new ChoiceField("Log level", "How much detail the log files get. Debug helps when reporting a problem.",
            new[] { Microsoft.Extensions.Logging.LogLevel.Trace, Microsoft.Extensions.Logging.LogLevel.Debug, Microsoft.Extensions.Logging.LogLevel.Information,
                    Microsoft.Extensions.Logging.LogLevel.Warning, Microsoft.Extensions.Logging.LogLevel.Error }
                .Select(l => ((object)l, l.ToString())),
            saved.LogLevel, defaults.LogLevel);

        GameWindowTitle = new TextField("Game window title",
            "Ctrl+C counts as a capture only while the window in front has this text in its title (not case-sensitive). Also decides which monitor shows the sweep grid.",
            RestartsCapture, saved.GameWindowTitle, defaults.GameWindowTitle, "Leave empty to accept any window.");
        ClipboardWait = new NumberField("Clipboard wait", "How long to wait for the game to put the item text on the clipboard after Ctrl+C.",
            RestartsCapture, saved.ClipboardTimeoutMs, defaults.ClipboardTimeoutMs, 10, 2000, 10, "ms",
            "Decrease clipboard wait", "Increase clipboard wait");
        PositionTolerance = new NumberField("Position tolerance",
            "How far apart two copies can be and still count as the same item position. Keep it well below one inventory cell.",
            "Applies instantly to the recording session", saved.PositionTolerance, defaults.PositionTolerance, 0, 200, 1, "px",
            "Decrease position tolerance", "Increase position tolerance");

        NewSession = new HotkeyField(HotkeyAction.NewSession, "New session", "Ends the recording session and starts a new one.",
            saved.HotkeyNewSession, defaults.HotkeyNewSession);
        Undo = new HotkeyField(HotkeyAction.Undo, "Undo", "Undoes the last change in the recording session.", saved.HotkeyUndo, defaults.HotkeyUndo);
        Redo = new HotkeyField(HotkeyAction.Redo, "Redo", "Redoes the change you undid.", saved.HotkeyRedo, defaults.HotkeyRedo);
        SweepStart = new HotkeyField(HotkeyAction.SweepStart, "Start / confirm grid",
            "Opens the grid editor. While it is open, confirms the grid and starts the sweep.", saved.HotkeySweepStart, defaults.HotkeySweepStart);
        SweepFinish = new HotkeyField(HotkeyAction.SweepFinish, "Finish / cancel grid",
            "Finishes the running sweep. In the grid editor, cancels it.", saved.HotkeySweepFinish, defaults.HotkeySweepFinish);
        SkipCell = new HotkeyField(HotkeyAction.SkipCell, "Skip cell under cursor",
            "During a sweep, marks the cell under the cursor as skipped. Press again to undo.", saved.HotkeySkipCell, defaults.HotkeySkipCell);
        MapInitialScan = new HotkeyField(HotkeyAction.MapInitialScan, "Initial scan",
            "Scans what you keep in the inventory before the map: the run's Initial (once per run).", saved.HotkeyMapInitialScan, defaults.HotkeyMapInitialScan);
        MapStashScan = new HotkeyField(HotkeyAction.MapStashScan, "Stash scan",
            "Scans the inventory right before you stash it: counts toward the run's loot.", saved.HotkeyMapStashScan, defaults.HotkeyMapStashScan);
        MapSwipe = new HotkeyField(HotkeyAction.MapSwipe, "Swipe",
            "Scans the inventory as a snapshot that never counts. The first swipe of a run is its Initial.", saved.HotkeyMapSwipe, defaults.HotkeyMapSwipe);
        MapFinalScan = new HotkeyField(HotkeyAction.MapFinalScan, "Final scan",
            "Scans the inventory at the end of a run, to compare with the Initial swipe.", saved.HotkeyMapFinalScan, defaults.HotkeyMapFinalScan);
        MapStartRun = new HotkeyField(HotkeyAction.MapStartRun, "Start new run",
            "Starts a run without copying a waystone (it ends the open run).", saved.HotkeyMapStartRun, defaults.HotkeyMapStartRun);
        MapEndRun = new HotkeyField(HotkeyAction.MapEndRun, "End run", "Ends the open run.", saved.HotkeyMapEndRun, defaults.HotkeyMapEndRun);

        Areas = new AreasField(saved.GridKinds, defaults.GridKinds);

        Placement = new ChoiceField("Placement", "Where the circle appears.",
            [(OverlayPlacement.NearCursor, "Near cursor"), (OverlayPlacement.TopLeft, "Top left"), (OverlayPlacement.TopRight, "Top right"),
             (OverlayPlacement.BottomLeft, "Bottom left"), (OverlayPlacement.BottomRight, "Bottom right")],
            saved.OverlayPlacement, defaults.OverlayPlacement);
        FeedbackSweep = new NumberField("Sweep animation", "Length of the clock-hand animation. Independent of the clipboard wait.", "",
            saved.FeedbackSweepMs, defaults.FeedbackSweepMs, 50, 3000, 50, "ms", "Shorter sweep animation", "Longer sweep animation");
        FeedbackResult = new NumberField("Result display", "How long the outcome glyph stays visible after the animation.", "",
            saved.FeedbackResultMs, defaults.FeedbackResultMs, 50, 5000, 50, "ms", "Shorter result display", "Longer result display");

        PriceUnit = new ChoiceField("Value unit",
            "The unit of the loot list's value column. Adaptive shows Divine Orbs, then Chaos, then Exalted Orbs once a value is under 1 (23.1 DIV, 4.20 ChO).",
            [(Unit.Adaptive, "Adaptive"), (Unit.Divine, "Divine"), (Unit.Exalted, "Exalted"), (Unit.Chaos, "Chaos")],
            saved.PriceUnit, defaults.PriceUnit);
        LootGrid = new LootGridField(saved.ParsedLootGrid(), defaults.ParsedLootGrid(), env.Currencies ?? CurrencyBook.Default);

        Hotkeys = [NewSession, Undo, Redo, SweepStart, SweepFinish, SkipCell, MapInitialScan, MapStashScan, MapSwipe, MapFinalScan, MapStartRun, MapEndRun];
        SessionHotkeys = [NewSession, Undo, Redo];
        SweepHotkeys = [SweepStart, SweepFinish, SkipCell];
        MappingHotkeys = [MapInitialScan, MapStashScan, MapSwipe, MapFinalScan, MapStartRun, MapEndRun];

        const string onSave = "Changes apply when you save, without a restart.";
        General = new SettingsPage("general", "General", "Appearance and diagnostics.", onSave, [Theme, LogLevel], "data folder logs");
        Capture = new SettingsPage("capture", "Capture", "How the app recognises an item copy in the game.", onSave,
            [GameWindowTitle, ClipboardWait, PositionTolerance]);
        HotkeysPage = new SettingsPage("hotkeys", "Hotkeys", "Press a combination to record it. Each one must be different.", onSave, [.. Hotkeys]);
        Sweeps = new SettingsPage("sweeps", "Sweeps", "The grids offered in the sweep area picker. Verify the sizes in the game.", onSave, [Areas],
            "area areas grid inventory stash columns rows");
        Feedback = new SettingsPage("feedback", "Feedback overlay", "The small circle that confirms each capture over the game.",
            "Applies to the next capture.", [Placement, FeedbackSweep, FeedbackResult], "circle overlay");
        Mapping = new SettingsPage("mapping", "Mapping", "Settings for mapping sessions.", "Applies to open sessions when you save.", [PriceUnit, LootGrid],
            "mapping loot grid currency currencies swipe stash final run price prices value unit divine exalted chaos");
        Pages = [General, Capture, HotkeysPage, Sweeps, Feedback, Mapping];

        foreach (var page in Pages)
            _fields.AddRange(page.Fields);
        foreach (var field in _fields)
            field.Edited += OnEdited;
        foreach (var hotkey in Hotkeys)
            hotkey.RecordingChanged += OnRecordingChanged;

        foreach (var page in Pages)
            VisiblePages.Add(page);
        SelectedPage = Capture;
        Revalidate();
    }

    // --- supplied by the host -------------------------------------------------------------------

    /// <summary>Theme preview while the dialog is open (and the saved theme again on Cancel).</summary>
    public Action<AppTheme>? PreviewTheme { get; set; }
    /// <summary>True while a hotkey is being recorded: the global hotkeys must not fire.</summary>
    public Action<bool>? SuspendHotkeys { get; set; }
    public Action? OpenLogs { get; set; }
    /// <summary>Saves and applies; returns an error message, or null on success.</summary>
    public Func<AppSettings, Task<string?>>? Apply { get; set; }
    /// <summary>Closes the dialog after a successful save.</summary>
    public Action? Saved { get; set; }

    // --- fields and pages -----------------------------------------------------------------------

    public ChoiceField Theme { get; }
    public ChoiceField LogLevel { get; }
    public TextField GameWindowTitle { get; }
    public NumberField ClipboardWait { get; }
    public NumberField PositionTolerance { get; }
    public HotkeyField NewSession { get; }
    public HotkeyField Undo { get; }
    public HotkeyField Redo { get; }
    public HotkeyField SweepStart { get; }
    public HotkeyField SweepFinish { get; }
    public HotkeyField SkipCell { get; }
    public HotkeyField MapInitialScan { get; }
    public HotkeyField MapStashScan { get; }
    public HotkeyField MapSwipe { get; }
    public HotkeyField MapFinalScan { get; }
    public HotkeyField MapStartRun { get; }
    public HotkeyField MapEndRun { get; }
    public IReadOnlyList<HotkeyField> Hotkeys { get; }
    public IReadOnlyList<HotkeyField> SessionHotkeys { get; }
    public IReadOnlyList<HotkeyField> SweepHotkeys { get; }
    public IReadOnlyList<HotkeyField> MappingHotkeys { get; }
    public AreasField Areas { get; }
    public ChoiceField Placement { get; }
    public NumberField FeedbackSweep { get; }
    public NumberField FeedbackResult { get; }
    public ChoiceField PriceUnit { get; }
    public LootGridField LootGrid { get; }

    public SettingsPage General { get; }
    public SettingsPage Capture { get; }
    public SettingsPage HotkeysPage { get; }
    public SettingsPage Sweeps { get; }
    public SettingsPage Feedback { get; }
    public SettingsPage Mapping { get; }
    public IReadOnlyList<SettingsPage> Pages { get; }

    public string DataDirectory => _env.DataDirectory;
    public string LogsDirectory => Path.Combine(_env.DataDirectory, "logs");

    /// <summary>Shown on the first page opened when settings.json could not be read.</summary>
    public bool ShowUnreadableBanner => _env.SettingsUnreadable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterNote), nameof(FooterMessage))]
    public partial SettingsPage? SelectedPage { get; set; }

    partial void OnSelectedPageChanged(SettingsPage? oldValue, SettingsPage? newValue)
    {
        oldValue?.IsSelected = false;
        newValue?.IsSelected = true;
    }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>The categories matching the search ("Search settings"); all of them when it is empty.</summary>
    public ObservableCollection<SettingsPage> VisiblePages { get; } = [];

    partial void OnSearchTextChanged(string value)
    {
        var query = value.Trim();
        var selected = SelectedPage;
        VisiblePages.Clear();
        foreach (var page in Pages.Where(p => query.Length == 0 || p.Matches(query)))
            VisiblePages.Add(page);
        // The first match opens; with no match the current page stays.
        SelectedPage = query.Length > 0 ? VisiblePages.FirstOrDefault() ?? selected : selected;
    }

    // --- counts, footer -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirtyText), nameof(IsDirty), nameof(CancelText))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial int ChangeCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterMessage), nameof(FooterIsError), nameof(HasProblems))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial int ProblemCount { get; set; }

    /// <summary>Set when Save failed; cleared by the next edit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterMessage), nameof(FooterIsError))]
    public partial string? ApplyError { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; set; }

    public bool IsDirty => ChangeCount > 0;
    public bool HasProblems => ProblemCount > 0;
    public string DirtyText => ChangeCount switch { 0 => "No unsaved changes", 1 => "1 unsaved change", var n => $"{n} unsaved changes" };
    public string CancelText => IsDirty ? "Cancel" : "Close";
    public string FooterNote => SelectedPage?.FooterNote ?? "";

    public string FooterMessage => ApplyError
                                   ?? (ProblemCount switch { 0 => FooterNote, 1 => "1 problem to fix before saving", var n => $"{n} problems to fix before saving" });

    public bool FooterIsError => ApplyError is not null || ProblemCount > 0;

    private void OnEdited(SettingField field)
    {
        ApplyError = null;
        if (field == Theme)
            PreviewTheme?.Invoke((AppTheme)Theme.Value);
        Revalidate();
    }

    private void OnRecordingChanged(HotkeyField field, bool recording)
    {
        if (recording)
        {
            foreach (var other in Hotkeys.Where(h => h != field))
                other.CancelRecording();
        }
        SuspendHotkeys?.Invoke(AnyRecording);
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Recomputes the cross-field hotkey messages, the per-page counts and the totals.</summary>
    private void Revalidate()
    {
        ValidateHotkeys();
        foreach (var page in Pages)
            page.Refresh();
        ChangeCount = _fields.Count(f => f.IsChanged);
        ProblemCount = Pages.Sum(p => p.ErrorCount);
    }

    private void ValidateHotkeys()
    {
        foreach (var h in Hotkeys)
        {
            h.Note = null;
            h.Warning = null;
            h.Error = h.Parsed switch
            {
                null => "Error: not a valid hotkey (for example Ctrl+Shift+Z).",
                var k when k == new Hotkey(KeyModifiers.Ctrl, 'C') => "Error: Ctrl+C is reserved for item capture.",
                _ => null,
            };
        }

        // Of two equal hotkeys, the one edited last carries the error, the other a note.
        foreach (var group in Hotkeys.Where(h => h.Error is null).GroupBy(h => h.Parsed).Where(g => g.Count() > 1))
        {
            var latest = group.MaxBy(h => h.EditStamp)!;
            foreach (var other in group.Where(h => h != latest))
            {
                latest.Error ??= $"Error: already used by {other.Label}. Hotkeys must be different from each other.";
                other.Note = $"Note: {latest.Label} is set to the same combination.";
            }
        }

        foreach (var h in Hotkeys.Where(h => h.Error is null && h.Parsed is not null))
        {
            var k = h.Parsed!;
            if (!h.IsChanged && _env.FailedHotkeys.Contains(h.Action))
                h.Warning = "Warning: already in use by another app. Change it here.";
            else if (k.Modifiers == KeyModifiers.None)
                h.Warning = $"Warning: {k} has no modifier. While registered, the key no longer reaches the game or any other app.";
        }
    }

    // --- commands -------------------------------------------------------------------------------

    [RelayCommand]
    private void ResetPage()
    {
        foreach (var field in SelectedPage?.Fields ?? [])
            field.Reset();
    }

    /// <summary>Footer message click: opens the first page with a problem.</summary>
    [RelayCommand]
    private void JumpToFirstError()
    {
        if (Pages.FirstOrDefault(p => p.ErrorCount > 0) is { } page)
        {
            SearchText = "";
            SelectedPage = page;
        }
    }

    [RelayCommand]
    private void OpenLogsFolder() => OpenLogs?.Invoke();

    private bool CanSave() => IsDirty && ProblemCount == 0 && !IsSaving && !AnyRecording;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (TryBuild() is not { } settings || Apply is null)
            return;
        CancelAllRecording();
        IsSaving = true;
        try
        {
            ApplyError = await Apply(settings) is { } error ? $"Applying settings failed: {error}" : null;
        }
        finally
        {
            IsSaving = false;
        }
        if (ApplyError is null)
            Saved?.Invoke();
    }

    /// <summary>Cancel / close: stops recording and puts the saved theme back.</summary>
    public void Discard()
    {
        CancelAllRecording();
        if (!Theme.Value.Equals(_saved.Theme))
            PreviewTheme?.Invoke(_saved.Theme);
    }

    public bool AnyRecording => Hotkeys.Any(h => h.IsRecording);

    public void CancelAllRecording()
    {
        foreach (var h in Hotkeys)
            h.CancelRecording();
    }

    /// <summary>The edited settings, or null while anything is invalid.</summary>
    public AppSettings? TryBuild()
    {
        if (ProblemCount > 0)
            return null;
        var settings = _saved with
        {
            Theme = (AppTheme)Theme.Value,
            LogLevel = (Microsoft.Extensions.Logging.LogLevel)LogLevel.Value,
            GameWindowTitle = GameWindowTitle.Value.Trim(),
            ClipboardTimeoutMs = ClipboardWait.Value!.Value,
            PositionTolerance = PositionTolerance.Value!.Value,
            HotkeyNewSession = NewSession.Parsed!.ToString(),
            HotkeyUndo = Undo.Parsed!.ToString(),
            HotkeyRedo = Redo.Parsed!.ToString(),
            HotkeySweepStart = SweepStart.Parsed!.ToString(),
            HotkeySweepFinish = SweepFinish.Parsed!.ToString(),
            HotkeySkipCell = SkipCell.Parsed!.ToString(),
            HotkeyMapInitialScan = MapInitialScan.Parsed!.ToString(),
            HotkeyMapStashScan = MapStashScan.Parsed!.ToString(),
            HotkeyMapSwipe = MapSwipe.Parsed!.ToString(),
            HotkeyMapFinalScan = MapFinalScan.Parsed!.ToString(),
            HotkeyMapStartRun = MapStartRun.Parsed!.ToString(),
            HotkeyMapEndRun = MapEndRun.Parsed!.ToString(),
            GridKinds = Areas.Text,
            OverlayPlacement = (OverlayPlacement)Placement.Value,
            FeedbackSweepMs = FeedbackSweep.Value!.Value,
            FeedbackResultMs = FeedbackResult.Value!.Value,
            LootGrid = LootGrid.IsChanged ? LootGrid.Value : _saved.LootGrid,
            PriceUnit = (Unit)PriceUnit.Value,
        };
        return settings.Validate().Count == 0 ? settings : null;
    }
}

/// <summary>One category of the Settings dialog: its fields, unsaved and error state for the category list.</summary>
public sealed partial class SettingsPage : ObservableObject
{
    private readonly string _keywords;

    public SettingsPage(string key, string title, string subtitle, string footerNote, IReadOnlyList<SettingField> fields,
        string extraKeywords = "", bool isSoon = false)
    {
        Key = key;
        Title = title;
        Subtitle = subtitle;
        FooterNote = footerNote;
        Fields = fields;
        IsSoon = isSoon;
        _keywords = string.Join(' ', [title, subtitle, extraKeywords, .. fields.SelectMany(f => new[] { f.Label, f.Description })]);
    }

    public string Key { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string FooterNote { get; }
    public IReadOnlyList<SettingField> Fields { get; }
    public bool IsSoon { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrors), nameof(ErrorText))]
    public partial int ErrorCount { get; set; }

    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    public bool HasErrors => ErrorCount > 0;
    public string ErrorText => ErrorCount == 1 ? "1 error" : $"{ErrorCount} errors";

    public bool Matches(string query) => _keywords.Contains(query, StringComparison.OrdinalIgnoreCase);

    internal void Refresh()
    {
        ErrorCount = Fields.Sum(f => f is AreasField a ? a.ProblemCount : f.HasError ? 1 : 0);
        IsDirty = Fields.Any(f => f.IsChanged);
    }
}
