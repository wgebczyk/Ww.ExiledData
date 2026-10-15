using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ww.ExiledData.Capture;
using Ww.ExiledData.Core.Sweeps;

namespace Ww.ExiledData.App.ViewModels.Settings;

/// <summary>
/// One editable setting: its draft value, the saved value and the default.
/// <see cref="IsChanged"/> (differs from the saved value) drives the amber dot, border and the unsaved count;
/// <see cref="CanReset"/> (differs from the default) shows the "Reset to" link.
/// </summary>
public abstract partial class SettingField(string label, string description = "", string applyNote = "") : ObservableObject
{
    public string Label { get; } = label;
    public string Description { get; } = description;
    /// <summary>"↻ Restarts capture when saved" and similar; empty when the change is instant.</summary>
    public string ApplyNote { get; } = applyNote;
    public bool HasApplyNote => ApplyNote.Length > 0;

    public abstract bool IsChanged { get; }
    public abstract bool IsDefault { get; }
    protected abstract string DefaultText { get; }

    public bool CanReset => !IsDefault;
    public string ResetText => $"↺ Reset to {DefaultText}";

    /// <summary>Blocks saving. Full sentence with the "Error:" prefix.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    partial void OnErrorChanged(string? value) => ErrorChanged();

    protected virtual void ErrorChanged()
    {
    }

    /// <summary>Raised after every edit of the draft value; the dialog revalidates and recounts.</summary>
    public event Action<SettingField>? Edited;

    protected void RaiseEdited()
    {
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(CanReset));
        Edited?.Invoke(this);
    }

    [RelayCommand]
    public void Reset() => ResetToDefault();

    protected abstract void ResetToDefault();
}

/// <summary>Whole number with a range and a unit, edited with the stepper (spec 4.2). An empty or out-of-range box is invalid.</summary>
public sealed partial class NumberField : SettingField
{
    public NumberField(string label, string description, string applyNote, int saved, int @default, int min, int max, int step, string unit,
        string decreaseName = "Decrease", string increaseName = "Increase")
        : base(label, description, applyNote)
    {
        Saved = saved;
        Default = @default;
        Min = min;
        Max = max;
        Step = step;
        Unit = unit;
        DecreaseName = decreaseName;
        IncreaseName = increaseName;
        Text = Format(saved);
    }

    public int Saved { get; }
    public int Default { get; }
    public int Min { get; }
    public int Max { get; }
    public int Step { get; }
    public string Unit { get; }
    public string DecreaseName { get; }
    public string IncreaseName { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    partial void OnTextChanged(string value)
    {
        Error = Value is null ? $"Error: must be {Min} to {Max}{UnitSuffix}." : null;
        DecreaseCommand.NotifyCanExecuteChanged();
        IncreaseCommand.NotifyCanExecuteChanged();
        RaiseEdited();
    }

    /// <summary>The number when the text is a whole number in range, else null.</summary>
    public int? Value => int.TryParse(Text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) && v >= Min && v <= Max
        ? v
        : null;

    /// <summary>The number typed, in range or not (for the steppers).</summary>
    private int? Typed => int.TryParse(Text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    public override bool IsChanged => Value != Saved;
    public override bool IsDefault => Value == Default;
    protected override string DefaultText => Format(Default);
    public string HelperText => $"{Min} to {Max}{UnitSuffix} · default {Default}{UnitSuffix}";
    private string UnitSuffix => Unit.Length > 0 ? $" {Unit}" : "";

    protected override void ResetToDefault() => Text = Format(Default);

    private bool CanDecrease() => Typed is not { } v || v > Min;
    private bool CanIncrease() => Typed is not { } v || v < Max;

    // A step from an out-of-range or empty box lands on the nearest end of the range.
    [RelayCommand(CanExecute = nameof(CanDecrease))]
    private void Decrease() => Text = Format(Typed is { } v ? Math.Clamp(v - Step, Min, Max) : Default);

    [RelayCommand(CanExecute = nameof(CanIncrease))]
    private void Increase() => Text = Format(Typed is { } v ? Math.Clamp(v + Step, Min, Max) : Default);

    private static string Format(int v) => v.ToString(CultureInfo.InvariantCulture);
}

public sealed partial class TextField : SettingField
{
    public TextField(string label, string description, string applyNote, string saved, string @default, string helper)
        : base(label, description, applyNote)
    {
        Saved = saved;
        Default = @default;
        HelperText = helper;
        Value = saved;
    }

    public string Saved { get; }
    public string Default { get; }
    public string HelperText { get; }

    [ObservableProperty]
    public partial string Value { get; set; }

    partial void OnValueChanged(string value) => RaiseEdited();

    public override bool IsChanged => Value.Trim() != Saved;
    public override bool IsDefault => Value.Trim() == Default;
    protected override string DefaultText => Default.Length > 0 ? $"“{Default}”" : "empty";
    protected override void ResetToDefault() => Value = Default;
}

/// <summary>One of a fixed set of values (theme, placement, log level), shown with readable labels.</summary>
public sealed partial class ChoiceField : SettingField
{
    public ChoiceField(string label, string description, IEnumerable<(object Value, string Label)> options, object saved, object @default)
        : base(label, description)
    {
        Options = options.Select(o => new ChoiceOption(this, o.Value, o.Label)).ToList();
        Saved = saved;
        Default = @default;
        Selected = Options.First(o => o.Value.Equals(saved));
    }

    public IReadOnlyList<ChoiceOption> Options { get; }
    public object Saved { get; }
    public object Default { get; }

    [ObservableProperty]
    public partial ChoiceOption Selected { get; set; }

    partial void OnSelectedChanged(ChoiceOption value)
    {
        foreach (var o in Options)
            o.Refresh();
        RaiseEdited();
    }

    public object Value => Selected.Value;
    public ChoiceOption Option(object value) => Options.First(o => o.Value.Equals(value));

    public override bool IsChanged => !Value.Equals(Saved);
    public override bool IsDefault => Value.Equals(Default);
    protected override string DefaultText => Option(Default).Label;
    protected override void ResetToDefault() => Selected = Option(Default);
}

/// <summary>An option of a <see cref="ChoiceField"/>; <see cref="IsSelected"/> is the two-way target of a radio button.</summary>
public sealed class ChoiceOption(ChoiceField owner, object value, string label) : ObservableObject
{
    public object Value { get; } = value;
    public string Label { get; } = label;

    public bool IsSelected
    {
        get => owner.Selected == this;
        set
        {
            if (value)
                owner.Selected = this;
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
    public override string ToString() => Label;
}

public enum MessageKind { None, Note, Warning, Error }

/// <summary>A global hotkey, recorded by pressing it or typed (spec 4.3). Stored as the canonical text (<c>Ctrl+Alt+Z</c>).</summary>
public sealed partial class HotkeyField : SettingField
{
    private static long s_editCounter;

    public HotkeyField(HotkeyAction action, string label, string description, string saved, string @default)
        : base(label, description)
    {
        Action = action;
        Saved = saved;
        Default = @default;
        Text = saved;
    }

    public HotkeyAction Action { get; }
    public string Saved { get; }
    public string Default { get; }

    /// <summary>When it was last edited; of two equal hotkeys the later edit carries the error.</summary>
    public long EditStamp { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Keys), nameof(Parsed))]
    public partial string Text { get; set; }

    partial void OnTextChanged(string value)
    {
        EditStamp = Interlocked.Increment(ref s_editCounter);
        RaiseEdited();
    }

    public Hotkey? Parsed => Hotkey.TryParse(Text, out var k) ? k : null;

    /// <summary>Keycaps: the parsed parts, or the typed text split on '+'.</summary>
    public IReadOnlyList<string> Keys => (Parsed?.ToString() ?? Text).Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordButtonText), nameof(ShowKeys), nameof(ShowTextBox))]
    public partial bool IsRecording { get; set; }

    partial void OnIsRecordingChanged(bool value) => RecordingChanged?.Invoke(this, value);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowKeys), nameof(ShowTextBox))]
    public partial bool IsTyping { get; set; }

    public bool ShowKeys => !IsTyping && !IsRecording;
    public bool ShowTextBox => IsTyping && !IsRecording;
    public string RecordButtonText => IsRecording ? "Cancel" : "Record";

    /// <summary>Raised when recording starts or stops (the host suspends the global hotkeys meanwhile).</summary>
    public event Action<HotkeyField, bool>? RecordingChanged;

    // Message under the control: an error blocks saving; a warning or note does not.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message), nameof(MessageKind), nameof(HasMessage), nameof(IsWarning), nameof(IsNote))]
    public partial string? Warning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message), nameof(MessageKind), nameof(HasMessage), nameof(IsWarning), nameof(IsNote))]
    public partial string? Note { get; set; }

    protected override void ErrorChanged()
    {
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(MessageKind));
        OnPropertyChanged(nameof(HasMessage));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsNote));
    }

    public string? Message => Error ?? Warning ?? Note;
    public MessageKind MessageKind => Error is not null ? MessageKind.Error : Warning is not null ? MessageKind.Warning : Note is not null ? MessageKind.Note : MessageKind.None;
    public bool HasMessage => Message is not null;
    public bool IsWarning => MessageKind == MessageKind.Warning;
    public bool IsNote => MessageKind == MessageKind.Note;

    public override bool IsChanged => Text.Trim() != Saved;
    public override bool IsDefault => Text.Trim() == Default;
    protected override string DefaultText => Default;

    protected override void ResetToDefault()
    {
        IsRecording = false;
        Text = Default;
    }

    /// <summary>Record button: starts recording, or cancels it.</summary>
    [RelayCommand]
    private void ToggleRecording()
    {
        IsTyping = false;
        IsRecording = !IsRecording;
    }

    [RelayCommand]
    private void TypeInstead()
    {
        IsRecording = false;
        IsTyping = true;
    }

    /// <summary>The recorder saw a complete combination.</summary>
    public void CompleteRecording(Hotkey hotkey)
    {
        IsRecording = false;
        Text = hotkey.ToString();
    }

    public void CancelRecording() => IsRecording = false;
}

/// <summary>One sweep area row (spec 4.4).</summary>
public sealed partial class AreaRow : ObservableObject
{
    public AreaRow(AreasField owner, string name, int columns, int rows)
    {
        Owner = owner;
        Columns = new NumberField("Columns", "", "", columns, columns, 1, GridKind.MaxCells, 1, "", "Fewer columns", "More columns");
        Rows = new NumberField("Rows", "", "", rows, rows, 1, GridKind.MaxCells, 1, "", "Fewer rows", "More rows");
        Name = name;
        Columns.Edited += _ => Changed();
        Rows.Edited += _ => Changed();
    }

    public AreasField Owner { get; }
    public NumberField Columns { get; }
    public NumberField Rows { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    partial void OnNameChanged(string value) => Changed();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool NameInvalid { get; set; }

    public bool HasError => Error is not null;

    /// <summary>Preview size; 0 hides it while a size is invalid.</summary>
    public int PreviewColumns => Columns.Value ?? 0;
    public int PreviewRows => Rows.Value ?? 0;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove() => Owner.Remove(this);

    private bool CanRemove() => Owner.Rows.Count > 1;
    internal void RefreshCanRemove() => RemoveCommand.NotifyCanExecuteChanged();

    private void Changed()
    {
        OnPropertyChanged(nameof(PreviewColumns));
        OnPropertyChanged(nameof(PreviewRows));
        Owner.RowChanged();
    }
}

/// <summary>The sweep areas as an editable list; stored as the <c>Name=CxR; ...</c> text.</summary>
public sealed partial class AreasField : SettingField
{
    private readonly IReadOnlyList<GridKind> _saved;
    private readonly IReadOnlyList<GridKind> _default;
    private bool _loading;

    public AreasField(string saved, string @default) : base("Areas")
    {
        _saved = GridKind.TryParseList(saved, out var s, out _) ? s : GridKind.Presets;
        _default = GridKind.TryParseList(@default, out var d, out _) ? d : GridKind.Presets;
        Load(_saved);
    }

    public ObservableCollection<AreaRow> Rows { get; } = [];

    /// <summary>"At least one area is needed." (under the list).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasListError))]
    public partial string? ListError { get; set; }

    public bool HasListError => ListError is not null;

    /// <summary>Row errors plus the list error.</summary>
    public int ProblemCount => Rows.Count(r => r.HasError) + (HasListError ? 1 : 0);

    public override bool IsChanged => !Same(_saved);
    public override bool IsDefault => Same(_default);
    protected override string DefaultText => "the three presets";

    /// <summary>The stored text; only meaningful while there are no problems.</summary>
    public string Text => GridKind.FormatList(Rows.Select(r => new GridKind(r.Name.Trim(), r.Columns.Value ?? 0, r.Rows.Value ?? 0)).ToList());

    protected override void ResetToDefault() => Load(_default);

    [RelayCommand]
    private void Add()
    {
        var n = 1;
        while (Rows.Any(r => string.Equals(r.Name.Trim(), $"Area {n}", StringComparison.OrdinalIgnoreCase)))
            n++;
        Rows.Add(new AreaRow(this, $"Area {n}", 12, 12));
        RowChanged();
    }

    internal void Remove(AreaRow row)
    {
        if (Rows.Count <= 1)
            return;
        Rows.Remove(row);
        RowChanged();
    }

    internal void RowChanged()
    {
        if (_loading)
            return;
        Validate();
        foreach (var r in Rows)
            r.RefreshCanRemove();
        OnPropertyChanged(nameof(ProblemCount));
        RaiseEdited();
    }

    private void Load(IReadOnlyList<GridKind> kinds)
    {
        _loading = true;
        Rows.Clear();
        foreach (var k in kinds)
            Rows.Add(new AreaRow(this, k.Name, k.Columns, k.Rows));
        _loading = false;
        RowChanged();
    }

    private bool Same(IReadOnlyList<GridKind> kinds) =>
        Rows.Count == kinds.Count
        && Rows.Zip(kinds).All(p => p.First.Name.Trim() == p.Second.Name && p.First.Columns.Value == p.Second.Columns && p.First.Rows.Value == p.Second.Rows);

    private void Validate()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
        {
            var name = row.Name.Trim();
            string? error = null;
            if (name.Length == 0)
                error = "Error: the name is empty.";
            else if (name.IndexOfAny(['=', ';']) >= 0)
                error = "Error: names cannot contain = or ;.";
            else if (!seen.Add(name))
                error = $"Error: “{name}” is listed twice. Names must be unique, ignoring case.";
            row.NameInvalid = error is not null;

            if (error is null && row.Columns.Value is null)
                error = $"Error: columns must be 1 to {GridKind.MaxCells}.";
            if (error is null && row.Rows.Value is null)
                error = $"Error: rows must be 1 to {GridKind.MaxCells}.";
            row.Error = error;
        }
        ListError = Rows.Count == 0 ? "Error: at least one area is needed." : null;
        Error = ProblemCount > 0 ? "Error: fix the areas." : null;
    }
}
