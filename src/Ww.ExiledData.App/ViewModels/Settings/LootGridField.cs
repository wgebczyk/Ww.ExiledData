using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.App.ViewModels.Settings;

/// <summary>A currency the loot grid editor offers: from the catalog, or a name typed in the picker.</summary>
public sealed record LootChoice(string Name, string Code, string? Badge, string? Image)
{
    public static LootChoice Of(CurrencyBook book, string name)
    {
        var display = book.Describe(name);
        return new LootChoice(name, display.Code, display.Badge, display.Image);
    }
}

/// <summary>
/// The condensed loot grid of mapping sessions (<see cref="AppSettings.LootGrid"/>): rows of nine places, each a
/// currency or empty. There is always one empty row at the end: picking a currency in it adds a new one, and emptying
/// the row above it removes it again. Empty rows in between stay (a gap in the grid).
/// </summary>
public sealed partial class LootGridField : SettingField
{
    private const int MaxMatches = 80;
    private readonly LootLayout _saved;
    private readonly LootLayout _default;
    private readonly CurrencyBook _book;
    private bool _loading;

    public LootGridField(LootLayout saved, LootLayout @default, CurrencyBook book)
        : base("Loot grid", "The currencies of the condensed loot grid in the Gather and Analyse tabs. Empty places are left blank.")
    {
        _saved = saved;
        _default = @default;
        _book = book;
        Choices = book.Catalog.Entries.Select(e => LootChoice.Of(book, e.Name)).ToList();
        Load(saved);
    }

    public ObservableCollection<LootGridRow> Rows { get; } = [];

    /// <summary>Every currency of the catalog, in catalog order.</summary>
    public IReadOnlyList<LootChoice> Choices { get; }

    /// <summary>The edited layout (empty rows at the end dropped).</summary>
    public LootLayout Layout => LootLayout.From(Rows.Select(r => r.Cells.Select(c => c.Name)));

    public override bool IsChanged => !Layout.SameAs(_saved);
    public override bool IsDefault => Layout.SameAs(_default);
    protected override string DefaultText => "the default grid";

    /// <summary>The stored value: rows of nine names or nulls.</summary>
    public IReadOnlyList<IReadOnlyList<string?>> Value => Layout.Rows;

    protected override void ResetToDefault() => Load(_default);

    internal LootChoice Describe(string name) => LootChoice.Of(_book, name);

    internal IEnumerable<LootChoice> Match(string filter)
    {
        var text = filter.Trim();
        var matches = text.Length == 0
            ? (IEnumerable<LootChoice>)Choices
            : Choices.Where(c => c.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || c.Code.Contains(text, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        return matches.Take(MaxMatches);
    }

    internal void Remove(LootGridRow row)
    {
        if (row == Rows[^1])
            return;
        Rows.Remove(row);
        Changed();
    }

    internal void Changed()
    {
        if (_loading)
            return;
        KeepOneEmptyRowAtTheEnd();
        RaiseEdited();
    }

    private void Load(LootLayout layout)
    {
        _loading = true;
        Rows.Clear();
        foreach (var row in layout.Rows)
            Rows.Add(new LootGridRow(this, row));
        _loading = false;
        Changed();
    }

    private void KeepOneEmptyRowAtTheEnd()
    {
        while (Rows.Count >= 2 && Rows[^1].IsEmpty && Rows[^2].IsEmpty)
            Rows.RemoveAt(Rows.Count - 1);
        if (Rows.Count == 0 || !Rows[^1].IsEmpty)
            Rows.Add(new LootGridRow(this, new string?[LootLayout.Columns]));
        foreach (var row in Rows)
            row.CanRemove = row != Rows[^1];
    }
}

/// <summary>A row of nine places of the loot grid editor.</summary>
public sealed partial class LootGridRow : ObservableObject
{
    public LootGridRow(LootGridField owner, IReadOnlyList<string?> names)
    {
        Owner = owner;
        Cells = Enumerable.Range(0, LootLayout.Columns).Select(i => new LootGridCell(this, i < names.Count ? names[i] : null)).ToList();
        Groups = Cells.Chunk(LootRowViewModel.GroupSize).ToList();
    }

    public LootGridField Owner { get; }
    public IReadOnlyList<LootGridCell> Cells { get; }
    /// <summary>The cells in three groups of three, shown with a gap between them.</summary>
    public IReadOnlyList<LootGridCell[]> Groups { get; }
    public bool IsEmpty => Cells.All(c => c.IsEmpty);

    /// <summary>The empty row at the end cannot be removed.</summary>
    [ObservableProperty]
    public partial bool CanRemove { get; set; }

    [RelayCommand]
    private void Remove() => Owner.Remove(this);
}

/// <summary>A place of the loot grid editor: a currency or empty; a click opens the picker.</summary>
public sealed partial class LootGridCell : ObservableObject
{
    private readonly LootGridRow _row;

    public LootGridCell(LootGridRow row, string? name)
    {
        _row = row;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(IsFilled), nameof(Choice), nameof(Tip))]
    public partial string? Name { get; set; }

    partial void OnNameChanged(string? value) => _row.Owner.Changed();

    public bool IsEmpty => Name is null;
    public bool IsFilled => !IsEmpty;
    public LootChoice? Choice => Name is null ? null : _row.Owner.Describe(Name);
    public string Tip => Name ?? "Empty: click to choose a currency";

    [ObservableProperty]
    public partial bool IsPickerOpen { get; set; }

    partial void OnIsPickerOpenChanged(bool value)
    {
        if (value)
        {
            Filter = "";
            RefreshMatches();
        }
    }

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => RefreshMatches();

    public ObservableCollection<LootMatch> Matches { get; } = [];

    /// <summary>"Use “…”" for a typed name the catalog does not know.</summary>
    public bool CanUseTyped => Filter.Trim().Length > 0 && !_row.Owner.Choices.Any(c => string.Equals(c.Name, Filter.Trim(), StringComparison.OrdinalIgnoreCase));
    public string UseTypedText => $"Use “{Filter.Trim()}”";

    [RelayCommand]
    private void OpenPicker() => IsPickerOpen = true;

    [RelayCommand]
    private void ClosePicker() => IsPickerOpen = false;

    [RelayCommand]
    private void Clear() => Pick(null);

    [RelayCommand]
    private void UseTyped() => Pick(Filter.Trim());

    /// <summary>Enter in the filter: the first match, else the typed name.</summary>
    [RelayCommand]
    private void PickFirst()
    {
        if (Matches.FirstOrDefault() is { } first)
            Pick(first.Choice.Name);
        else if (CanUseTyped)
            UseTyped();
    }

    internal void Pick(string? name)
    {
        Name = string.IsNullOrWhiteSpace(name) ? null : name;
        IsPickerOpen = false;
    }

    private void RefreshMatches()
    {
        Matches.Clear();
        foreach (var choice in _row.Owner.Match(Filter))
            Matches.Add(new LootMatch(this, choice));
        OnPropertyChanged(nameof(CanUseTyped));
        OnPropertyChanged(nameof(UseTypedText));
    }
}

/// <summary>A currency of the picker list.</summary>
public sealed class LootMatch(LootGridCell cell, LootChoice choice)
{
    public LootChoice Choice => choice;
    public IRelayCommand PickCommand { get; } = new RelayCommand(() => cell.Pick(choice.Name));
}
