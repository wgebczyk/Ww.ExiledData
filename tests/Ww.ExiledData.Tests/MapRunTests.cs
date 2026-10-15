using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.Tests;

/// <summary>Run boundaries and setup (MAPPING_PLAN.md 3.2, 8).</summary>
public sealed class MapRunTests
{
    private readonly MapFeed _feed = new();
    private MapSession Session => _feed.Session;

    [Fact]
    public void A_waystone_copy_starts_a_run_and_the_next_one_ends_it()
    {
        Assert.Equal(MappingOutcome.RunStarted, _feed.Copy(MapItems.Waystone(15)));
        var first = Session.CurrentRun!;
        _feed.Copy(MapItems.Currency("Chaos Orb", 1)); // a loot check: ends the setup phase

        Assert.Equal(MappingOutcome.RunStarted, _feed.Copy(MapItems.Waystone(14)));

        Assert.Equal([1, 2], Session.Runs.Select(r => r.Number));
        Assert.Equal(Session.Runs[1].StartedAt, first.EndedAt);
        Assert.Same(Session.Runs[1], Session.CurrentRun);
        Assert.Contains("Tier 15", first.Waystone!.Label);
    }

    [Fact]
    public void End_run_ends_the_last_run_and_later_captures_belong_to_no_run()
    {
        _feed.Copy(MapItems.Waystone(15));

        Assert.True(Session.EndRun(_feed.Now));
        _feed.Copy(MapItems.Rare("After The Run"));

        var run = Assert.Single(Session.Runs);
        Assert.Equal(_feed.Now - TimeSpan.FromSeconds(1), run.EndedAt);
        Assert.Null(Session.CurrentRun);
        Assert.Single(run.CaptureIds); // only the waystone
        Assert.False(Session.EndRun(_feed.Now));
    }

    [Fact]
    public void Captures_between_boundaries_attach_to_the_right_run()
    {
        _feed.Copy(MapItems.Rare("Before Any Run"));
        _feed.Copy(MapItems.Waystone(15));
        _feed.Copy(MapItems.Rare("Drop A"));
        _feed.Copy(MapItems.Rare("Drop A"), logged: false); // changed nothing in the capture log: no id to attach
        _feed.Copy(MapItems.Waystone(16));
        _feed.Copy(MapItems.Rare("Drop B"));
        _feed.Copy(MapItems.Rare("Drop C"));

        Assert.Equal([2, 3], Session.Runs.Select(r => r.CaptureIds.Count));
    }

    [Fact]
    public void The_same_waystone_copied_again_right_away_is_the_same_run()
    {
        _feed.Copy(MapItems.Waystone(15));
        Assert.Equal(MappingOutcome.SameWaystone, _feed.Copy(MapItems.Waystone(15), logged: false));
        _feed.Copy(MapItems.Currency("Chaos Orb", 1)); // a loot check: not setup

        // Once something else happened, the same text is the next run (two identical waystones).
        Assert.Equal(MappingOutcome.RunStarted, _feed.Copy(MapItems.Waystone(15)));
        Assert.Equal(2, Session.Runs.Count);
    }

    [Fact]
    public void A_waystone_copied_in_a_scan_cell_is_part_of_the_scan_not_a_run()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.StartScan();

        Assert.Equal(MappingOutcome.ScanCellUpdated, _feed.Copy(MapItems.Waystone(16), cell: new GridCell(3, 1)));

        Assert.Single(Session.Runs);
        var cell = Session.ActiveScan!.Cells[new GridCell(3, 1)];
        Assert.Null(cell.Stack);
    }

    [Fact]
    public void Tablets_copied_after_the_waystone_form_the_setup()
    {
        _feed.Copy(MapItems.Waystone(15));
        Assert.Equal(MappingOutcome.SetupItemAdded, _feed.Copy(MapItems.Tablet("Ritual Tablet")));
        Assert.Equal(MappingOutcome.SetupItemAdded, _feed.Copy(MapItems.Unknown("Breach Something")));

        var setup = Session.CurrentRun!.Setup;
        Assert.Equal([SetupItemKind.Waystone, SetupItemKind.Tablet, SetupItemKind.PossibleTablet], setup.Select(i => i.Kind));
        Assert.Equal(["Ritual Tablet", "Breach Something"], Session.CurrentRun.Tablets.Select(t => t.Label));
    }

    [Fact]
    public void Setup_holds_at_most_four_tablets()
    {
        _feed.Copy(MapItems.Waystone(15));
        for (var i = 1; i <= 5; i++)
            _feed.Copy(MapItems.Tablet($"Tablet {i}"));

        Assert.Equal(4, Session.CurrentRun!.Tablets.Count());
        Assert.Equal(6, Session.CurrentRun.CaptureIds.Count);
    }

    [Fact]
    public void Unknown_items_count_as_possible_tablets_only_in_the_setup_phase()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Now += _feed.Options.SetupWindow; // too long after the waystone: a loot check
        Assert.Equal(MappingOutcome.Attached, _feed.Copy(MapItems.Unknown("Late")));

        _feed.Copy(MapItems.Waystone(16));
        _feed.Copy(MapItems.Currency("Chaos Orb", 1)); // a loot check: ends the setup phase
        Assert.Equal(MappingOutcome.Attached, _feed.Copy(MapItems.Unknown("After Loot")));
        // A known tablet still joins until the first stash or final scan (a swipe, like the Initial, does not end the setup).
        Assert.Equal(MappingOutcome.SetupItemAdded, _feed.Copy(MapItems.Tablet("Late Tablet")));
        _feed.Swipe(("Chaos Orb", 3));
        Assert.Equal(MappingOutcome.SetupItemAdded, _feed.Copy(MapItems.Tablet("After Swipe")));

        _feed.Stash(("Chaos Orb", 3));
        Assert.Equal(MappingOutcome.Attached, _feed.Copy(MapItems.Tablet("After Stash")));
        Assert.Equal(2, Session.CurrentRun!.Tablets.Count());
    }

    [Fact]
    public void Stackables_and_rare_items_are_never_setup()
    {
        _feed.Copy(MapItems.Waystone(15));

        Assert.Equal(MappingOutcome.Attached, _feed.Copy(MapItems.Currency("Chaos Orb", 3)));
        Assert.Empty(Session.CurrentRun!.Tablets);
    }

    [Fact]
    public void A_possible_tablet_can_be_confirmed_or_rejected_and_undone()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Copy(MapItems.Unknown("Mystery"));
        var item = Session.CurrentRun!.Setup[1];

        Assert.True(Session.SetSetupItemKind(item.Id, SetupItemKind.NotSetup));
        Assert.Empty(Session.CurrentRun.Tablets);
        Session.Undo();
        Assert.Equal(SetupItemKind.PossibleTablet, item.Kind);
        Assert.Throws<InvalidOperationException>(() => Session.SetSetupItemKind(Session.CurrentRun.Setup[0].Id, SetupItemKind.Tablet));
    }

    [Fact]
    public void A_scan_with_no_run_open_starts_a_new_run()
    {
        var before = _feed.Swipe(("Chaos Orb", 3));
        _feed.Copy(MapItems.Waystone(15)); // before any stash scan: the waystone joins the run the scan started
        var inRun = _feed.Stash(("Chaos Orb", 10));
        Session.EndRun(_feed.Now);
        var after = _feed.Swipe(("Chaos Orb", 4));

        Assert.Equal(2, Session.Runs.Count);
        Assert.Equal([Session.Runs[0].Id, Session.Runs[0].Id, Session.Runs[1].Id], new[] { before, inRun, after }.Select(s => s.RunId));
        Assert.Equal("Waystone (Tier 15) of Hexwarding", Session.Runs[0].Waystone!.Label);
        Assert.Null(Session.Runs[1].Waystone);
        Assert.Equal("Initial", Session.NameOf(after));
    }

    [Fact]
    public void Start_new_run_works_with_no_copy_and_ends_the_previous_run()
    {
        _feed.Copy(MapItems.Waystone(15));
        var run = Session.StartNewRun(_feed.Now);

        Assert.Equal((2, true), (run.Number, run.IsOpen));
        Assert.Equal(_feed.Now, Session.Runs[0].EndedAt);
        Assert.Empty(run.Setup);
        Assert.Equal("?", run.SetupText);

        // The setup copied right after it joins it.
        _feed.Copy(MapItems.Waystone(16));
        _feed.Copy(MapItems.Tablet("Ritual Tablet"));
        Assert.Equal(2, Session.Runs.Count);
        Assert.Equal("T16 + 1 tablet", run.SetupText);
    }

    [Fact]
    public void A_waystone_after_a_stash_scan_starts_the_next_run()
    {
        Session.StartNewRun(_feed.Now);
        _feed.Stash(("Chaos Orb", 3));
        _feed.Copy(MapItems.Waystone(15));

        Assert.Equal(2, Session.Runs.Count);
    }

    [Fact]
    public void Recall_lists_offer_waystones_and_tablets_from_setups_and_scans()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Copy(MapItems.Tablet("Ritual Tablet"));
        _feed.StartScan(); // waystones and a tablet waiting in the inventory
        _feed.Copy(MapItems.Waystone(16), cell: new GridCell(0, 0));
        _feed.Copy(MapItems.Waystone(15), cell: new GridCell(1, 0)); // the same text as run 1's: listed once
        _feed.Copy(MapItems.Tablet("Breach Tablet"), cell: new GridCell(2, 0));
        _feed.Copy(MapItems.Rare("Armour"), cell: new GridCell(3, 0));
        _feed.End();

        Assert.Equal(["Waystone (Tier 15) of Hexwarding", "Waystone (Tier 16) of Hexwarding"],
            Session.RecallWaystones().Select(i => i.Label).Order());
        Assert.Equal(["Breach Tablet", "Ritual Tablet"], Session.RecallTablets().Select(i => i.Label).Order());
        Assert.All(Session.RecallTablets(), i => Assert.Equal(1, i.RunNumber));
    }

    [Fact]
    public void A_recalled_item_is_assigned_to_a_run_by_reference_and_undoable()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.StartScan();
        _feed.Copy(MapItems.Waystone(16), cell: new GridCell(0, 0));
        _feed.End();
        var run = Session.StartNewRun(_feed.Now); // the player rushed the map start

        var waystone = Session.RecallWaystones().Single(i => i.Label.Contains("Tier 16"));
        Assert.True(Session.AssignSetup(run.Id, waystone));
        Assert.Equal((1, waystone.CaptureId), (run.Waystone!.RecalledFromRun, run.Waystone.CaptureId));
        Assert.Equal("T16", run.SetupText);
        Assert.False(Session.AssignSetup(run.Id, waystone)); // already there

        // Another waystone replaces it.
        Assert.True(Session.AssignSetup(run.Id, Session.RecallWaystones().Single(i => i.Label.Contains("Tier 15"))));
        Assert.Equal("T15", run.SetupText);
        Session.Undo();
        Assert.Equal("T16", run.SetupText);
        Session.Undo();
        Assert.Empty(run.Setup);
    }

    [Fact]
    public void The_recall_dialog_sets_the_whole_setup_in_one_undoable_edit()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Copy(MapItems.Tablet("Ritual Tablet"));
        _feed.Copy(MapItems.Tablet("Breach Tablet"));
        _feed.StartScan();
        _feed.Copy(MapItems.Waystone(16), cell: new GridCell(0, 0));
        _feed.Copy(MapItems.Tablet("Delirium Tablet"), cell: new GridCell(1, 0));
        _feed.End();
        var run = Session.StartNewRun(_feed.Now);
        var ritual = Session.CurrentRun!.Tablets.Any() ? null : Session.RecallTablets().Single(i => i.Label == "Ritual Tablet");

        Assert.True(Session.SetSetup(run.Id, Session.RecallWaystones().Single(i => i.Label.Contains("Tier 16")),
            [ritual!, Session.RecallTablets().Single(i => i.Label == "Delirium Tablet")]));
        Assert.Equal("T16 + 2 tablets", run.SetupText);
        Assert.Equal(10, Session.RecallTablets().Single(i => i.Label == "Delirium Tablet").UsesLeft);

        // Unchecking a tablet removes it; the kept one stays the same item.
        var kept = run.Tablets.First();
        Assert.True(Session.SetSetup(run.Id, null, [Session.RecallTablets().Single(i => i.Label == "Ritual Tablet")]));
        Assert.Same(kept, Assert.Single(run.Tablets));
        Assert.Equal("T16 + 1 tablet", run.SetupText);

        Session.Undo();
        Session.Undo();
        Assert.Empty(run.Setup);
        Assert.Throws<InvalidOperationException>(() => Session.SetSetup(run.Id, null, [.. Session.RecallTablets(), .. Session.RecallTablets()]));
    }

    [Fact]
    public void The_previous_run_tablets_fill_the_free_slots()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Copy(MapItems.Tablet("Ritual Tablet"));
        _feed.Copy(MapItems.Tablet("Breach Tablet"));
        _feed.Copy(MapItems.Waystone(16));
        _feed.Copy(MapItems.Tablet("Ritual Tablet")); // copied again: not added twice
        var run = Session.Runs[1];

        Assert.True(Session.UsePreviousTablets(run.Id));
        Assert.Equal(["Ritual Tablet", "Breach Tablet"], run.Tablets.Select(t => t.Label));
        Assert.Equal([null, 1], run.Tablets.Select(t => t.RecalledFromRun));
        Assert.False(Session.UsePreviousTablets(run.Id));
        Assert.False(Session.UsePreviousTablets(Session.Runs[0].Id)); // no previous run

        Assert.True(Session.RemoveSetupItem(run.Id, run.Tablets.Last().Id));
        Assert.Single(run.Tablets);
    }

    [Fact]
    public void A_canceled_scan_does_not_count_and_a_rescanned_cell_replaces_its_stack()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.StartScan();
        _feed.Stack("Chaos Orb", 3, new GridCell(0, 0));
        var canceled = _feed.End(SweepStatus.Canceled);

        _feed.StartScan();
        _feed.Stack("Chaos Orb", 3, new GridCell(0, 0));
        _feed.Stack("Chaos Orb", 5, new GridCell(0, 0)); // the same cell again: updates, does not add
        var scan = _feed.End();

        Assert.False(canceled.Counts);
        Assert.Equal(5, Assert.Single(scan.Totals()).Count);
        Assert.Equal("Initial", Session.NameOf(scan)); // the canceled scan is not the run's first swipe
    }
}
