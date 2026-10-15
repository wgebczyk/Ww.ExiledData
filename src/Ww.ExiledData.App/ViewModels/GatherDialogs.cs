using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.App.ViewModels;

/// <summary>
/// The recall dialog: sets up a run from earlier copies in the session. One
/// waystone (or keep the current one), and up to four tablets; "Use previous run's tablets" checks those.
/// </summary>
public sealed partial class RecallDialogViewModel : ObservableObject
{
    public RecallDialogViewModel(int runNumber, IReadOnlyList<RecallItem> waystones, IReadOnlyList<RecallItem> tablets, string? currentWaystone,
        IReadOnlyList<string> currentTablets, IReadOnlyList<string> previousTablets, bool startOnTablets)
    {
        RunNumber = runNumber;
        _previousTablets = previousTablets;
        foreach (var w in waystones)
            Waystones.Add(new RecallRowViewModel(w, w.RawText == currentWaystone));
        foreach (var t in tablets)
        {
            var row = new RecallRowViewModel(t, currentTablets.Contains(t.RawText));
            row.PropertyChanged += (_, _) => OnTabletsChanged();
            Tablets.Add(row);
        }
        SelectedWaystone = Waystones.FirstOrDefault(w => w.IsChecked);
        _initialWaystone = SelectedWaystone;
        IsTabletsTab = startOnTablets;
    }

    private readonly IReadOnlyList<string> _previousTablets;
    private readonly RecallRowViewModel? _initialWaystone;

    public int RunNumber { get; }
    public string Title => $"Set up run {RunNumber}";
    public string AssignText => $"Assign to run {RunNumber}";

    public ObservableCollection<RecallRowViewModel> Waystones { get; } = [];
    public ObservableCollection<RecallRowViewModel> Tablets { get; } = [];
    public bool HasNoWaystones => Waystones.Count == 0;
    public bool HasNoTablets => Tablets.Count == 0;

    [ObservableProperty]
    public partial RecallRowViewModel? SelectedWaystone { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaystoneTab))]
    public partial bool IsTabletsTab { get; set; }

    public bool IsWaystoneTab { get => !IsTabletsTab; set => IsTabletsTab = !value; }

    public int CheckedTablets => Tablets.Count(t => t.IsChecked);
    public string TabletCountText => $"{CheckedTablets} of {MapSession.MaxTablets}";
    public bool TooManyTablets => CheckedTablets > MapSession.MaxTablets;
    public bool CanAssign => !TooManyTablets;
    public bool HasPreviousTablets => _previousTablets.Count > 0;

    private void OnTabletsChanged()
    {
        OnPropertyChanged(nameof(CheckedTablets));
        OnPropertyChanged(nameof(TabletCountText));
        OnPropertyChanged(nameof(TooManyTablets));
        OnPropertyChanged(nameof(CanAssign));
    }

    /// <summary>Checks the previous run's tablets (the ones listed), keeping others checked while slots are free.</summary>
    [RelayCommand]
    private void UsePreviousTablets()
    {
        foreach (var row in Tablets.Where(t => _previousTablets.Contains(t.Item.RawText)))
            row.IsChecked = true;
        IsTabletsTab = true;
    }

    /// <summary>The picked waystone; null keeps the run's waystone (nothing picked, or the same one).</summary>
    public RecallItem? ChosenWaystone => SelectedWaystone is { } w && w != _initialWaystone ? w.Item : null;

    public IReadOnlyList<RecallItem> ChosenTablets => Tablets.Where(t => t.IsChecked).Select(t => t.Item).ToList();
}

/// <summary>What the user chose in the review dialog.</summary>
public enum ReviewChoice
{
    Apply,
    Dismiss,
    /// <summary>Show the warning of a dismissed check again.</summary>
    Restore,
}

/// <summary>
/// The Final-vs-Initial review (MAPPING_PLAN.md 3.3): per currency, the Initial and Final counts and the proposed
/// adjustment, which the user applies (possibly edited) or dismisses. Nothing changes without the user's choice.
/// </summary>
public sealed class ReviewDialogViewModel
{
    public ReviewDialogViewModel(int runNumber, FinalCheck check, Func<string, string> codeOf)
    {
        Title = $"Review run {runNumber}";
        Text = check.Initial is null
            ? "There is no Initial swipe, so the Final scan is compared with an empty inventory."
            : "The Final scan should match the Initial swipe. A difference is usually loot that was never stashed (more at the end) or currency that was spent (less).";
        IsDismissed = check.Final.CheckDismissed;
        foreach (var d in check.Differences)
            Rows.Add(new ReviewRowViewModel(d, codeOf(d.Name)));
    }

    public string Title { get; }
    public string Text { get; }
    public bool IsDismissed { get; }
    public bool IsNotDismissed => !IsDismissed;
    public ObservableCollection<ReviewRowViewModel> Rows { get; } = [];

    /// <summary>The adjustments to apply: every row with a non-zero amount.</summary>
    public IReadOnlyList<(string Key, string Name, int Amount)> Adjustments() =>
        Rows.Where(r => r.Amount is { } a && a != 0).Select(r => (r.Key, r.Name, (int)r.Amount!.Value)).ToList();
}
