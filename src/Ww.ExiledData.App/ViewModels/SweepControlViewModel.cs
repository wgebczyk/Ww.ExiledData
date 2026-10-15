using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ww.ExiledData.App.Services;
using Ww.ExiledData.Core.Sweeps;

namespace Ww.ExiledData.App.ViewModels;

/// <summary>
/// Sweep state shared by the session windows (only the recording session's can start or end a sweep): the chosen grid
/// kind, the running sweep and the hotkey hint. One instance, owned by the host.
/// </summary>
public sealed partial class SweepControlViewModel : ObservableObject
{
    private readonly SessionController _controller;

    public SweepControlViewModel(SessionController controller)
    {
        _controller = controller;
        controller.StateChanged += active => Dispatcher.UIThread.Post(() => Sweep = active?.Sweep);
    }

    // Supplied by the host: opens the grid editor for a kind (on the game's screen) and starts the sweep when confirmed.
    public Func<GridKind, Task>? BeginSweep { get; set; }
    public Action<GridKind>? GridKindChosen { get; set; }

    /// <summary>Raised when a sweep can start or end (grid kind or running sweep changed).</summary>
    public event Action? CanChange;

    public ObservableCollection<GridKind> GridKinds { get; } = [];

    [ObservableProperty]
    public partial GridKind? SelectedGridKind { get; set; }

    partial void OnSelectedGridKindChanged(GridKind? value)
    {
        if (value is not null)
            GridKindChosen?.Invoke(value);
        CanChange?.Invoke();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSweep), nameof(SweepText), nameof(SweepProgressText))]
    public partial ActiveSweepInfo? Sweep { get; set; }

    partial void OnSweepChanged(ActiveSweepInfo? value) => CanChange?.Invoke();

    [ObservableProperty]
    public partial string HotkeyHint { get; set; } = "";

    public bool HasSweep => Sweep is not null;
    public bool CanStart => SelectedGridKind is not null && BeginSweep is not null;

    public string SweepText => Sweep is { } s ? $"Sweep: {s.Label} · {s.ItemCount} item(s)" : "";

    public string SweepProgressText => Sweep is { } s
        ? $"{s.ItemCount} item(s) · {s.Cells.Count(c => c.State != CellState.Pending)} / {s.Kind.Columns * s.Kind.Rows} cells marked"
        : "";

    /// <summary>Replaces the grid kinds, keeping the selection by name when possible.</summary>
    public void SetGridKinds(IReadOnlyList<GridKind> kinds, string? select)
    {
        var name = SelectedGridKind?.Name ?? select;
        GridKinds.Clear();
        foreach (var k in kinds)
            GridKinds.Add(k);
        SelectedGridKind = GridKinds.FirstOrDefault(k => k.Name == name) ?? GridKinds.FirstOrDefault();
    }

    public async Task Refresh() => Sweep = (await _controller.GetActive())?.Sweep;

    /// <summary>A mapping scan sweeps the inventory: selects the area whose name contains "Inventory", if any.</summary>
    public GridKind? SelectInventory()
    {
        if (GridKinds.FirstOrDefault(k => k.Name.Contains("Inventory", StringComparison.OrdinalIgnoreCase)) is { } inventory)
            SelectedGridKind = inventory;
        return SelectedGridKind;
    }

    public Task Start() => SelectedGridKind is { } kind && BeginSweep is { } begin ? begin(kind) : Task.CompletedTask;

    public Task Finish() => _controller.FinishSweep();

    /// <summary>Asks first (message, confirm button label); the confirm text says how much the cancel withdraws.</summary>
    public async Task Cancel(Func<string, string, Task<bool>> confirm)
    {
        if (Sweep is not { } sweep)
            return;
        var what = sweep.ChangeCount == 0
            ? "Nothing was recorded during it."
            : $"This withdraws all {sweep.ChangeCount} change(s) made during it (captures inside and outside the grid, removals). Undo brings them back.";
        if (await confirm($"Cancel sweep '{sweep.Label}'? {what}", "Discard sweep"))
            await _controller.CancelSweep();
    }
}
