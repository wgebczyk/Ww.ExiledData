using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ww.ExiledData.App.Services;

namespace Ww.ExiledData.App.ViewModels;

/// <summary>One entry of the session switcher (the breadcrumb's ▾).</summary>
public sealed record SwitcherItem(Guid Id, string Name, string Detail, bool IsRecording, bool IsCurrent);

/// <summary>
/// The single app window: shows the session list or one session's page, with a back stack, a breadcrumb
/// (Sessions / session / Run N), a session switcher and the recording session's REC pill. Session pages are
/// kept alive for the recording session and the few last visited ones, so going back keeps their mode and picked run.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>Visited session pages kept besides the recording one.</summary>
    public const int KeptSessions = 4;
    private const int SwitcherSize = 10;

    private readonly SessionController _controller;
    private readonly Func<Guid, SessionViewModel> _createSession;
    private readonly Dictionary<Guid, SessionViewModel> _sessions = [];
    /// <summary>Visited sessions, most recent last (eviction order).</summary>
    private readonly List<Guid> _visited = [];
    /// <summary>Earlier pages: a session id, or null for the session list.</summary>
    private readonly List<Guid?> _back = [];
    private ActiveSessionInfo? _recording;

    public MainWindowViewModel(SessionListViewModel list, SessionController controller, Func<Guid, SessionViewModel> createSession)
    {
        List = list;
        _controller = controller;
        _createSession = createSession;
        list.OpenSession = Open;
        CurrentPage = list;
        controller.StateChanged += active => Dispatcher.UIThread.Post(() => SetRecording(active));
        _ = InitRecording(controller);
        new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateElapsed()).Start();
    }

    public SessionListViewModel List { get; }

    /// <summary>The page shown: <see cref="List"/> or a <see cref="SessionViewModel"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSession), nameof(IsSessionPage), nameof(IsListPage), nameof(Title), nameof(ShowRecPill))]
    public partial object CurrentPage { get; private set; }

    public SessionViewModel? CurrentSession => CurrentPage as SessionViewModel;
    public bool IsSessionPage => CurrentSession is not null;
    public bool IsListPage => !IsSessionPage;

    public string Title => CurrentSession is { } s ? $"{s.Name} · Ww.ExiledData" : "Ww.ExiledData";

    /// <summary>The session pages alive now (settings and status go to each).</summary>
    public IEnumerable<SessionViewModel> Sessions => _sessions.Values;

    public Func<Task>? OpenSettings { get; set; }

    partial void OnCurrentPageChanged(object oldValue, object newValue)
    {
        if (oldValue is SessionViewModel old)
            old.PropertyChanged -= OnSessionChanged;
        if (newValue is SessionViewModel session)
            session.PropertyChanged += OnSessionChanged;
        NotifyPickedRun();
        BackCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The breadcrumb's last segment: "Run 9" while Gather shows a run picked in the history.</summary>
    public string? PickedRunText => CurrentSession?.PickedRunText;
    public bool HasPickedRun => PickedRunText is not null;
    /// <summary>The session segment is the page shown (bold) unless a picked run follows it.</summary>
    public bool IsSessionCrumbCurrent => IsSessionPage && !HasPickedRun;

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.Name))
            OnPropertyChanged(nameof(Title));
        if (e.PropertyName == nameof(SessionViewModel.PickedRunText))
            NotifyPickedRun();
    }

    private void NotifyPickedRun()
    {
        OnPropertyChanged(nameof(PickedRunText));
        OnPropertyChanged(nameof(HasPickedRun));
        OnPropertyChanged(nameof(IsSessionCrumbCurrent));
    }

    // --- navigation -----------------------------------------------------------------------------

    /// <summary>Shows a session's page (the one already alive, if any).</summary>
    public void Open(Guid sessionId)
    {
        if (CurrentSession?.Id == sessionId)
            return;
        Push();
        Show(sessionId);
    }

    [RelayCommand]
    private void ShowList()
    {
        if (IsListPage)
            return;
        Push();
        Show(null);
    }

    private bool CanGoBack() => _back.Count > 0;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (_back.Count == 0)
            return;
        var page = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        Show(page);
    }

    [RelayCommand]
    private void OpenRecording()
    {
        if (_recording is { } r)
            Open(r.Id);
    }

    private void Push()
    {
        var current = CurrentSession?.Id;
        if (_back.Count == 0 || _back[^1] != current)
            _back.Add(current);
    }

    private void Show(Guid? sessionId)
    {
        if (sessionId is not { } id)
        {
            CurrentPage = List;
            _ = List.RefreshAsync(); // run counts and tags may have changed on a session page
            return;
        }

        if (!_sessions.TryGetValue(id, out var session))
        {
            session = _createSession(id);
            session.Gone = () => OnGone(id);
            _sessions[id] = session;
            _ = session.LoadAsync();
        }
        _visited.Remove(id);
        _visited.Add(id);
        CurrentPage = session;
        Evict();
    }

    /// <summary>Drops the least recently visited pages beyond <see cref="KeptSessions"/>; never the shown or recording one.</summary>
    private void Evict()
    {
        while (_visited.Count(id => id != _recording?.Id) > KeptSessions)
        {
            var oldest = _visited.First(id => id != _recording?.Id && id != CurrentSession?.Id);
            Drop(oldest);
        }
    }

    private void Drop(Guid id)
    {
        _visited.Remove(id);
        if (_sessions.Remove(id, out var session))
            session.Dispose();
    }

    /// <summary>The session was deleted (here or elsewhere): forget it; when it is shown, go back to the list.</summary>
    private void OnGone(Guid id)
    {
        var shown = CurrentSession?.Id == id;
        Drop(id);
        _back.RemoveAll(p => p == id);
        for (var i = _back.Count - 1; i > 0; i--)
            if (_back[i] == _back[i - 1])
                _back.RemoveAt(i);
        if (shown)
        {
            if (_back.Count > 0 && _back[^1] is null)
                _back.RemoveAt(_back.Count - 1);
            Show(null);
        }
        BackCommand.NotifyCanExecuteChanged();
    }

    // --- switcher -------------------------------------------------------------------------------

    public ObservableCollection<SwitcherItem> SwitcherItems { get; } = [];

    /// <summary>Fills the switcher: the recording session, then the newest sessions.</summary>
    public void RefreshSwitcher()
    {
        SwitcherItems.Clear();
        foreach (var row in List.Latest(SwitcherSize))
            SwitcherItems.Add(new SwitcherItem(row.Id, row.Name, row.IsMapping ? $"{row.Kind} · {row.RunsText} runs · {row.StartedText}" : $"{row.Kind} · {row.StartedText}",
                row.IsRecording, row.Id == CurrentSession?.Id));
    }

    public void OpenSwitcherItem(SwitcherItem item) => Open(item.Id);

    [RelayCommand]
    private Task Settings() => OpenSettings?.Invoke() ?? Task.CompletedTask;

    // --- recording pill -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecording), nameof(ShowRecPill))]
    public partial string? RecordingName { get; private set; }

    [ObservableProperty]
    public partial string RecordingElapsed { get; private set; } = "";

    public bool HasRecording => RecordingName is not null;

    /// <summary>The pill shows on every page but the recording session's own (its header has the REC badge).</summary>
    public bool ShowRecPill => _recording is { } r && CurrentSession?.Id != r.Id;

    /// <summary>Reads the recording session (at start; later it follows the controller's state changes).</summary>
    public Task RefreshRecording() => InitRecording(_controller);

    private async Task InitRecording(SessionController controller)
    {
        try
        {
            SetRecording(await controller.GetActive());
        }
        catch (Exception)
        {
            // The pill shows on the next state change; errors are reported by the pages.
        }
    }

    private void SetRecording(ActiveSessionInfo? active)
    {
        _recording = active;
        RecordingName = active?.Name;
        OnPropertyChanged(nameof(ShowRecPill));
        UpdateElapsed();
    }

    private void UpdateElapsed() =>
        RecordingElapsed = _recording is { } r ? Format.Duration(DateTimeOffset.UtcNow - r.StartedAt) : "";
}
