using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Ww.ExiledData.App.Services;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Mapping;
using Ww.ExiledData.Storage;

namespace Ww.ExiledData.App.ViewModels;

/// <summary>
/// One session's page in the main window. Free recording sessions show their captures; mapping
/// sessions show the Gather tab (<see cref="GatherViewModel"/>) with the captures in a side strip.
/// </summary>
public sealed partial class SessionViewModel : ObservableObject, IDisposable
{
    private readonly SessionController _controller;
    private readonly ILogger _log;
    private readonly DispatcherTimer _clock;
    private readonly Action<ActiveSessionInfo?> _onStateChanged;
    private ActiveSessionInfo? _active;
    /// <summary>The recording session when it is another one (making this one record ends it).</summary>
    private ActiveSessionInfo? _otherRecording;
    private int _loadVersion;
    private readonly MappingService? _mapping;
    private CurrencyBook _currencies;
    private readonly Action<Guid> _onMappingChanged;

    public SessionViewModel(SessionController controller, SweepControlViewModel sweeps, Guid sessionId, ILogger<SessionViewModel> logger,
        MappingService? mapping = null, CurrencyBook? currencies = null)
    {
        _controller = controller;
        _log = logger;
        _mapping = mapping;
        _currencies = currencies ?? CurrencyBook.Default;
        Id = sessionId;
        Sweeps = sweeps;
        _onStateChanged = active => Dispatcher.UIThread.Post(() =>
        {
            SetActive(active);
            _ = LoadAsync();
        });
        controller.StateChanged += _onStateChanged;
        _onMappingChanged = id =>
        {
            if (id == Id)
                Dispatcher.UIThread.Post(() => _ = LoadAsync());
        };
        if (mapping is not null)
            mapping.Changed += _onMappingChanged;
        sweeps.CanChange += NotifySweepCommands;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateElapsed());
        _clock.Start();
    }

    public void Dispose()
    {
        _clock.Stop();
        _controller.StateChanged -= _onStateChanged;
        if (_mapping is not null)
            _mapping.Changed -= _onMappingChanged;
        Sweeps.CanChange -= NotifySweepCommands;
    }

    /// <summary>The Gather tab; set once the session is known to be a mapping session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMapping), nameof(IsFreeRecording))]
    public partial GatherViewModel? Gather { get; set; }

    public bool IsMapping => Gather is not null;
    public bool IsFreeRecording => !IsMapping;

    private SessionData? _data;

    /// <summary>
    /// The captures tables, for the whole session or (mapping) the loot card's scope: the shown run's or the session's
    /// items, shown in the loot card's Items list.
    /// </summary>
    private void BuildDetail()
    {
        if (_data is not { } data)
            return;
        var run = IsMapping && Gather!.LootOfRun ? Gather.ShownRunNumber : null;
        var detail = new SessionDetailViewModel(data, IsRecording, run is null ? null : Gather!.ShownRunCaptures);
        if (Detail is null && detail.Stackables.Count == 0 && detail.Items.Count > 0)
            View = CaptureView.Items; // first load: open on what the session has
        detail.RestoreSelection(Detail);
        Detail = detail;
        Gather?.SetItems(detail.Items);
    }

    /// <summary>For the Gather tab: tag and note input (supplied by the view).</summary>
    public Func<string, IReadOnlyList<string>, Task<string?>>? AskTag { get; set; }
    public Func<string, string, Task<string?>>? AskNote { get; set; }
    /// <summary>For the Gather tab: the recall and review dialogs (supplied by the view).</summary>
    public Func<RecallDialogViewModel, Task<bool>>? AskRecall { get; set; }
    public Func<ReviewDialogViewModel, Task<ReviewChoice?>>? AskReview { get; set; }
    /// <summary>For the Analyze tab: (title, help, suggestions) → filter text.</summary>
    public Func<string, string, IReadOnlyList<string>, Task<string?>>? AskFilter { get; set; }
    /// <summary>For the Analyze tab: (title, currencies) → the chosen currency names (Add chart).</summary>
    public Func<string, IReadOnlyList<CurrencyChoice>, Task<IReadOnlyList<string>?>>? AskCurrencies { get; set; }

    /// <summary>The Analyze tab; created with the Gather tab.</summary>
    [ObservableProperty]
    public partial AnalyzeViewModel? Analyze { get; set; }

    /// <summary>The mode tab (mapping sessions): Gather or Analyze. The tabs are the only place it switches.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnalyzeMode), nameof(IsGatherBody), nameof(IsAnalyzeBody), nameof(ShowRecBadge), nameof(ShowAnalyzeSummary), nameof(ShowEndSession),
        nameof(ShowRecordingToggle))]
    public partial bool IsGatherMode { get; set; } = true;

    public bool IsAnalyzeMode { get => !IsGatherMode; set => IsGatherMode = !value; }
    public bool IsGatherBody => IsMapping && IsGatherMode;
    public bool IsAnalyzeBody => IsMapping && IsAnalyzeMode;

    /// <summary>"Run 9" while Gather shows a run picked in the history (the breadcrumb's last segment); else null.</summary>
    public string? PickedRunText => IsGatherBody && Gather is { IsPickedRun: true, ShownRunNumber: { } number } ? $"Run {number}" : null;

    /// <summary>Back from a picked run to the current one.</summary>
    [RelayCommand]
    private void ShowCurrentRun() => Gather?.ShowCurrentRunCommand.Execute(null);

    partial void OnIsGatherModeChanged(bool value) => OnPropertyChanged(nameof(PickedRunText));
    /// <summary>The REC badge shows only in Gather; Analyze shows the run summary instead.</summary>
    public bool ShowRecBadge => IsRecording && (!IsMapping || IsGatherMode);
    public bool ShowAnalyzeSummary => IsAnalyzeBody;
    /// <summary>End session: while recording, not in Analyze.</summary>
    public bool ShowEndSession => IsRecording && !IsAnalyzeBody;
    /// <summary>The header's last button shows: stop (<see cref="ShowEndSession"/>) or play (<see cref="IsEnded"/>).</summary>
    public bool ShowRecordingToggle => ShowEndSession || IsEnded;

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowRecBadge));
        OnPropertyChanged(nameof(ShowEndSession));
        OnPropertyChanged(nameof(ShowRecordingToggle));
    }

    partial void OnGatherChanged(GatherViewModel? value)
    {
        OnPropertyChanged(nameof(IsGatherBody));
        OnPropertyChanged(nameof(IsAnalyzeBody));
        OnPropertyChanged(nameof(ShowRecBadge));
        OnPropertyChanged(nameof(ShowAnalyzeSummary));
    }

    /// <summary>The settings changed the loot grid: the open tabs rebuild theirs.</summary>
    public void SetCurrencies(CurrencyBook currencies)
    {
        _currencies = currencies;
        Gather?.SetBook(currencies);
        Analyze?.SetBook(currencies);
    }

    private AnalyzeViewModel CreateAnalyze() => new(_mapping!, Id, _currencies, _log)
    {
        AskFilter = (title, help, suggestions) => AskFilter?.Invoke(title, help, suggestions) ?? Task.FromResult<string?>(null),
        AskCurrencies = (title, choices) => AskCurrencies?.Invoke(title, choices) ?? Task.FromResult<IReadOnlyList<string>?>(null),
        ReportError = text => StatusText = text,
    };

    private GatherViewModel CreateGather()
    {
        var gather = new GatherViewModel(_mapping!, Id, _currencies, _log)
        {
            AskTag = (title, suggestions) => AskTag?.Invoke(title, suggestions) ?? Task.FromResult<string?>(null),
            AskNote = (title, initial) => AskNote?.Invoke(title, initial) ?? Task.FromResult<string?>(null),
            StartScan = StartInventoryScan,
            AskRecall = dialog => AskRecall?.Invoke(dialog) ?? Task.FromResult(false),
            AskReview = dialog => AskReview?.Invoke(dialog) ?? Task.FromResult<ReviewChoice?>(null),
            FinishScan = Sweeps.Finish,
            CancelScan = () => Confirm is { } confirm ? Sweeps.Cancel(confirm) : Task.CompletedTask,
            ReportError = text => StatusText = text,
            Confirm = (message, confirm) => Confirm?.Invoke(message, confirm) ?? Task.FromResult(false),
        };
        gather.ShownRunChanged += () =>
        {
            BuildDetail();
            OnPropertyChanged(nameof(PickedRunText));
        };
        gather.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GatherViewModel.LootOfRun))
                BuildDetail();
        };
        return gather;
    }

    /// <summary>A scan is a sweep of the inventory, of the chosen kind.</summary>
    private async Task StartInventoryScan(ScanKind kind)
    {
        Sweeps.SelectInventory();
        await _mapping!.SetNextScanKind(kind);
        await Sweeps.Start();
    }

    // Supplied by the view / host.
    /// <summary>(message, confirm button label) → confirmed.</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }
    public Func<string, string, Task<string?>>? Prompt { get; set; }
    public Func<string, string, Task<Stream?>>? SaveFile { get; set; }
    /// <summary>The session no longer exists (deleted here or elsewhere).</summary>
    public Action? Gone { get; set; }

    public Guid Id { get; }

    /// <summary>Sweep area, running sweep and hotkey hint; shared by all windows, shown only while this session records.</summary>
    public SweepControlViewModel Sweeps { get; }

    [ObservableProperty]
    public partial SessionDetailViewModel? Detail { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string SummaryText { get; set; } = "";

    [ObservableProperty]
    public partial string ElapsedText { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnded))]
    [NotifyCanExecuteChangedFor(nameof(EndSessionCommand), nameof(MakeRecordingCommand), nameof(UndoCommand), nameof(RedoCommand))]
    public partial bool IsRecording { get; set; }

    public bool IsEnded => !IsRecording;

    public enum CaptureView { Stackables, Items, Sweeps }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStackables), nameof(ShowItems), nameof(ShowSweeps))]
    public partial CaptureView View { get; set; }

    // Two-way targets for the segmented toggle.
    public bool ShowStackables { get => View == CaptureView.Stackables; set { if (value) View = CaptureView.Stackables; } }
    public bool ShowItems { get => View == CaptureView.Items; set { if (value) View = CaptureView.Items; } }
    public bool ShowSweeps { get => View == CaptureView.Sweeps; set { if (value) View = CaptureView.Sweeps; } }

    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        try
        {
            var data = await _controller.LoadSession(Id);
            var active = await _controller.GetActive();
            if (version != _loadVersion)
                return;
            if (data is null)
            {
                Gone?.Invoke();
                return;
            }

            SetActive(active);
            var s = data.Session;
            Name = s.Name;
            var withdrawn = data.Captures.Count(c => c.IsWithdrawn);
            var captures = $"{data.Captures.Count} captures" + (withdrawn > 0 ? $" ({withdrawn} undone/removed)" : "");
            var kind = SessionListViewModel.KindText(s.Kind);
            SummaryText = IsRecording
                ? $"{kind} · started {Format.Time(s.StartedAt)} · {captures}"
                : $"{kind} · {Format.Time(s.StartedAt)} – {(s.EndedAt is { } end ? Format.ShortTime(end) : "?")} · {Format.HoursMinutes(s.Duration(DateTimeOffset.UtcNow))}"
                  + (s.Interrupted ? " · interrupted" : "") + $" · {captures}";

            _data = data;

            if (s.Kind == SessionKind.Mapping && _mapping is not null)
            {
                var map = await _mapping.Load(Id);
                var undo = await _mapping.UndoState(Id);
                if (version != _loadVersion)
                    return;
                if (Analyze is null)
                {
                    Analyze = CreateAnalyze();
                    await Analyze.LoadLayout();
                }
                Gather ??= CreateGather();
                Gather.Update(map, IsRecording, _active?.Sweep, undo, DateTimeOffset.UtcNow);
                Analyze.Update(map, IsRecording, DateTimeOffset.UtcNow);
            }
            BuildDetail();
            UpdateElapsed();
        }
        catch (Exception ex)
        {
            Fail("Loading the session", ex);
        }
    }

    private void SetActive(ActiveSessionInfo? active)
    {
        _active = active?.Id == Id ? active : null;
        _otherRecording = active?.Id == Id ? null : active;
        IsRecording = _active is not null;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        NotifySweepCommands();
    }

    private void NotifySweepCommands()
    {
        StartSweepCommand.NotifyCanExecuteChanged();
        FinishSweepCommand.NotifyCanExecuteChanged();
        CancelSweepCommand.NotifyCanExecuteChanged();
    }

    private void UpdateElapsed()
    {
        ElapsedText = _active is { } a ? Format.Duration(DateTimeOffset.UtcNow - a.StartedAt) : "";
        Gather?.Tick(DateTimeOffset.UtcNow);
    }

    // --- commands -------------------------------------------------------------------------------

    [RelayCommand]
    private Task Reload() => LoadAsync();

    [RelayCommand(CanExecute = nameof(IsRecording))]
    private async Task EndSession()
    {
        if (Confirm is not null && await Confirm($"End session '{Name}'? The next capture starts a new session.", "End session"))
            await Do("Ending the session", _controller.EndSession);
    }

    /// <summary>Continues this ended session: captures go here from now on (only one session records at a time).</summary>
    [RelayCommand(CanExecute = nameof(IsEnded))]
    private async Task MakeRecording()
    {
        if (_otherRecording is { } other && Confirm is not null
            && !await Confirm($"Make '{Name}' the recording session? '{other.Name}' is recording now; it ends.", "Make recording"))
            return;
        await Do("Making this the recording session", () => _controller.ContinueSession(Id));
    }

    private bool CanUndo() => _active?.CanUndo == true;
    private bool CanRedo() => _active?.CanRedo == true;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task Undo() => Do("Undo", _controller.Undo);

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private Task Redo() => Do("Redo", _controller.Redo);

    // Sweeps belong to the recording session, so only its window offers them.
    private bool CanStartSweep() => IsRecording && Sweeps.CanStart;
    private bool CanEndSweep() => IsRecording && Sweeps.HasSweep;

    [RelayCommand(CanExecute = nameof(CanStartSweep))]
    private Task StartSweep() => Do("Starting a sweep", Sweeps.Start);

    [RelayCommand(CanExecute = nameof(CanEndSweep))]
    private Task FinishSweep() => Do("Finishing the sweep", Sweeps.Finish);

    [RelayCommand(CanExecute = nameof(CanEndSweep))]
    private Task CancelSweep() => Confirm is { } confirm
        ? Do("Canceling the sweep", () => Sweeps.Cancel(confirm))
        : Task.CompletedTask;

    [RelayCommand]
    private async Task Rename()
    {
        if (Prompt is not null && await Prompt("Rename session", Name) is { Length: > 0 } name && name != Name)
            await Do("Renaming the session", () => _controller.Rename(Id, name));
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (Confirm is null)
            return;
        var warning = IsRecording ? " It is the recording session; capturing will start a new one." : "";
        if (await Confirm($"Delete session '{Name}' and everything captured in it?{warning}", "Delete"))
            await Do("Deleting the session", () => _controller.DeleteSession(Id));
    }

    [RelayCommand]
    private Task RemoveItem() => Detail is { IsActive: true, SelectedItem: { } item }
        ? Do("Removing the item", () => _controller.RemoveItem(Id, item.CaptureId))
        : Task.CompletedTask;

    [RelayCommand]
    private Task RemoveStackable() => Detail is { IsActive: true, SelectedStackable: { } s }
        ? Do("Removing the stackable", () => _controller.RemoveStackable(Id, s.Key))
        : Task.CompletedTask;

    [RelayCommand]
    private Task SplitCovered() => Detail is { IsActive: true, SelectedItem: { CoveredCount: > 0 } item }
        ? Do("Splitting the covered cell", () => _controller.SplitCovered(Id, item.CaptureId))
        : Task.CompletedTask;

    [RelayCommand]
    private Task ExportJson() => Export("json", SessionExporter.ToJson);

    [RelayCommand]
    private Task ExportCsv() => Export("csv", SessionExporter.ToCsv);

    private async Task Export(string extension, Func<SessionData, string> format)
    {
        if (SaveFile is null)
            return;
        await Do($"Exporting {extension.ToUpperInvariant()}", async () =>
        {
            var data = await _controller.LoadSession(Id) ?? throw new InvalidOperationException("Session no longer exists.");
            var safeName = string.Concat(data.Session.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            await using var stream = await SaveFile($"{safeName}.{extension}", extension);
            if (stream is null)
                return;
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: extension == "csv").GetPreamble()
                .Concat(Encoding.UTF8.GetBytes(format(data))).ToArray();
            await stream.WriteAsync(bytes);
            StatusText = $"Exported '{data.Session.Name}' as {extension.ToUpperInvariant()}";
            _log.LogInformation("Exported session {Id} as {Format}", data.Session.Id, extension);
        });
    }

    private async Task Do(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Fail(what, ex);
        }
    }

    private void Fail(string what, Exception ex)
    {
        _log.LogError(ex, "{What} failed", what);
        StatusText = $"{what} failed: {ex.Message}";
    }
}
