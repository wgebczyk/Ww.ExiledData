using Microsoft.Extensions.Logging.Abstractions;
using Ww.ExiledData.App.Services;
using Ww.ExiledData.App.ViewModels;
using Ww.ExiledData.Core.Recording;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.Tests;

/// <summary>The Gather tab built from a session's mapping data.</summary>
public sealed class GatherViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ww-gather-").FullName;
    private readonly MappingStore _store;
    private readonly MapFeed _feed = new();
    private readonly GatherViewModel _vm;

    public GatherViewModelTests()
    {
        // Update never touches the service; it only needs one to exist (no sessions table: nothing is saved).
        _store = MappingStore.Open(Path.Combine(_dir, "data.db"));
        var service = new MappingService(_store, new NoStream());
        _vm = new GatherViewModel(service, _feed.Session.SessionId, TestBook, NullLogger.Instance);
    }

    /// <summary>A loot grid with a gap: two rows of nine, Divine Orb after three empty places.</summary>
    internal static readonly CurrencyBook TestBook = CurrencyBook.Default.WithLayout(LootLayout.From([
        ["Orb of Transmutation", "Greater Orb of Transmutation", "Chaos Orb", "Greater Chaos Orb"],
        [null, null, null, "Divine Orb"],
    ]));

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class NoStream : ICaptureStream
    {
        public event Action<ProcessedCapture>? CaptureProcessed { add { } remove { } }
        public event Action<SweepChanged>? SweepChanged { add { } remove { } }
    }

    private void Show(bool recording = true, ActiveSweepInfo? sweep = null) =>
        _vm.Update(_feed.Session, recording, sweep, (false, false), _feed.Now);

    [Fact]
    public void Items_replace_the_currencies_and_have_no_grid()
    {
        TwoRuns();
        Show();
        Assert.True(_vm.ShowCurrencyGrid);

        _vm.ShowItems = true;
        Assert.False(_vm.ShowCurrencyGrid);
        Assert.False(_vm.ShowCurrencyList);
        Assert.False(_vm.ShowOutsideGridNote);
        Assert.True(_vm.ShowNoItems);

        _vm.SetItems([new ItemRow(Guid.NewGuid(), "Strange Carving", 3, 4, "", 0, "12:00", "", "raw")]);
        Assert.False(_vm.ShowNoItems);

        _vm.ShowCurrencies = true;
        Assert.True(_vm.ShowCurrencyGrid); // Grid / List is kept
    }

    [Fact]
    public void A_new_loot_grid_from_the_settings_is_filled_at_once()
    {
        TwoRuns();
        Show();

        _vm.SetBook(TestBook.WithLayout(LootLayout.From([["Exalted Orb"]])));

        Assert.Equal(LootLayout.Columns, Assert.Single(_vm.LootRows).Cells.Count);
        Assert.Equal([3, 3, 3], _vm.LootRows[0].Groups.Select(g => g.Count));
        Assert.Equal(0, Cell("Exalted Orb").Count); // run 2 is shown
        Assert.Equal("3 more currencies not in the grid: see List", _vm.OutsideGridText);

        _vm.SetBook(TestBook.WithLayout(LootLayout.Empty));
        Assert.True(_vm.HasNoLootGrid);
    }

    private CurrencyCellViewModel Cell(string name) =>
        _vm.LootRows.SelectMany(r => r.Cells).Single(c => c.Name == name);

    /// <summary>Two runs: run 1 ended by run 2, run 2 open with an Initial swipe and a stash scan.</summary>
    private void TwoRuns()
    {
        _feed.Copy(MapItems.Waystone(14));
        _feed.Copy(MapItems.Tablet("Ritual Tablet"));
        _feed.Stash(("Chaos Orb", 10), ("Exalted Orb", 1));
        _feed.Copy(MapItems.Waystone(15));
        _feed.Copy(MapItems.Tablet("Breach Tablet"));
        _feed.Copy(MapItems.Unknown("Strange Carving"));
        _feed.Swipe(("Chaos Orb", 3));
        _feed.Stash(("Chaos Orb", 17), ("Orb of Transmutation", 40), ("Orb of Transmutation", 25), ("Greater Chaos Orb", 2));
        _feed.Session.AddTag(_feed.Session.Runs[1].Id, "rituals: 6");
        _feed.Session.AddTag(_feed.Session.SessionId, "farming");
    }

    [Fact]
    public void Without_runs_the_card_invites_a_run()
    {
        Show();

        Assert.False(_vm.HasRun);
        Assert.StartsWith("Copy a waystone, take a swipe or press Start new run", _vm.EmptyText);
        Assert.True(_vm.StartNewRunCommand.CanExecute(null));
        Assert.True(_vm.HasNoHistory);
        Assert.False(_vm.HasLoot);

        Show(recording: false);
        Assert.Equal("No runs recorded in this session.", _vm.EmptyText);
        Assert.False(_vm.StartNewRunCommand.CanExecute(null));
    }

    [Fact]
    public void The_run_card_shows_the_current_run()
    {
        TwoRuns();
        Show();

        Assert.True(_vm.HasRun);
        Assert.Equal(("2", "OF 2", "Running", true), (_vm.RunNumberText, _vm.RunOfText, _vm.RunStateText, _vm.IsRunRunning));
        Assert.Contains("Tier 15", _vm.WaystoneText);
        Assert.StartsWith("Magic · raw text stored", _vm.WaystoneSubText);
        Assert.Equal("2 / 4", _vm.TabletCountText);
        Assert.Equal([false, true, false, false], _vm.Tablets.Select(t => t.IsPossible));
        Assert.Equal([false, false, true, true], _vm.Tablets.Select(t => t.IsEmpty));
        Assert.All(_vm.Tablets.Where(t => t.IsEmpty), t => Assert.NotNull(t.PickCommand));
        Assert.True(_vm.CanUsePreviousTablets);
        Assert.Equal(["rituals: 6"], _vm.RunTags.Select(t => t.Text));
        Assert.Equal(["farming"], _vm.SessionTags.Select(t => t.Text));
        Assert.True(_vm.CanEndRun);
    }

    [Fact]
    public void A_run_started_by_hand_has_no_waystone_yet()
    {
        _feed.Session.StartNewRun(_feed.Now);
        Show();

        Assert.False(_vm.HasWaystone);
        Assert.Equal("No waystone", _vm.WaystoneText);
        Assert.True(_vm.HasNoScans);
    }

    [Fact]
    public void The_scan_list_names_kinds_and_numbers_the_stash_scans()
    {
        TwoRuns();
        _feed.Swipe(("Chaos Orb", 3));
        _feed.Stash(("Chaos Orb", 4));
        Show();

        Assert.Equal(["Initial", "Stash 1", "Swipe", "Stash 2"], _vm.ScanRows.Select(r => r.Name));
        Assert.Equal(["Swipe", "Stash", "Swipe", "Stash"], _vm.ScanRows.Select(r => r.KindText));
        Assert.Equal(["not counted", "+3 currencies", "not counted", "+1 currency"], _vm.ScanRows.Select(r => r.Summary));
        Assert.All(_vm.ScanRows, r => Assert.True(r.CanEditKind));
        Assert.All(_vm.ScanRows, r => Assert.Null(r.ReviewCommand));
        Assert.Equal("4 scans · Final pending", _vm.Runs[0].ScansText);
        Assert.Equal("1 scan · no Final", _vm.Runs[1].ScansText);
    }

    [Fact]
    public void A_final_that_differs_warns_with_a_review_and_the_history_shows_it()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Swipe(("Exalted Orb", 5));
        _feed.Stash(("Exalted Orb", 3));
        _feed.Final(("Exalted Orb", 7));
        Show();

        var final = _vm.ScanRows[^1];
        Assert.Equal(("Final", "differs", true), (final.Name, final.Summary, final.HasWarning));
        Assert.NotNull(final.ReviewCommand);
        Assert.Equal(("3 scans · ⚠ Final differs · Review", true), (_vm.Runs[0].ScansText, _vm.Runs[0].HasWarning));
        Assert.Equal([("EX", "▲ +2")], _vm.SummaryDeltas.Select(d => (d.Code, d.ValueText)));

        // Applying the proposal clears the warning; the loot caption counts the adjustment.
        _feed.Session.ApplyAdjustments(_feed.Session.Scans[^1].Id, [(MapItems.KeyOf("Exalted Orb"), "Exalted Orb", 2)], _feed.Now);
        Show();
        Assert.Equal(("adjusted", false), (_vm.ScanRows[^1].Summary, _vm.ScanRows[^1].HasWarning));
        Assert.Equal("3 scans · Final adjusted", _vm.Runs[0].ScansText);
        Assert.Equal("Run 1 · from stash scans + 1 adjustment", _vm.LootCaption);
    }

    [Fact]
    public async Task Removing_an_initial_or_a_final_asks_first_and_a_stash_scan_does_not()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Swipe(("Exalted Orb", 5));
        _feed.Stash(("Exalted Orb", 3));
        _feed.Final(("Exalted Orb", 7));
        _feed.Session.ApplyAdjustments(_feed.Session.Scans[^1].Id, [(MapItems.KeyOf("Exalted Orb"), "Exalted Orb", 2)], _feed.Now);
        Show();
        var asked = new List<string>();
        _vm.Confirm = (message, _) => { asked.Add(message); return Task.FromResult(false); };
        _vm.ReportError = _ => { }; // the stash removal reaches the service, which has no such session here

        await _vm.ScanRows[0].RemoveCommand!.ExecuteAsync(null);
        await _vm.ScanRows[2].RemoveCommand!.ExecuteAsync(null);
        await _vm.ScanRows[1].RemoveCommand!.ExecuteAsync(null);

        Assert.Equal([
            "Remove the Initial swipe? With no Initial, a Final is compared with an empty inventory; adjustments already applied stay. Undo brings it back.",
            "Remove the Final scan? The run has no check any more. Its applied adjustment is removed too. Undo brings it back.",
        ], asked);
        Assert.All(_feed.Session.Scans, s => Assert.Equal(ScanStatus.Finished, s.Status)); // declined
    }

    [Fact]
    public void A_running_scan_offers_no_remove()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Swipe(("Chaos Orb", 3));
        _feed.StartScan(ScanKind.Stash);
        Show();

        Assert.NotNull(_vm.ScanRows[0].RemoveCommand);
        Assert.Null(_vm.ScanRows[^1].RemoveCommand);
    }

    [Fact]
    public void A_running_scan_shows_as_active_with_its_summary()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Swipe(("Chaos Orb", 3), ("Divine Orb", 1));
        _feed.StartScan(ScanKind.Stash);
        _feed.Stack("Chaos Orb", 10);

        Show(sweep: new ActiveSweepInfo(_feed.SweepId!.Value, "Inventory", new GridKind("Inventory", 12, 5), new GridGeometry(0, 0, 50, 12, 5),
            [], 1, 1));

        Assert.True(_vm.IsScanning);
        Assert.False(_vm.CanScanNow);
        Assert.True(_vm.ScanRows[^1].IsActive);
        Assert.False(_vm.ScanRows[^1].CanEditKind);
        Assert.StartsWith("scanning", _vm.ScanRows[^1].Summary);
        Assert.Contains("will be added to the loot", _vm.SummaryTitle);
        Assert.Equal([("ChO", "10")], _vm.SummaryDeltas.Select(d => (d.Code, d.ValueText)));
    }

    [Fact]
    public void Loot_fills_the_grid_and_the_list()
    {
        TwoRuns();
        Show();

        // Run 2: the stash scan only (the Initial swipe never counts); Transmutation 40 + 25 (two stacks).
        Assert.Equal((17, 65, 2), (Cell("Chaos Orb").Count, Cell("Orb of Transmutation").Count, Cell("Greater Chaos Orb").Count));
        Assert.True(Cell("Divine Orb").IsZero);
        Assert.Equal("", Cell("Divine Orb").CountText);
        Assert.Equal(["TRN", "ChO", "ChO"], _vm.LootList.Select(r => r.Code));
        Assert.Equal([null, null, "G"], _vm.LootList.Select(r => r.Badge));
        Assert.Equal("Run 2 · from stash scans", _vm.LootCaption);
        Assert.False(_vm.HasOutsideGrid);

        _vm.LootOfSession = true;
        Assert.Equal(27, Cell("Chaos Orb").Count);
        Assert.Equal("1 more currency not in the grid: see List", _vm.OutsideGridText); // Exalted Orb from run 1
        Assert.True(_vm.ShowOutsideGridNote);
        _vm.ShowList = true;
        Assert.False(_vm.ShowOutsideGridNote);
        _vm.ShowGrid = true;
        Assert.Contains(_vm.LootList, r => r is { Code: "EX", InGrid: false });
    }

    [Fact]
    public void Grid_cells_stay_the_same_objects_so_a_changed_count_can_be_highlighted()
    {
        TwoRuns();
        Show();
        var cell = Cell("Chaos Orb");

        _feed.Stash(("Chaos Orb", 27));
        Show();

        Assert.Same(cell, Cell("Chaos Orb"));
        Assert.Equal(17 + 27, cell.Count);
    }

    [Fact]
    public void History_lists_runs_newest_first_and_picking_one_shows_it()
    {
        TwoRuns();
        Show();

        Assert.Equal([2, 1], _vm.Runs.Select(r => r.Number));
        Assert.Equal(["T15 + 2 tablets", "T14 + 1 tablet"], _vm.Runs.Select(r => r.SetupText));
        Assert.Equal("TRN 65 · ChO 17 · ChO/G 2", _vm.Runs[0].TopText);
        Assert.Equal("running, rituals: 6", _vm.Runs[0].TagsText);
        Assert.Same(_vm.Runs[0], _vm.SelectedRun);

        _vm.SelectedRun = _vm.Runs[1];

        Assert.Equal(("1", "Ended"), (_vm.RunNumberText, _vm.RunStateText));
        Assert.True(_vm.IsPickedRun);
        Assert.False(_vm.CanEndRun);
        Assert.Equal(10, Cell("Chaos Orb").Count);

        // New data keeps the picked run; Back to the current run returns.
        Show();
        Assert.Equal("1", _vm.RunNumberText);
        _vm.ShowCurrentRunCommand.Execute(null);
        Assert.Equal("2", _vm.RunNumberText);
        Assert.False(_vm.IsPickedRun);
    }

    [Fact]
    public void An_open_run_of_an_ended_session_is_not_running()
    {
        _feed.Copy(MapItems.Waystone(15));
        Show(recording: false);

        Assert.Equal(("Open", false), (_vm.RunStateText, _vm.IsRunRunning));
        Assert.False(_vm.CanScanNow);
        Assert.True(_vm.CanEndRun);
    }
}
