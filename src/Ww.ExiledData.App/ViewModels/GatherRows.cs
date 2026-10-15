using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.App.ViewModels;

/// <summary>
/// One cell of the condensed loot grid. The grid's cells are built once from the layout
/// and only their counts change, so the cell control can highlight a changed count. A placeholder is an empty place of
/// the grid: it takes its column but nothing is drawn.
/// </summary>
public sealed partial class CurrencyCellViewModel(CurrencyDisplay? display) : ObservableObject
{
    public string? Name => display?.Name;
    public bool IsPlaceholder => display is null;
    public string Code => display?.Code ?? "···";
    public string Letter => display?.Code[..1] ?? "";
    /// <summary>The icon path below the icons folder (catalog), or null.</summary>
    public string? Image => display?.Image;
    public string? Badge => display?.Badge;
    public string? Tip => display is null ? null : $"{display.Name}: {Count}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZero), nameof(CountText), nameof(Tip))]
    public partial int Count { get; set; }

    /// <summary>Shown instead of <see cref="Count"/> when set (an average in the Analyze tab).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial string? DisplayText { get; set; }

    public bool IsZero => !IsPlaceholder && Count == 0;
    /// <summary>The count, or a empty string for zero (the tooltip still says 0).</summary>
    public string CountText => IsPlaceholder ? "" : DisplayText ?? (Count == 0 ? "" : Count.ToString(CultureInfo.InvariantCulture));

    /// <summary>The cells of the book's loot grid, row by row (nine per row).</summary>
    public static IReadOnlyList<LootRowViewModel> RowsOf(CurrencyBook book) =>
        book.Layout.Rows
            .Select(r => new LootRowViewModel(r.Select(n => new CurrencyCellViewModel(n is null ? null : book.Describe(n))).ToList()))
            .ToList();
}

/// <summary>A row of the condensed loot grid: nine cells, shown as three groups of three.</summary>
public sealed record LootRowViewModel(IReadOnlyList<CurrencyCellViewModel> Cells)
{
    public const int GroupSize = 3;

    public IReadOnlyList<IReadOnlyList<CurrencyCellViewModel>> Groups { get; } = Cells.Chunk(GroupSize).ToList();
}

/// <summary>A row of the loot list view (and of "all currencies").</summary>
public sealed record LootListRow(string Code, string? Badge, string Name, int Count, bool InGrid, string? Image = null, CurrencyValue? Value = null,
    PriceUnit Unit = PriceUnit.Adaptive)
{
    public string CountText => Count.ToString(CultureInfo.InvariantCulture);
    /// <summary>The whole count's value (poe.ninja) in the unit of the settings; a dash when it has no price.</summary>
    public string AmountText => Format.Value(Value, Unit).Amount;
    /// <summary>The unit an adaptive value landed in (<c>DIV</c>, <c>ChO</c>, <c>EX</c>); empty for a fixed unit.</summary>
    public string UnitText => Format.Value(Value, Unit).Unit;
    public bool ShowsUnit => Unit == PriceUnit.Adaptive;
}

/// <summary>
/// One scan of the shown run: kind chip, name, time since the run started, a short summary.
/// The kind menu corrects the kind; a Final scan that differs from the Initial swipe has a warning and Review.
/// </summary>
public sealed class ScanRowViewModel
{
    public ScanRowViewModel(Guid scanId, ScanKind kind, string name, string timeText, string summary, bool isActive, bool isInterrupted,
        bool hasWarning, Func<Guid, ScanKind, Task>? setKind, Func<Guid, Task>? review, Func<Guid, Task>? remove)
    {
        ScanId = scanId;
        Kind = kind;
        Name = name;
        TimeText = timeText;
        Summary = summary;
        IsActive = isActive;
        IsInterrupted = isInterrupted;
        HasWarning = hasWarning;
        if (setKind is not null)
        {
            SetSwipeCommand = new AsyncRelayCommand(() => setKind(scanId, ScanKind.Swipe));
            SetStashCommand = new AsyncRelayCommand(() => setKind(scanId, ScanKind.Stash));
            SetFinalCommand = new AsyncRelayCommand(() => setKind(scanId, ScanKind.Final));
        }
        if (review is not null)
            ReviewCommand = new AsyncRelayCommand(() => review(scanId));
        if (remove is not null)
            RemoveCommand = new AsyncRelayCommand(() => remove(scanId));
    }

    public Guid ScanId { get; }
    public ScanKind Kind { get; }
    public string KindText => Kind switch { ScanKind.Stash => "Stash", ScanKind.Final => "Final", _ => "Swipe" };
    public bool IsSwipe => Kind == ScanKind.Swipe;
    public bool IsStash => Kind == ScanKind.Stash;
    public bool IsFinal => Kind == ScanKind.Final;
    /// <summary>"Initial", "Swipe", "Stash 2", "Final".</summary>
    public string Name { get; }
    public string TimeText { get; }
    public string Summary { get; }
    /// <summary>The scan is running.</summary>
    public bool IsActive { get; }
    public bool IsInterrupted { get; }
    /// <summary>Swipes never count: shown muted.</summary>
    public double RowOpacity => IsSwipe ? 0.8 : 1;
    /// <summary>A Final scan that differs from the Initial swipe (neither applied nor dismissed).</summary>
    public bool HasWarning { get; }
    public bool CanEditKind => SetSwipeCommand is not null;
    public string KindTip => CanEditKind ? $"{KindText}: click to change the kind or remove the scan (the loot is recomputed)" : KindText;

    public IAsyncRelayCommand? SetSwipeCommand { get; }
    public IAsyncRelayCommand? SetStashCommand { get; }
    public IAsyncRelayCommand? SetFinalCommand { get; }
    public IAsyncRelayCommand? ReviewCommand { get; }
    /// <summary>Removes the ended scan (undoable); an Initial or a Final asks first.</summary>
    public IAsyncRelayCommand? RemoveCommand { get; }
}

/// <summary>
/// One of the four tablet slots. A possible tablet can be confirmed or rejected; a filled slot can be cleared; an empty
/// one opens the recall list.
/// </summary>
public sealed class TabletSlotViewModel
{
    public TabletSlotViewModel(SetupItem? item, Func<Guid, SetupItemKind, Task>? setKind, Func<Guid, Task>? remove, Func<Task>? pick)
    {
        Label = item?.Label ?? "empty · pick from earlier";
        IsEmpty = item is null;
        IsPossible = item?.Kind == SetupItemKind.PossibleTablet;
        RecalledText = item?.RecalledFromRun is { } run ? $"from run {run} · one use fewer expected" : null;
        if (item is not null && setKind is not null)
        {
            ConfirmCommand = new AsyncRelayCommand(() => setKind(item.Id, SetupItemKind.Tablet));
            RejectCommand = new AsyncRelayCommand(() => setKind(item.Id, SetupItemKind.NotSetup));
            PossibleCommand = new AsyncRelayCommand(() => setKind(item.Id, SetupItemKind.PossibleTablet));
        }
        if (item is not null && remove is not null)
            RemoveCommand = new AsyncRelayCommand(() => remove(item.Id));
        if (item is null && pick is not null)
            PickCommand = new AsyncRelayCommand(pick);
    }

    public string Label { get; }
    public bool IsEmpty { get; }
    public bool IsFilled => !IsEmpty;
    /// <summary>An unknown item copied in the setup phase: maybe a tablet.</summary>
    public bool IsPossible { get; }
    /// <summary>Recalled from an earlier run ("from run 23 · one use fewer expected"); null when copied in this setup.</summary>
    public string? RecalledText { get; }
    public bool IsRecalled => RecalledText is not null;
    public string Tip => IsPossible ? "Possible tablet (unknown item class): confirm or reject it"
        : IsEmpty ? "Pick a tablet from earlier scans" : RecalledText ?? "Tablet; click to correct";

    public IAsyncRelayCommand? ConfirmCommand { get; }
    public IAsyncRelayCommand? RejectCommand { get; }
    public IAsyncRelayCommand? PossibleCommand { get; }
    public IAsyncRelayCommand? RemoveCommand { get; }
    public IAsyncRelayCommand? PickCommand { get; }
}

/// <summary>A tag chip with its remove command.</summary>
public sealed class TagChipViewModel(MapTag tag, Func<MapTag, Task> remove)
{
    public MapTag Tag => tag;
    public string Text => tag.ToString();
    public IAsyncRelayCommand RemoveCommand { get; } = new AsyncRelayCommand(() => remove(tag));
}

/// <summary>A currency of the scan summary: its count, or for a Final scan its difference from the Initial (▲ / ▼, never color alone).</summary>
public sealed record ScanDeltaRow(string Code, string? Badge, string Name, int Value, bool IsDifference)
{
    public bool IsDown => IsDifference && Value < 0;
    public string ValueText => !IsDifference ? Value.ToString(CultureInfo.InvariantCulture) : Value > 0 ? $"▲ +{Value}" : $"▼ {Value}";
}

/// <summary>One currency of the Final-vs-Initial review: the counts and the adjustment, editable before applying.</summary>
public sealed partial class ReviewRowViewModel(CheckDifference difference, string code) : ObservableObject
{
    public string Key => difference.Key;
    public string Name => difference.Name;
    public string Code => code;
    public string InitialText => difference.Initial.ToString(CultureInfo.InvariantCulture);
    public string FinalText => difference.Final.ToString(CultureInfo.InvariantCulture);
    public string AppliedText => difference.Applied == 0 ? "" : $"{difference.Applied:+0;-0} applied";

    /// <summary>The adjustment to apply: the proposal, edited by the user.</summary>
    [ObservableProperty]
    public partial decimal? Amount { get; set; } = difference.Proposed;
}

/// <summary>One row of the run history table.</summary>
/// <param name="ScansText">"4 scans · Final matches", "6 scans · ⚠ Final differs · Review".</param>
public sealed record RunRowViewModel(Guid Id, int Number, string DurationText, string SetupText, string ScansText, bool HasWarning,
    string TopText, string TagsText, bool IsCurrent)
{
    public string NumberText => Number.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A waystone or tablet of the recall dialog (GatherRecall): an earlier copy in the session.</summary>
public sealed partial class RecallRowViewModel(RecallItem item, bool isChecked) : ObservableObject
{
    public RecallItem Item => item;
    public string Label => item.Label;
    public string RunText => $"run {item.RunNumber}";
    public string UsesText => item.UsesLeft is { } uses ? $"{uses} use{(uses == 1 ? "" : "s")} left" : "";

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;
}
