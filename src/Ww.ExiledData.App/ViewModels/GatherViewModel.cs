using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Ww.ExiledData.App.Services;
using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.App.ViewModels;

/// <summary>
/// The Gather tab of a mapping session: the run card with setup, scan list and actions;
/// the loot panel (condensed grid or list, this run or the whole session); the run history. Built from a copy of the
/// session's mapping data (<see cref="Update"/>); every change goes through <see cref="MappingService"/>, whose
/// Changed event makes the window load a new copy.
/// </summary>
public sealed partial class GatherViewModel : ObservableObject
{
    private readonly MappingService _mapping;
    private CurrencyBook _book;
    private readonly ILogger _log;
    private MapSession _session;
    private bool _recording;
    private ActiveSweepInfo? _sweep;
    private DateTimeOffset _now;
    /// <summary>The run picked in the history; null follows the current (or last) run.</summary>
    private Guid? _pickedRunId;

    public GatherViewModel(MappingService mapping, Guid sessionId, CurrencyBook book, ILogger logger)
    {
        _mapping = mapping;
        _book = book;
        _log = logger;
        _session = new MapSession(sessionId);
        SessionId = sessionId;

        BuildCells();
    }

    /// <summary>The settings changed the codes or the loot grid: rebuilds the grid and refills it.</summary>
    public void SetBook(CurrencyBook book)
    {
        _book = book;
        BuildCells();
        BuildLoot();
    }

    private void BuildCells()
    {
        LootRows = CurrencyCellViewModel.RowsOf(_book);
        _cells = LootRows.SelectMany(r => r.Cells).Where(c => !c.IsPlaceholder).ToList();
    }

    private IReadOnlyList<CurrencyCellViewModel> _cells = [];

    // Supplied by the window.
    /// <summary>(title, suggestions) → the typed tag, or null when canceled.</summary>
    public Func<string, IReadOnlyList<string>, Task<string?>>? AskTag { get; set; }
    /// <summary>(title, initial text) → the note, or null when canceled.</summary>
    public Func<string, string, Task<string?>>? AskNote { get; set; }
    /// <summary>Shows the recall dialog; true when the user assigned.</summary>
    public Func<RecallDialogViewModel, Task<bool>>? AskRecall { get; set; }
    /// <summary>Shows the Final-vs-Initial review; the user's choice, or null when canceled.</summary>
    public Func<ReviewDialogViewModel, Task<ReviewChoice?>>? AskReview { get; set; }
    /// <summary>Starts a scan of the given kind (a sweep of the inventory).</summary>
    public Func<ScanKind, Task>? StartScan { get; set; }
    public Func<Task>? FinishScan { get; set; }
    public Func<Task>? CancelScan { get; set; }
    public Action<string>? ReportError { get; set; }
    /// <summary>(message, confirm button) → true when the user confirmed.</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    public Guid SessionId { get; }

    // --- run card ---------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRuns))]
    public partial bool HasRun { get; set; }

    public bool HasNoRuns => !HasRun;

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "";

    [ObservableProperty]
    public partial string RunNumberText { get; set; } = "";

    /// <summary>"of 24": the number of runs in the session so far.</summary>
    [ObservableProperty]
    public partial string RunOfText { get; set; } = "";

    [ObservableProperty]
    public partial string RunStateText { get; set; } = "";

    /// <summary>The shown run is the open run of the recording session.</summary>
    [ObservableProperty]
    public partial bool IsRunRunning { get; set; }

    [ObservableProperty]
    public partial string RunTimerText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoWaystone))]
    public partial bool HasWaystone { get; set; }

    public bool HasNoWaystone => !HasWaystone;

    [ObservableProperty]
    public partial string WaystoneText { get; set; } = "";

    [ObservableProperty]
    public partial string WaystoneSubText { get; set; } = "";

    [ObservableProperty]
    public partial string TabletCountText { get; set; } = "0 / 4";

    public ObservableCollection<TabletSlotViewModel> Tablets { get; } = [];
    public ObservableCollection<ScanRowViewModel> ScanRows { get; } = [];

    [ObservableProperty]
    public partial bool HasNoScans { get; set; } = true;
    public ObservableCollection<TagChipViewModel> RunTags { get; } = [];
    public ObservableCollection<TagChipViewModel> SessionTags { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote), nameof(NoteButtonText))]
    public partial string NoteText { get; set; } = "";

    public bool HasNote => NoteText.Length > 0;
    public string NoteButtonText => HasNote ? "✎ Edit note" : "✎ Note";

    /// <summary>The run card shows a run from the history, not the current one.</summary>
    [ObservableProperty]
    public partial bool IsPickedRun { get; set; }

    /// <summary>The shown run's number (for the captured items strip: "RUN 24 ITEMS"); null when there is none.</summary>
    public int? ShownRunNumber => ShownRun()?.Number;

    /// <summary>The logged captures of the shown run (the captured items panel's default scope).</summary>
    public IReadOnlySet<Guid> ShownRunCaptures => ShownRun()?.CaptureIds.ToHashSet() ?? [];

    /// <summary>Raised when the shown run changes (another run picked, or a new current run).</summary>
    public event Action? ShownRunChanged;

    // Summary of the shown run's running or last scan (MAPPING_PLAN.md 3.3: where a forgotten cell is noticed).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    public partial string SummaryTitle { get; set; } = "";

    public bool HasSummary => SummaryTitle.Length > 0;
    public ObservableCollection<ScanDeltaRow> SummaryDeltas { get; } = [];
    public ObservableCollection<string> SummaryWarnings { get; } = [];

    // --- recording state --------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScanNow))]
    [NotifyCanExecuteChangedFor(nameof(StashScanCommand), nameof(SwipeCommand), nameof(FinalScanCommand), nameof(StartNewRunCommand),
        nameof(InitialScanCommand))]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScanNow), nameof(IsNotScanning))]
    [NotifyCanExecuteChangedFor(nameof(StashScanCommand), nameof(SwipeCommand), nameof(FinalScanCommand), nameof(StartNewRunCommand),
        nameof(InitialScanCommand), nameof(FinishScanNowCommand), nameof(CancelScanNowCommand))]
    public partial bool IsScanning { get; set; }

    public bool IsNotScanning => !IsScanning;
    public bool CanScanNow => IsRecording && !IsScanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    public partial bool CanUndo { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    public partial bool CanRedo { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EndRunCommand))]
    public partial bool CanEndRun { get; set; }

    /// <summary>The run a scan would go to has no Initial yet (or no run is open: a swipe starts one).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InitialScanCommand))]
    public partial bool NeedsInitial { get; set; }

    /// <summary>The shown run has a run before it whose tablets can carry over.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UsePreviousTabletsCommand))]
    public partial bool CanUsePreviousTablets { get; set; }

    // --- loot panel -------------------------------------------------------------------------------

    /// <summary>The condensed grid: rows of nine cells in three groups; a placeholder is an empty place.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLootGrid), nameof(HasNoLootGrid))]
    public partial IReadOnlyList<LootRowViewModel> LootRows { get; private set; } = [];

    public bool HasLootGrid => LootRows.Count > 0;
    public bool HasNoLootGrid => !HasLootGrid;
    public ObservableCollection<LootListRow> LootList { get; } = [];

    /// <summary>The list's footer: every currency's count and the value of those with a price.</summary>
    [ObservableProperty]
    public partial LootListRow? LootTotal { get; private set; }

    [ObservableProperty]
    public partial string LootTotalNote { get; private set; } = "";

    /// <summary>The value column's header: the unit of the settings, or VALUE when each row names its own.</summary>
    [ObservableProperty]
    public partial string ValueHeader { get; private set; } = "VALUE";

    /// <summary>The loot card shows currencies (grid or list) or the captured items (list only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowItems), nameof(ShowCurrencyGrid), nameof(ShowCurrencyList), nameof(ShowOutsideGridNote), nameof(ShowNoLoot))]
    public partial bool ShowCurrencies { get; set; } = true;

    public bool ShowItems { get => !ShowCurrencies; set => ShowCurrencies = !value; }

    /// <summary>The captured items of the loot scope (shown run or session); filled by the session window.</summary>
    public ObservableCollection<ItemRow> LootItems { get; } = [];

    public void SetItems(IEnumerable<ItemRow> items)
    {
        LootItems.Clear();
        foreach (var item in items)
            LootItems.Add(item);
        OnPropertyChanged(nameof(ShowNoItems));
    }

    public bool ShowNoItems => ShowItems && LootItems.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList), nameof(ShowOutsideGridNote), nameof(ShowCurrencyGrid), nameof(ShowCurrencyList))]
    public partial bool ShowGrid { get; set; } = true;

    public bool ShowCurrencyGrid => ShowCurrencies && ShowGrid;
    public bool ShowCurrencyList => ShowCurrencies && !ShowGrid;

    public bool ShowList { get => !ShowGrid; set => ShowGrid = !value; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LootOfSession))]
    public partial bool LootOfRun { get; set; } = true;

    public bool LootOfSession { get => !LootOfRun; set => LootOfRun = !value; }

    partial void OnLootOfRunChanged(bool value) => BuildLoot();

    partial void OnShowCurrenciesChanged(bool value) => OnPropertyChanged(nameof(ShowNoItems));

    [ObservableProperty]
    public partial string LootCaption { get; set; } = "";

    /// <summary>Currencies that dropped but have no place in the grid; the list view shows them all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutsideGrid), nameof(ShowOutsideGridNote))]
    public partial string OutsideGridText { get; set; } = "";

    public bool HasOutsideGrid => OutsideGridText.Length > 0;

    /// <summary>The note points to the List, so the List itself does not show it.</summary>
    public bool ShowOutsideGridNote => HasOutsideGrid && ShowCurrencyGrid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoLoot), nameof(ShowNoLoot))]
    public partial bool HasLoot { get; set; }

    public bool HasNoLoot => !HasLoot;
    public bool ShowNoLoot => ShowCurrencies && HasNoLoot;

    // --- run history ------------------------------------------------------------------------------

    public ObservableCollection<RunRowViewModel> Runs { get; } = [];

    [ObservableProperty]
    public partial bool HasNoHistory { get; set; } = true;

    [ObservableProperty]
    public partial RunRowViewModel? SelectedRun { get; set; }

    private bool _building;

    partial void OnSelectedRunChanged(RunRowViewModel? value)
    {
        if (_building)
            return;
        _pickedRunId = value is null || value.IsCurrent ? null : value.Id;
        BuildRun();
        BuildLoot();
        ShownRunChanged?.Invoke();
    }

    // --- building ---------------------------------------------------------------------------------

    /// <summary>Shows a new copy of the session's mapping data.</summary>
    public void Update(MapSession session, bool recording, ActiveSweepInfo? sweep, (bool CanUndo, bool CanRedo) undo, DateTimeOffset now)
    {
        _session = session;
        _recording = recording;
        _sweep = recording ? sweep : null;
        _now = now;
        IsRecording = recording;
        IsScanning = _sweep is not null;
        (CanUndo, CanRedo) = undo;
        NeedsInitial = session.CurrentRun is not { } current || session.InitialOf(current) is null;
        if (_pickedRunId is { } picked && session.FindRun(picked) is null)
            _pickedRunId = null;

        BuildTags(SessionTags, session.SessionId);
        BuildHistory();
        BuildRun();
        BuildLoot();
        ShownRunChanged?.Invoke();
    }

    /// <summary>Clock tick: the running run's timer.</summary>
    public void Tick(DateTimeOffset now)
    {
        _now = now;
        if (ShownRun() is { } run)
            RunTimerText = Format.Duration(EndOf(run) - run.StartedAt);
    }

    private MapRun? ShownRun() =>
        (_pickedRunId is { } id ? _session.FindRun(id) : null) ?? _session.CurrentRun ?? _session.Runs.LastOrDefault();

    private DateTimeOffset EndOf(MapRun run) => _session.EndOf(run, _recording, _now);

    private void BuildHistory()
    {
        _building = true;
        try
        {
            Runs.Clear();
            var current = _session.CurrentRun ?? _session.Runs.LastOrDefault();
            foreach (var run in Enumerable.Reverse(_session.Runs))
            {
                var loot = Loot.Of(_session, run);
                var top = string.Join(" · ", loot.Total.Take(3).Select(c => $"{CodeOf(c.Name)} {c.Count}"));
                var tags = string.Join(", ", _session.TagsOf(run.Id));
                if (run == _session.CurrentRun && _recording)
                    tags = tags.Length == 0 ? "running" : $"running, {tags}";
                var (scans, warning) = ScansText(run);
                Runs.Add(new RunRowViewModel(run.Id, run.Number, Format.Duration(EndOf(run) - run.StartedAt), run.SetupText,
                    scans, warning, top, tags, run == current));
            }
            HasNoHistory = Runs.Count == 0;
            var shown = ShownRun();
            SelectedRun = Runs.FirstOrDefault(r => r.Id == shown?.Id);
        }
        finally
        {
            _building = false;
        }
    }

    /// <summary>"4 scans · Final pending" (or "no Final" once ended) / "6 scans · ⚠ Final differs · Review" / "4 scans · Final matches".</summary>
    private (string Text, bool Warning) ScansText(MapRun run)
    {
        var count = _session.ScansOf(run).Count(s => !s.IsDiscarded);
        var scans = count == 1 ? "1 scan" : $"{count} scans";
        return Loot.CheckOf(_session, run) switch
        {
            null when run.IsOpen => ($"{scans} · Final pending", false),
            null => ($"{scans} · no Final", false),
            { HasWarning: true } => ($"{scans} · ⚠ Final differs · Review", true),
            { Final.CheckDismissed: true } => ($"{scans} · Final dismissed", false),
            { Differences.Count: > 0 } => ($"{scans} · Final adjusted", false),
            _ => ($"{scans} · Final matches", false),
        };
    }

    private void BuildRun()
    {
        var run = ShownRun();
        HasRun = run is not null;
        IsPickedRun = _pickedRunId is not null;
        EmptyText = _recording
            ? "Copy a waystone, take a swipe or press Start new run to start run 1"
            : "No runs recorded in this session.";
        Tablets.Clear();
        ScanRows.Clear();
        RunTags.Clear();
        SummaryDeltas.Clear();
        SummaryWarnings.Clear();
        SummaryTitle = "";
        CanEndRun = run is { IsOpen: true } && run == _session.CurrentRun;
        CanUsePreviousTablets = run is not null && _session.PreviousRun(run) is { } previous && previous.Tablets.Any();
        if (run is null)
        {
            NoteText = "";
            HasNoScans = true;
            return;
        }

        RunNumberText = run.Number.ToString(CultureInfo.InvariantCulture);
        RunOfText = $"OF {_session.Runs.Count.ToString(CultureInfo.InvariantCulture)}";
        IsRunRunning = _recording && run == _session.CurrentRun;
        RunStateText = IsRunRunning ? "Running" : run.IsOpen ? "Open" : "Ended";
        RunTimerText = Format.Duration(EndOf(run) - run.StartedAt);

        HasWaystone = run.Waystone is not null;
        if (run.Waystone is { } w)
        {
            WaystoneText = w.Label;
            var rarity = Core.Text.ItemText.FindValue(w.RawText.Split('\n'), Core.Text.ItemText.RarityPrefix);
            var source = w.RecalledFromRun is { } from ? $"scanned in run {from}" : "raw text stored";
            WaystoneSubText = $"{(rarity is null ? "" : rarity + " · ")}{source} · mods parsed later";
        }
        else
        {
            WaystoneText = "No waystone";
            WaystoneSubText = "Copy it, or pick one from earlier scans";
        }

        var tablets = run.Tablets.ToList();
        TabletCountText = $"{tablets.Count} / {MapSession.MaxTablets}";
        foreach (var t in tablets.Take(MapSession.MaxTablets))
            Tablets.Add(new TabletSlotViewModel(t, SetSetupKind, id => RemoveSetupItem(run.Id, id), pick: null));
        for (var i = tablets.Count; i < MapSession.MaxTablets; i++)
            Tablets.Add(new TabletSlotViewModel(null, null, null, () => Recall(tablets: true)));

        BuildTags(RunTags, run.Id);
        NoteText = _session.NoteOf(run.Id) ?? "";
        BuildScanRows(run);
        BuildSummary(run);
    }

    private void BuildScanRows(MapRun run)
    {
        var check = Loot.CheckOf(_session, run);
        var stashNumber = 0;
        foreach (var scan in _session.ScansOf(run).Where(s => !s.IsDiscarded))
        {
            var name = _session.NameOf(scan);
            if (scan.Kind == ScanKind.Stash)
                name = $"Stash {++stashNumber}";
            var active = scan.Status == ScanStatus.Active;
            var currencies = scan.Totals().Count;
            var summary = scan.Kind switch
            {
                _ when active => $"scanning · {Stacks(scan)}",
                ScanKind.Stash => currencies == 1 ? "+1 currency" : $"+{currencies} currencies",
                ScanKind.Final when check?.Final == scan => check.HasWarning ? "differs"
                    : check.Final.CheckDismissed ? "differences dismissed"
                    : check.Differences.Count > 0 ? "adjusted" : "matches Initial",
                ScanKind.Final => "not used (a later Final)",
                _ => "not counted",
            };
            if (scan.Status == ScanStatus.Interrupted)
                summary += " · interrupted";
            var warning = check?.Final == scan && check.HasWarning;
            ScanRows.Add(new ScanRowViewModel(scan.Id, scan.Kind, name, Format.Duration(scan.StartedAt - run.StartedAt), summary, active,
                scan.Status == ScanStatus.Interrupted, warning, active ? null : SetKind,
                scan.Kind == ScanKind.Final && check?.Final == scan && check.Differences.Count > 0 ? Review : null,
                active ? null : RemoveScan));
        }
        HasNoScans = ScanRows.Count == 0;
    }

    private void BuildSummary(MapRun run)
    {
        var scan = _session.ScansOf(run).LastOrDefault(s => !s.IsDiscarded);
        if (scan is null)
            return;
        var summary = Loot.Summarize(_session, scan);
        var name = ScanRows.FirstOrDefault(r => r.ScanId == scan.Id)?.Name ?? summary.Name;
        var state = scan.Status == ScanStatus.Active ? "scanning" : Stacks(scan);
        var meaning = scan.Kind switch
        {
            ScanKind.Stash => scan.Status == ScanStatus.Active ? "will be added to the loot" : "added to the loot",
            ScanKind.Final => "compared with the Initial",
            _ => "not counted",
        };
        SummaryTitle = $"Last scan · {name} · {state} · {meaning}";
        IEnumerable<(string Name, int Value, bool Difference)> rows = summary.Check is { } check && scan.Status != ScanStatus.Active
            ? check.Differences.Select(d => (d.Name, d.Difference, true))
            : summary.Totals.Select(t => (t.Name, t.Count, false));
        foreach (var (currency, value, difference) in rows.Take(8))
        {
            var display = _book.Describe(currency);
            SummaryDeltas.Add(new ScanDeltaRow(display.Code, display.Badge, currency, value, difference));
        }
        foreach (var warning in summary.Warnings)
            SummaryWarnings.Add(warning);
    }

    private static string Stacks(Scan scan)
    {
        var stacks = scan.Cells.Values.Count(c => c.Stack is not null);
        return stacks == 1 ? "1 stack" : $"{stacks} stacks";
    }

    private void BuildLoot()
    {
        var run = ShownRun();
        IReadOnlyList<CurrencyStack> loot = LootOfRun
            ? run is null ? [] : Loot.Of(_session, run).Total
            : Loot.OfSession(_session);
        var adjustments = LootOfRun ? run?.Adjustments.Count ?? 0 : _session.Runs.Sum(r => r.Adjustments.Count);
        var adjusted = adjustments switch { 0 => "", 1 => " + 1 adjustment", _ => $" + {adjustments} adjustments" };
        LootCaption = LootOfRun
            ? run is null ? "No run yet" : $"Run {run.Number} · from stash scans{adjusted}"
            : $"Session · {_session.Runs.Count} run(s) · from stash scans{adjusted}";

        // Variants with one name (different max stack) count as one currency here.
        var byName = loot.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));
        foreach (var cell in _cells)
            cell.Count = byName.GetValueOrDefault(cell.Name!);

        var inGrid = _book.Layout.Names.ToHashSet();
        LootList.Clear();
        foreach (var (name, count) in byName.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            var display = _book.Describe(name);
            LootList.Add(new LootListRow(display.Code, display.Badge, name, count, inGrid.Contains(name), display.Image, _book.ValueOf(name, count), _book.Unit));
        }
        HasLoot = LootList.Count > 0;
        var priced = LootList.Where(r => r.Value is not null).ToList();
        var total = priced.Aggregate(CurrencyValue.Zero, (sum, r) => sum + r.Value!.Value);
        LootTotal = new LootListRow("", null, "Total", LootList.Sum(r => r.Count), true, Value: priced.Count > 0 ? total : null, Unit: _book.Unit);
        ValueHeader = _book.Unit switch
        {
            PriceUnit.Divine => "DIV",
            PriceUnit.Exalted => "EX",
            PriceUnit.Chaos => "CHAOS",
            _ => "VALUE",
        };
        var unpriced = LootList.Count - priced.Count;
        LootTotalNote = !_book.Prices.HasPrices ? "No poe.ninja prices yet."
            : unpriced == 0 ? $"poe.ninja, {_book.Prices.League}"
            : $"poe.ninja, {_book.Prices.League}; {unpriced} without a price";
        var outside = LootList.Count(r => !r.InGrid);
        OutsideGridText = outside == 0 ? "" : $"{outside} more currenc{(outside == 1 ? "y" : "ies")} not in the grid: see List";
    }

    private void BuildTags(ObservableCollection<TagChipViewModel> target, Guid ownerId)
    {
        target.Clear();
        foreach (var tag in _session.TagsOf(ownerId))
            target.Add(new TagChipViewModel(tag, t => Do("Removing the tag", () => _mapping.RemoveTag(SessionId, ownerId, t))));
    }

    private string CodeOf(string name) => _book.Describe(name) is var d && d.Badge is { } badge ? $"{d.Code}/{badge}" : d.Code;

    // --- commands ---------------------------------------------------------------------------------

    private Task SetKind(Guid scanId, ScanKind kind) => Do("Changing the scan kind", () => _mapping.SetScanKind(SessionId, scanId, kind));

    /// <summary>Removes an ended scan. A stash scan or a later swipe goes at once; an Initial or a Final asks first.</summary>
    private async Task RemoveScan(Guid scanId)
    {
        if (_session.FindScan(scanId) is not { } scan || _session.FindRun(scan.RunId) is not { } run)
            return;
        if (RemovalQuestion(run, scan) is { } question && (Confirm is null || !await Confirm(question, "Remove")))
            return;
        await Do("Removing the scan", () => _mapping.RemoveScan(SessionId, scanId));
    }

    /// <summary>What removing an Initial or a Final changes in the run's check; null for a scan that removes freely.</summary>
    private string? RemovalQuestion(MapRun run, Scan scan)
    {
        if (scan.Kind == ScanKind.Final)
        {
            var check = _session.FinalOf(run) != scan ? ""
                : _session.ScansOf(run).Any(s => s != scan && s.Kind == ScanKind.Final && s.Counts) ? " The check uses the earlier Final."
                : " The run has no check any more.";
            var adjustments = run.Adjustments.Count(a => a.SourceScanId == scan.Id) switch
            {
                0 => "",
                1 => " Its applied adjustment is removed too.",
                var n => $" Its {n} applied adjustments are removed too.",
            };
            return $"Remove the Final scan?{check}{adjustments} Undo brings it back.";
        }
        if (_session.InitialOf(run) == scan)
        {
            var next = _session.ScansOf(run).Any(s => s != scan && s.Kind == ScanKind.Swipe && !s.IsDiscarded)
                ? "The next swipe becomes the Initial"
                : "With no Initial, a Final is compared with an empty inventory";
            return $"Remove the Initial swipe? {next}; adjustments already applied stay. Undo brings it back.";
        }
        return null;
    }

    private Task SetSetupKind(Guid itemId, SetupItemKind kind) => Do("Correcting the setup", () => _mapping.SetSetupItemKind(SessionId, itemId, kind));

    private Task RemoveSetupItem(Guid runId, Guid itemId) => Do("Removing the tablet", () => _mapping.RemoveSetupItem(SessionId, runId, itemId));

    private bool CanScanInitial() => CanScanNow && NeedsInitial;

    /// <summary>The run's first swipe is its Initial: the same scan as a swipe, offered while the run has none.</summary>
    [RelayCommand(CanExecute = nameof(CanScanInitial))]
    private Task InitialScan() => ScanAs(ScanKind.Swipe);

    [RelayCommand(CanExecute = nameof(CanScanNow))]
    private Task StashScan() => ScanAs(ScanKind.Stash);

    [RelayCommand(CanExecute = nameof(CanScanNow))]
    private Task Swipe() => ScanAs(ScanKind.Swipe);

    [RelayCommand(CanExecute = nameof(CanScanNow))]
    private Task FinalScan() => ScanAs(ScanKind.Final);

    private Task ScanAs(ScanKind kind) => Do("Starting a scan", () => StartScan?.Invoke(kind) ?? Task.CompletedTask);

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private Task FinishScanNow() => Do("Finishing the scan", () => FinishScan?.Invoke() ?? Task.CompletedTask);

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private Task CancelScanNow() => Do("Canceling the scan", () => CancelScan?.Invoke() ?? Task.CompletedTask);

    [RelayCommand(CanExecute = nameof(CanScanNow))]
    private Task StartNewRun() => Do("Starting a new run", async () =>
    {
        _pickedRunId = null;
        await _mapping.StartNewRun(SessionId);
    });

    [RelayCommand(CanExecute = nameof(CanEndRun))]
    private Task EndRun() => Do("Ending the run", () => _mapping.EndRun(SessionId));

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task Undo() => Do("Undo", () => _mapping.Undo(SessionId));

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private Task Redo() => Do("Redo", () => _mapping.Redo(SessionId));

    [RelayCommand(CanExecute = nameof(CanUsePreviousTablets))]
    private Task UsePreviousTablets() => ShownRun() is { } run
        ? Do("Using the previous run's tablets", () => _mapping.UsePreviousTablets(SessionId, run.Id))
        : Task.CompletedTask;

    /// <summary>The recall dialog: the shown run's waystone and tablets from earlier scans.</summary>
    [RelayCommand]
    private Task ChangeWaystone() => Recall(tablets: false);

    [RelayCommand]
    private Task ChangeTablets() => Recall(tablets: true);

    private async Task Recall(bool tablets)
    {
        if (AskRecall is null || ShownRun() is not { } run)
            return;
        var previous = _session.PreviousRun(run)?.Tablets.Select(t => t.RawText).ToList() ?? [];
        var dialog = new RecallDialogViewModel(run.Number, _session.RecallWaystones(), _session.RecallTablets(), run.Waystone?.RawText,
            run.Tablets.Select(t => t.RawText).ToList(), previous, startOnTablets: tablets);
        if (await AskRecall(dialog))
            await Do("Setting up the run", () => _mapping.SetSetup(SessionId, run.Id, dialog.ChosenWaystone, dialog.ChosenTablets));
    }

    /// <summary>The Final-vs-Initial review: apply the (edited) adjustments, or dismiss the warning.</summary>
    private async Task Review(Guid finalScanId)
    {
        if (AskReview is null || ShownRun() is not { } run || Loot.CheckOf(_session, run) is not { } check || check.Final.Id != finalScanId)
            return;
        var dialog = new ReviewDialogViewModel(run.Number, check, name => _book.Describe(name).Code);
        switch (await AskReview(dialog))
        {
            case ReviewChoice.Apply:
                await Do("Applying the adjustment", () => _mapping.ApplyAdjustments(SessionId, finalScanId, dialog.Adjustments()));
                break;
            case ReviewChoice.Dismiss:
                await Do("Dismissing the differences", () => _mapping.DismissCheck(SessionId, finalScanId, true));
                break;
            case ReviewChoice.Restore:
                await Do("Showing the differences again", () => _mapping.DismissCheck(SessionId, finalScanId, false));
                break;
        }
    }

    [RelayCommand]
    private Task AddRunTag() => ShownRun() is { } run ? AddTag(run.Id, $"Tag for run {run.Number}") : Task.CompletedTask;

    [RelayCommand]
    private Task AddSessionTag() => AddTag(SessionId, "Session tag");

    private async Task AddTag(Guid ownerId, string title)
    {
        if (AskTag is null)
            return;
        var known = await _mapping.KnownTags();
        if (await AskTag(title, known.Select(t => t.ToString()).ToList()) is { Length: > 0 } text)
            await Do("Adding the tag", () => _mapping.AddTag(SessionId, ownerId, text));
    }

    [RelayCommand]
    private async Task EditNote()
    {
        if (AskNote is null || ShownRun() is not { } run)
            return;
        if (await AskNote($"Note for run {run.Number}", NoteText) is { } text)
            await Do("Saving the note", () => _mapping.SetNote(SessionId, run.Id, text));
    }

    [RelayCommand]
    private void ShowCurrentRun()
    {
        _pickedRunId = null;
        Update(_session, _recording, _sweep, (CanUndo, CanRedo), _now);
    }

    private async Task Do(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{What} failed", what);
            ReportError?.Invoke($"{what} failed: {ex.Message}");
        }
    }
}
