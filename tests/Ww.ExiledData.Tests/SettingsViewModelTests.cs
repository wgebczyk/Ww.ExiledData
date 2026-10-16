using Ww.ExiledData.App;
using Ww.ExiledData.App.Controls;
using Ww.ExiledData.App.ViewModels;
using Ww.ExiledData.Capture;
using Ww.ExiledData.Mapping;
using AvaloniaKey = Avalonia.Input.Key;
using AvaloniaModifiers = Avalonia.Input.KeyModifiers;

namespace Ww.ExiledData.Tests;

/// <summary>The Settings dialog logic.</summary>
public sealed class SettingsViewModelTests
{
    private static SettingsViewModel Create(AppSettings? saved = null, IReadOnlyCollection<HotkeyAction>? failed = null) =>
        new(saved ?? new AppSettings(), new SettingsEnvironment("C:\\data", failed ?? [], false));

    [Fact]
    public void Saved_settings_open_clean()
    {
        var vm = Create();

        Assert.Equal(0, vm.ChangeCount);
        Assert.Equal(0, vm.ProblemCount);
        Assert.Equal("No unsaved changes", vm.DirtyText);
        Assert.Equal("Close", vm.CancelText);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal(new AppSettings(), vm.TryBuild());
    }

    [Fact]
    public void An_edit_is_counted_on_its_page_and_enables_save()
    {
        var vm = Create();

        vm.PositionTolerance.Text = "14";

        Assert.True(vm.PositionTolerance.IsChanged);
        Assert.True(vm.Capture.IsDirty);
        Assert.False(vm.HotkeysPage.IsDirty);
        Assert.Equal("1 unsaved change", vm.DirtyText);
        Assert.Equal("Cancel", vm.CancelText);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Equal("↺ Reset to 10", vm.PositionTolerance.ResetText);
        Assert.Equal(14, vm.TryBuild()!.PositionTolerance);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("201")]
    [InlineData("-1")]
    public void An_invalid_number_blocks_saving_and_is_never_corrected(string text)
    {
        var vm = Create();

        vm.PositionTolerance.Text = text;

        Assert.Equal("Error: must be 0 to 200 px.", vm.PositionTolerance.Error);
        Assert.Equal(text, vm.PositionTolerance.Text);
        Assert.Equal(1, vm.Capture.ErrorCount);
        Assert.Equal("1 problem to fix before saving", vm.FooterMessage);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Null(vm.TryBuild());
    }

    [Fact]
    public void Steppers_use_the_step_and_stay_in_range()
    {
        var vm = Create();

        vm.ClipboardWait.IncreaseCommand.Execute(null);
        Assert.Equal("110", vm.ClipboardWait.Text);

        vm.ClipboardWait.Text = "2000";
        Assert.False(vm.ClipboardWait.IncreaseCommand.CanExecute(null));
        vm.ClipboardWait.Text = "5";
        vm.ClipboardWait.IncreaseCommand.Execute(null);
        Assert.Equal("15", vm.ClipboardWait.Text);
    }

    [Fact]
    public void Of_two_equal_hotkeys_the_one_changed_last_carries_the_error()
    {
        var vm = Create();

        vm.SweepFinish.Text = "Ctrl+Shift+Y"; // Redo's

        Assert.Equal("Error: already used by Redo. Hotkeys must be different from each other.", vm.SweepFinish.Error);
        Assert.Null(vm.Redo.Error);
        Assert.Equal("Note: Finish / cancel grid is set to the same combination.", vm.Redo.Note);
        Assert.Equal(1, vm.ProblemCount);
        Assert.Equal("1 error", vm.HotkeysPage.ErrorText);
    }

    [Theory]
    [InlineData("Ctrl+C", "Error: Ctrl+C is reserved for item capture.")]
    [InlineData("Ctrl+Alt+", "Error: not a valid hotkey (for example Ctrl+Shift+Z).")]
    [InlineData("Ctrl+Alt+F25", "Error: not a valid hotkey (for example Ctrl+Shift+Z).")]
    public void Invalid_hotkeys_are_errors(string text, string error)
    {
        var vm = Create();

        vm.Undo.Text = text;

        Assert.Equal(error, vm.Undo.Error);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Hotkey_warnings_do_not_block_saving()
    {
        var vm = Create(failed: [HotkeyAction.SkipCell]);

        vm.Undo.Text = "F9";

        Assert.Equal("Warning: already in use by another app. Change it here.", vm.SkipCell.Warning);
        Assert.StartsWith("Warning: F9 has no modifier.", vm.Undo.Warning);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Saved_hotkeys_are_canonical_text()
    {
        var vm = Create();

        vm.Undo.TypeInsteadCommand.Execute(null);
        vm.Undo.Text = "alt+ctrl+u";

        Assert.Equal("Ctrl+Alt+U", vm.TryBuild()!.HotkeyUndo);
    }

    [Fact]
    public void Recording_suspends_the_global_hotkeys_one_row_at_a_time()
    {
        var vm = Create();
        var suspended = new List<bool>();
        vm.SuspendHotkeys = suspended.Add;

        vm.Undo.ToggleRecordingCommand.Execute(null);
        vm.Redo.ToggleRecordingCommand.Execute(null);

        Assert.False(vm.Undo.IsRecording);
        Assert.True(vm.Redo.IsRecording);
        Assert.Equal("Cancel", vm.Redo.RecordButtonText);

        vm.Redo.CompleteRecording(new Hotkey(KeyModifiers.Ctrl | KeyModifiers.Shift, 'R'));

        Assert.Equal("Ctrl+Shift+R", vm.Redo.Text);
        Assert.False(suspended[^1]);
    }

    [Theory]
    [InlineData(AvaloniaKey.N, AvaloniaModifiers.Control | AvaloniaModifiers.Alt, "Ctrl+Alt+N")]
    [InlineData(AvaloniaKey.D7, AvaloniaModifiers.Shift, "Shift+7")]
    [InlineData(AvaloniaKey.F12, AvaloniaModifiers.Meta, "Win+F12")]
    [InlineData(AvaloniaKey.N, AvaloniaModifiers.None, null)]
    [InlineData(AvaloniaKey.LeftCtrl, AvaloniaModifiers.Control, null)]
    [InlineData(AvaloniaKey.Space, AvaloniaModifiers.Control, null)]
    public void The_recorder_needs_a_modifier_and_an_allowed_key(AvaloniaKey key, AvaloniaModifiers modifiers, string? expected) =>
        Assert.Equal(expected, KeyRecorderBox.ToHotkey(key, modifiers)?.ToString());

    [Fact]
    public void Area_names_must_be_unique_ignoring_case()
    {
        var vm = Create();

        vm.Areas.AddCommand.Execute(null);
        vm.Areas.Rows[^1].Name = "stash";

        Assert.Equal("Error: “stash” is listed twice. Names must be unique, ignoring case.", vm.Areas.Rows[^1].Error);
        Assert.Equal(1, vm.Sweeps.ErrorCount);
        Assert.Null(vm.TryBuild());

        vm.Areas.Rows[^1].Name = "Map device";
        vm.Areas.Rows[^1].Columns.Text = "6";
        vm.Areas.Rows[^1].Rows.Text = "1";

        Assert.Equal("Inventory=12x5; Stash=12x12; Quad stash=24x24; Map device=6x1", vm.TryBuild()!.GridKinds);
    }

    [Fact]
    public void Area_sizes_and_names_are_checked()
    {
        var vm = Create();
        var row = vm.Areas.Rows[0];

        row.Columns.Text = "65";
        Assert.Equal("Error: columns must be 1 to 64.", row.Error);

        row.Columns.Text = "12";
        row.Name = "A=B";
        Assert.Equal("Error: names cannot contain = or ;.", row.Error);
    }

    [Fact]
    public void The_last_area_cannot_be_removed()
    {
        var vm = Create(new AppSettings { GridKinds = "Inventory=12x5; Stash=12x12" });

        vm.Areas.Rows[0].RemoveCommand.Execute(null);

        Assert.Single(vm.Areas.Rows);
        Assert.False(vm.Areas.Rows[0].RemoveCommand.CanExecute(null));
        Assert.Equal("Stash=12x12", vm.TryBuild()!.GridKinds);
    }

    [Fact]
    public void Reset_page_restores_the_defaults_of_that_page_only()
    {
        var vm = Create(new AppSettings { PositionTolerance = 30, HotkeyUndo = "Ctrl+Shift+U" });

        vm.SelectedPage = vm.Capture;
        vm.ResetPageCommand.Execute(null);

        Assert.Equal("10", vm.PositionTolerance.Text);
        Assert.Equal("Ctrl+Shift+U", vm.Undo.Text);
        Assert.Equal(1, vm.ChangeCount);
    }

    [Fact]
    public void Search_filters_the_categories_and_opens_the_first_match()
    {
        var vm = Create();

        vm.SearchText = "tolerance";

        Assert.Equal([vm.Capture], vm.VisiblePages);
        Assert.Same(vm.Capture, vm.SelectedPage);

        vm.SearchText = "undo";
        Assert.Same(vm.HotkeysPage, vm.SelectedPage);

        vm.SearchText = "";
        Assert.Equal(vm.Pages, vm.VisiblePages);
    }

    [Fact]
    public void Footer_message_jumps_to_the_first_page_with_a_problem()
    {
        var vm = Create();
        vm.Areas.Rows[0].Name = "";
        vm.SelectedPage = vm.General;

        vm.JumpToFirstErrorCommand.Execute(null);

        Assert.Same(vm.Sweeps, vm.SelectedPage);
    }

    [Fact]
    public void Theme_previews_live_and_cancel_puts_it_back()
    {
        var vm = Create();
        var applied = new List<AppTheme>();
        vm.PreviewTheme = applied.Add;

        vm.Theme.Option(AppTheme.Light).IsSelected = true;
        vm.Discard();

        Assert.Equal([AppTheme.Light, AppTheme.Dark], applied);
    }

    [Fact]
    public void The_loot_grid_always_ends_with_one_empty_row()
    {
        var vm = Create(new AppSettings { LootGrid = [[null]] }); // nothing defined: the default grid
        var grid = vm.LootGrid;

        Assert.Equal(AppSettings.DefaultLootGrid.Count + 1, grid.Rows.Count);
        Assert.True(grid.IsDefault);
        Assert.Equal(0, vm.ChangeCount);
        foreach (var row in grid.Rows.Take(grid.Rows.Count - 1).ToList())
            row.RemoveCommand.Execute(null);

        Assert.Single(grid.Rows);
        Assert.Equal(LootLayout.Columns, grid.Rows[0].Cells.Count);
        Assert.False(grid.Rows[0].CanRemove);

        grid.Rows[0].Cells[4].Name = "Chaos Orb";
        Assert.Equal(2, grid.Rows.Count);
        Assert.True(grid.Rows[1].IsEmpty);
        Assert.True(grid.Rows[0].CanRemove);

        grid.Rows[1].Cells[0].Name = "Divine Orb";
        Assert.Equal(3, grid.Rows.Count);

        grid.Rows[1].Cells[0].Name = null; // the row above the last is empty again: the last goes
        Assert.Equal(2, grid.Rows.Count);

        Assert.True(vm.Mapping.IsDirty);
        var saved = vm.TryBuild()!;
        Assert.Equal([null, null, null, null, "Chaos Orb", null, null, null, null], Assert.Single(saved.LootGrid));
    }

    [Fact]
    public void A_gap_row_stays_and_the_picker_picks()
    {
        var vm = Create(new AppSettings { LootGrid = [["Chaos Orb"], [], ["Divine Orb"]] });
        var grid = vm.LootGrid;

        Assert.Equal(4, grid.Rows.Count); // three rows and the empty one
        Assert.Equal(0, vm.ChangeCount);

        var cell = grid.Rows[1].Cells[0];
        cell.IsPickerOpen = true;
        cell.Filter = "exalted";
        Assert.Contains(cell.Matches, m => m.Choice.Name == "Exalted Orb");
        cell.PickFirstCommand.Execute(null);
        Assert.False(cell.IsPickerOpen);
        Assert.StartsWith("Exalted", cell.Name);

        grid.Rows[2].RemoveCommand.Execute(null);
        Assert.Equal(2, vm.TryBuild()!.LootGrid.Count);

        grid.ResetCommand.Execute(null);
        Assert.True(grid.IsDefault);
        Assert.Equal(AppSettings.DefaultLootGrid.Count, vm.TryBuild()!.LootGrid.Count);
    }

    [Fact]
    public void The_default_loot_grid_has_rows_of_nine_catalog_currencies()
    {
        var layout = new AppSettings().ParsedLootGrid();

        Assert.Equal(5, layout.Rows.Count);
        Assert.All(layout.Names, n => Assert.NotNull(CurrencyCatalog.Default.Find(n)));
        Assert.Equal(layout.Rows, new AppSettings { LootGrid = [] }.ParsedLootGrid().Rows);
    }

    [Fact]
    public async Task A_failed_save_keeps_the_dialog_open_with_the_reason()
    {
        var vm = Create();
        var closed = false;
        vm.Saved = () => closed = true;
        vm.Apply = _ => Task.FromResult<string?>("disk full");
        vm.LogLevel.Selected = vm.LogLevel.Options[0];

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.Equal("Applying settings failed: disk full", vm.FooterMessage);
        Assert.True(vm.FooterIsError);

        vm.Apply = _ => Task.FromResult<string?>(null);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(closed);
    }
}
