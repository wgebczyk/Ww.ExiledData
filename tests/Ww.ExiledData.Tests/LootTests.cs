using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.Tests;

/// <summary>Loot derivation from scans and the Final-vs-Initial check (MAPPING_PLAN.md 3.3, 8).</summary>
public sealed class LootTests
{
    private const string Chaos = "Chaos Orb";
    private const string Trans = "Orb of Transmutation";
    private const string Exalted = "Exalted Orb";
    private const string Divine = "Divine Orb";

    private readonly MapFeed _feed = new();
    private MapSession Session => _feed.Session;

    private RunLoot LootOf(int run) => Loot.Of(Session, Session.Runs[run - 1]);

    private static int Count(RunLoot loot, string name) => loot.CountOf(MapItems.KeyOf(name));

    [Fact]
    public void Only_stash_scans_count_and_swipes_never_do()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Swipe((Chaos, 50));
        _feed.Stash((Chaos, 12), (Trans, 40));
        _feed.Swipe((Divine, 3));
        _feed.Final((Chaos, 50), (Divine, 3));

        var loot = LootOf(1);
        Assert.Single(loot.StashScans);
        Assert.Equal((12, 40, 0), (Count(loot, Chaos), Count(loot, Trans), Count(loot, Divine)));
        Assert.Equal([Trans, Chaos], loot.Total.Select(c => c.Name)); // highest first
    }

    [Fact]
    public void Two_stash_scans_of_one_currency_add_and_never_override()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Stash((Chaos, 15));
        _feed.Stash((Chaos, 11)); // a second hideout trip

        Assert.Equal(26, Count(LootOf(1), Chaos));
    }

    [Fact]
    public void Multiple_stacks_of_one_currency_are_summed_within_a_scan()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Stash((Trans, 40), (Trans, 25), (Chaos, 2));

        Assert.Equal(65, Count(LootOf(1), Trans));
    }

    [Fact]
    public void Counts_above_the_max_stack_are_kept()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Stash((Trans, 606));

        Assert.Equal(606, Count(LootOf(1), Trans));
    }

    [Fact]
    public void Canceled_scans_do_not_count_and_interrupted_ones_do()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.StartScan(ScanKind.Stash);
        _feed.Stack(Chaos, 7);
        _feed.End(SweepStatus.Canceled);
        _feed.StartScan(ScanKind.Stash);
        _feed.Stack(Chaos, 4);
        _feed.End(SweepStatus.Interrupted);

        Assert.Equal(4, Count(LootOf(1), Chaos));
    }

    [Fact]
    public void A_changed_scan_kind_recomputes_the_loot()
    {
        _feed.Copy(MapItems.Waystone(15));
        var swipe = _feed.Swipe((Chaos, 10));
        _feed.Stash((Chaos, 25));
        Assert.Equal(25, Count(LootOf(1), Chaos));

        Assert.True(Session.SetScanKind(swipe.Id, ScanKind.Stash));
        Assert.Equal(35, Count(LootOf(1), Chaos));

        Session.Undo();
        Assert.Equal(25, Count(LootOf(1), Chaos));
        Assert.False(Session.SetScanKind(swipe.Id, ScanKind.Swipe)); // no change
    }

    [Fact]
    public void Only_scans_count_not_copies_outside_them()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Copy(MapItems.Currency(Chaos, 50));

        Assert.Empty(LootOf(1).Total);
    }

    [Fact]
    public void The_session_loot_sums_the_runs()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Stash((Chaos, 7));
        _feed.Copy(MapItems.Waystone(16));
        _feed.Stash((Chaos, 2), (Divine, 1));

        Assert.Equal([(Chaos, 9), (Divine, 1)], Loot.OfSession(Session).Select(c => (c.Name, c.Count)));
    }

    // --- Initial and the Final check ---------------------------------------------------------------

    [Fact]
    public void The_first_swipe_of_each_run_is_its_Initial()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.StartScan(ScanKind.Swipe);
        _feed.End(SweepStatus.Canceled); // a canceled swipe is not the Initial
        var initial = _feed.Swipe((Exalted, 5));
        var later = _feed.Swipe((Exalted, 6));
        var stash = _feed.Stash((Exalted, 1));
        _feed.Copy(MapItems.Waystone(16));
        var second = _feed.Swipe((Exalted, 5));

        Assert.Equal(["Initial", "Swipe", "Stash scan", "Initial"], new[] { initial, later, stash, second }.Select(Session.NameOf));
        Assert.Same(initial, Session.InitialOf(Session.Runs[0]));
    }

    [Fact]
    public void Worked_example_kept_currencies_split_off_before_stashing()
    {
        // 5 Exalted kept, 3 gathered: split 5/3, stash scan of the 3. A second trip stashes 4 more: 7 in all.
        _feed.Copy(MapItems.Waystone(15));
        _feed.Swipe((Exalted, 5));
        _feed.Stash((Exalted, 3));
        _feed.Stash((Exalted, 4));
        Assert.Equal(7, Count(LootOf(1), Exalted));

        // A Final scan showing the 5 kept matches the Initial: no warning.
        _feed.Final((Exalted, 5));
        var check = Loot.CheckOf(Session, Session.Runs[0])!;
        Assert.Empty(check.Differences);
        Assert.False(check.HasWarning);
    }

    [Fact]
    public void A_final_with_more_than_the_initial_warns_and_proposes_an_adjustment()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Swipe((Exalted, 5));
        _feed.Stash((Exalted, 3));
        var final = _feed.Final((Exalted, 7)); // 2 gathered but never stashed

        var check = Loot.CheckOf(Session, Session.Runs[0])!;
        Assert.True(check.HasWarning);
        var open = Assert.Single(check.Open);
        Assert.Equal((Exalted, 5, 7, 2), (open.Name, open.Initial, open.Final, open.Proposed));
        Assert.Contains(Loot.Summarize(Session, final).Warnings, w => w.Contains("Exalted Orb +2"));

        Assert.True(Session.ApplyAdjustments(final.Id, [(open.Key, open.Name, open.Proposed)], _feed.Now));
        Assert.Equal(5, Count(LootOf(1), Exalted));
        Assert.False(Loot.CheckOf(Session, Session.Runs[0])!.HasWarning);

        Session.Undo(); // the adjustment is undone, and proposed again
        Assert.Equal(3, Count(LootOf(1), Exalted));
        Assert.True(Loot.CheckOf(Session, Session.Runs[0])!.HasWarning);
    }

    [Fact]
    public void An_edited_adjustment_leaves_the_rest_proposed_and_can_be_removed()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Swipe((Exalted, 5));
        var final = _feed.Final((Exalted, 9));

        Session.ApplyAdjustments(final.Id, [(MapItems.KeyOf(Exalted), Exalted, 3)], _feed.Now);
        var check = Loot.CheckOf(Session, Session.Runs[0])!;
        Assert.Equal(1, Assert.Single(check.Open).Proposed);
        Assert.Equal(3, Count(LootOf(1), Exalted));

        var adjustment = Assert.Single(Session.Runs[0].Adjustments);
        Assert.True(Session.RemoveAdjustment(Session.Runs[0].Id, adjustment.Id));
        Assert.Equal(0, Count(LootOf(1), Exalted));
    }

    [Fact]
    public void A_negative_adjustment_takes_loot_away_but_never_below_zero_in_the_total()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Swipe((Chaos, 10));
        _feed.Stash((Chaos, 2));
        var final = _feed.Final((Chaos, 6)); // 4 spent from the kept stack

        var open = Assert.Single(Loot.CheckOf(Session, Session.Runs[0])!.Open);
        Assert.Equal(-4, open.Proposed);
        Session.ApplyAdjustments(final.Id, [(open.Key, open.Name, open.Proposed)], _feed.Now);

        Assert.Empty(LootOf(1).Total);
    }

    [Fact]
    public void A_dismissed_check_does_not_warn()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Swipe((Exalted, 5));
        var final = _feed.Final((Exalted, 6));

        Assert.True(Session.DismissCheck(final.Id, true));
        Assert.False(Loot.CheckOf(Session, Session.Runs[0])!.HasWarning);
        Session.Undo();
        Assert.True(Loot.CheckOf(Session, Session.Runs[0])!.HasWarning);
    }

    [Fact]
    public void Without_an_initial_swipe_the_final_is_compared_with_an_empty_inventory()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Final((Chaos, 2));

        var check = Loot.CheckOf(Session, Session.Runs[0])!;
        Assert.Null(check.Initial);
        Assert.Equal(2, Assert.Single(check.Open).Proposed);
    }

    [Fact]
    public void The_check_uses_the_last_final_scan()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Swipe((Chaos, 5));
        _feed.Final((Chaos, 8));
        var last = _feed.Final((Chaos, 5));

        var check = Loot.CheckOf(Session, Session.Runs[0])!;
        Assert.Same(last, check.Final);
        Assert.False(check.HasWarning);
    }

    // --- removing scans --------------------------------------------------------------------------------

    [Fact]
    public void A_removed_stash_scan_leaves_the_loot_and_undo_brings_it_back()
    {
        _feed.Copy(MapItems.Waystone(15));
        var first = _feed.Stash((Chaos, 15));
        _feed.Stash((Chaos, 11));

        Assert.True(Session.RemoveScan(first.Id));
        Assert.Equal(ScanStatus.Removed, first.Status);
        Assert.Equal(11, Count(LootOf(1), Chaos));
        Assert.False(Session.RemoveScan(first.Id)); // already removed

        Session.Undo();
        Assert.Equal(ScanStatus.Finished, first.Status);
        Assert.Equal(26, Count(LootOf(1), Chaos));
    }

    [Fact]
    public void Removing_the_initial_makes_the_next_swipe_the_initial()
    {
        _feed.Copy(MapItems.Waystone(15));
        var initial = _feed.Swipe((Chaos, 5));
        var next = _feed.Swipe((Chaos, 8));
        _feed.Final((Chaos, 8));

        Assert.True(Session.RemoveScan(initial.Id));

        Assert.Same(next, Session.InitialOf(Session.Runs[0]));
        Assert.Equal("Initial", Session.NameOf(next));
        Assert.False(Loot.CheckOf(Session, Session.Runs[0])!.HasWarning);
    }

    [Fact]
    public void Removing_a_final_removes_its_adjustments_in_the_same_edit()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.Swipe((Exalted, 5));
        var earlier = _feed.Final((Exalted, 5));
        var final = _feed.Final((Exalted, 7));
        Session.ApplyAdjustments(final.Id, [(MapItems.KeyOf(Exalted), Exalted, 2)], _feed.Now);
        Assert.Equal(2, Count(LootOf(1), Exalted));

        Assert.True(Session.RemoveScan(final.Id));
        Assert.Empty(Session.Runs[0].Adjustments);
        Assert.Equal(0, Count(LootOf(1), Exalted));
        Assert.Same(earlier, Loot.CheckOf(Session, Session.Runs[0])!.Final);

        Session.Undo(); // one undo restores both
        Assert.Equal(ScanStatus.Finished, final.Status);
        Assert.Single(Session.Runs[0].Adjustments);
        Assert.Same(final, Loot.CheckOf(Session, Session.Runs[0])!.Final);
    }

    [Fact]
    public void A_running_scan_cannot_be_removed()
    {
        _feed.Copy(MapItems.Waystone(15));
        var scan = _feed.StartScan(ScanKind.Stash);

        Assert.Throws<InvalidOperationException>(() => Session.RemoveScan(scan.Id));
    }

    // --- summary -------------------------------------------------------------------------------------

    [Fact]
    public void Summary_shows_the_totals_before_the_scan_finishes()
    {
        _feed.Copy(MapItems.Waystone(15));
        var scan = _feed.StartScan(ScanKind.Stash);
        _feed.Stack(Chaos, 10);
        _feed.Stack(Chaos, 4);

        var summary = Loot.Summarize(Session, scan);

        Assert.Equal("Stash scan", summary.Name);
        Assert.Equal(14, Assert.Single(summary.Totals).Count);
        Assert.Empty(summary.Warnings);
    }

    [Fact]
    public void Summary_warns_about_a_stash_scan_without_currency()
    {
        _feed.Copy(MapItems.Waystone(15));
        _feed.StartScan(ScanKind.Stash);
        _feed.Copy(MapItems.Rare("Not Currency"), cell: new GridCell(0, 0));
        var scan = _feed.End();

        Assert.Contains(Loot.Summarize(Session, scan).Warnings, w => w.StartsWith("No currency", StringComparison.Ordinal));
    }
}
