using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Ww.ExiledData.App.Services;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Mapping;
using Ww.ExiledData.Storage;

namespace Ww.ExiledData.App.ViewModels;

/// <summary>What the New session dialog asks for. A null name means the default (the start time).</summary>
public sealed record NewSessionRequest(string? Name, SessionKind Kind);

/// <summary>The session list page: every session, with search and filters; opening one shows its page.</summary>
public sealed partial class SessionListViewModel : ObservableObject
{
    public const string KindFree = "Free recording";
    public const string KindMapping = "Mapping";

    private readonly SessionController _controller;
    private readonly ILogger _log;
    private IReadOnlyList<SessionSummary> _all = [];
    private IReadOnlyDictionary<Guid, MappingSummary> _mapping = new Dictionary<Guid, MappingSummary>();
    private ActiveSessionInfo? _recording;
    private int _refreshVersion;

    public static string KindText(SessionKind kind) => kind == SessionKind.Mapping ? KindMapping : KindFree;

    public SessionListViewModel(SessionController controller, ILogger<SessionListViewModel> logger)
    {
        _controller = controller;
        _log = logger;
        controller.StateChanged += active => Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        // Keeps the recording session's duration current.
        new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Rebuild()).Start();
    }

    // Supplied by the view / host.
    /// <summary>(message, confirm button label) → confirmed.</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }
    public Func<string, string, Task<string?>>? Prompt { get; set; }
    /// <summary>(default name, the recording session's name or null) → the new session, or null when canceled.</summary>
    public Func<string, string?, Task<NewSessionRequest?>>? AskNewSession { get; set; }
    public Action<Guid>? OpenSession { get; set; }
    /// <summary>Run counts and session tags of mapping sessions (the mapping module).</summary>
    public Func<Task<IReadOnlyDictionary<Guid, MappingSummary>>>? LoadMappingSummaries { get; set; }
    public Action? ToggleTheme { get; set; }
    /// <summary>(title, suggestions) → a tag typed for the selected mapping session, or null when canceled.</summary>
    public Func<string, IReadOnlyList<string>, Task<string?>>? AskTag { get; set; }
    /// <summary>Tags used before (suggestions) and adding a session tag (the mapping module).</summary>
    public Func<Task<IReadOnlyList<string>>>? KnownTags { get; set; }
    public Func<Guid, string, Task>? AddSessionTag { get; set; }

    public ObservableCollection<SessionListRow> Sessions { get; } = [];

    public string[] KindFilters { get; } = ["Kind: All", $"Kind: {KindFree}", $"Kind: {KindMapping}"];
    public string[] StateFilters { get; } = ["State: any", "State: Recording", "State: Ended"];

    public const string StartedAny = "Started: any";
    public const string StartedToday = "Started: today";
    public const string StartedWeek = "Started: last 7 days";
    public const string StartedMonth = "Started: last 30 days";
    public const string StartedBetween = "Started: between…";
    public string[] StartedFilters { get; } = [StartedAny, StartedToday, StartedWeek, StartedMonth, StartedBetween];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartedBetween))]
    public partial string StartedFilter { get; set; } = StartedAny;

    /// <summary>The custom range is chosen: show its two date pickers.</summary>
    public bool IsStartedBetween => StartedFilter == StartedBetween;

    [ObservableProperty]
    public partial DateTime? StartedFrom { get; set; }

    [ObservableProperty]
    public partial DateTime? StartedTo { get; set; }

    partial void OnStartedFilterChanged(string value) => Rebuild();
    partial void OnStartedFromChanged(DateTime? value) => Rebuild();
    partial void OnStartedToChanged(DateTime? value) => Rebuild();

    /// <summary>
    /// Whether a session started at <paramref name="startedLocal"/> passes the Started filter. Days are local
    /// calendar days, both ends of a range inclusive; an empty end has no limit.
    /// </summary>
    public static bool StartedMatches(string filter, DateTime startedLocal, DateTime today, DateTime? from, DateTime? to) => filter switch
    {
        StartedToday => startedLocal.Date == today.Date,
        StartedWeek => startedLocal.Date > today.Date.AddDays(-7),
        StartedMonth => startedLocal.Date > today.Date.AddDays(-30),
        StartedBetween => (from is null || startedLocal.Date >= from.Value.Date) && (to is null || startedLocal.Date <= to.Value.Date),
        _ => true,
    };

    public const string TagAny = "Tag: any";

    /// <summary>"Tag: any" and every session tag in use.</summary>
    public ObservableCollection<string> TagFilters { get; } = [TagAny];

    [ObservableProperty]
    public partial string? TagFilter { get; set; } = TagAny;

    partial void OnTagFilterChanged(string? value)
    {
        if (!_rebuildingTags)
            Rebuild();
    }

    private bool _rebuildingTags;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial string KindFilter { get; set; } = "Kind: All";

    [ObservableProperty]
    public partial string StateFilter { get; set; } = "State: any";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText), nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(ContinueCommand), nameof(RenameCommand), nameof(DeleteCommand), nameof(TagsCommand))]
    public partial SessionListRow? Selected { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptySessions))]
    [NotifyCanExecuteChangedFor(nameof(DeleteEmptyCommand))]
    public partial int EmptyCount { get; set; }

    [ObservableProperty]
    public partial string ThemeToggleText { get; set; } = "";

    public bool HasEmptySessions => EmptyCount > 0;
    public bool HasSelection => Selected is not null;

    public string SelectionText => Selected is { } s
        ? $"1 selected · {s.Name} · Enter opens it"
        : Sessions.Count == 0 ? "No sessions match" : "Select a session";

    partial void OnSearchTextChanged(string value) => Rebuild();
    partial void OnKindFilterChanged(string value) => Rebuild();
    partial void OnStateFilterChanged(string value) => Rebuild();

    public async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        try
        {
            var all = await _controller.ListSessions();
            var active = await _controller.GetActive();
            var mapping = LoadMappingSummaries is { } load ? await load() : _mapping;
            if (version != _refreshVersion)
                return;
            _all = all;
            _recording = active;
            _mapping = mapping;
            Rebuild();
        }
        catch (Exception ex)
        {
            Fail("Refreshing sessions", ex);
        }
    }

    /// <summary>Re-applies search and filters to the last loaded list, keeping the selection.</summary>
    private void Rebuild()
    {
        var now = DateTimeOffset.UtcNow;
        var today = DateTime.Today;
        var selectedId = Selected?.Id;
        var search = SearchText.Trim();
        RebuildTagFilters();
        var tag = TagFilter is { } t && t != TagAny ? t : null;
        var rows = _all
            .Select(s => new SessionListRow(s, s.Session.Id == _recording?.Id, now, _mapping.GetValueOrDefault(s.Session.Id)))
            .Where(r => search.Length == 0 || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || r.Tags.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Where(r => KindFilter == KindFilters[0] || KindFilter.EndsWith(r.Kind, StringComparison.Ordinal))
            .Where(r => StateFilter == StateFilters[0] || (StateFilter == StateFilters[1]) == r.IsRecording)
            .Where(r => StartedMatches(StartedFilter, r.StartedAt.LocalDateTime, today, StartedFrom, StartedTo))
            .Where(r => tag is null || r.Tags.Contains(tag))
            .OrderByDescending(r => r.IsRecording)
            .ThenByDescending(r => r.StartedAt)
            .ToList();

        Sessions.Clear();
        foreach (var r in rows)
            Sessions.Add(r);
        Selected = Sessions.FirstOrDefault(r => r.Id == selectedId);
        EmptyCount = _all.Count(s => s.IsEmpty && s.Session.Id != _recording?.Id);
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasNoRows));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>The tag filter's choices follow the session tags in use; a chosen tag that disappeared resets to any.</summary>
    private void RebuildTagFilters()
    {
        var tags = _mapping.Values.SelectMany(m => m.Tags).Select(t => t.ToString()).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (TagFilters.Skip(1).SequenceEqual(tags))
            return;
        var keep = TagFilter is { } current && tags.Contains(current) ? current : TagAny;
        _rebuildingTags = true;
        try
        {
            TagFilters.Clear();
            TagFilters.Add(TagAny);
            foreach (var t in tags)
                TagFilters.Add(t);
            TagFilter = keep;
        }
        finally
        {
            _rebuildingTags = false;
        }
    }

    /// <summary>The newest sessions, ignoring search and filters (the session switcher), the recording one first.</summary>
    public IReadOnlyList<SessionListRow> Latest(int count)
    {
        var now = DateTimeOffset.UtcNow;
        return _all
            .Select(s => new SessionListRow(s, s.Session.Id == _recording?.Id, now, _mapping.GetValueOrDefault(s.Session.Id)))
            .OrderByDescending(r => r.IsRecording)
            .ThenByDescending(r => r.StartedAt)
            .Take(count)
            .ToList();
    }

    public bool HasNoRows => Sessions.Count == 0;
    public string EmptyText => _all.Count == 0
        ? "No sessions yet. Copy an item in game (Ctrl+C) or press New session."
        : "No sessions match the search and filters.";

    // --- commands -------------------------------------------------------------------------------

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    /// <summary>Asks for a name and kind, starts the session (ending the recording one) and opens it.</summary>
    [RelayCommand]
    private async Task NewSession()
    {
        if (AskNewSession is null
            || await AskNewSession(SessionLifecycle.DefaultName(DateTimeOffset.Now), _recording?.Name) is not { } request)
            return;
        await Do("Starting a new session", async () =>
        {
            var id = await _controller.StartNewSession(request.Name, request.Kind);
            OpenSession?.Invoke(id);
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Open()
    {
        if (Selected is { } s)
            OpenSession?.Invoke(s.Id);
    }

    private bool CanContinue() => Selected is { IsRecording: false };

    /// <summary>Makes the selected ended session the recording one again and opens it.</summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task Continue()
    {
        if (Selected is not { } s)
            return;
        if (_recording is { } recording && Confirm is not null
            && !await Confirm($"Continue '{s.Name}'? '{recording.Name}' is recording now; it ends.", "Continue"))
            return;
        await Do("Continuing the session", async () =>
        {
            await _controller.ContinueSession(s.Id);
            OpenSession?.Invoke(s.Id);
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Rename()
    {
        if (Selected is not { } s || Prompt is null)
            return;
        if (await Prompt("Rename session", s.Name) is { Length: > 0 } name && name != s.Name)
            await Do("Renaming the session", () => _controller.Rename(s.Id, name));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Delete()
    {
        if (Selected is not { } s || Confirm is null)
            return;
        var warning = s.IsRecording ? " It is the recording session; capturing will start a new one." : "";
        if (await Confirm($"Delete session '{s.Name}' and everything captured in it?{warning}", "Delete"))
            await Do("Deleting the session", () => _controller.DeleteSession(s.Id));
    }

    [RelayCommand(CanExecute = nameof(HasEmptySessions))]
    private async Task DeleteEmpty()
    {
        if (Confirm is null || !await Confirm($"Delete {EmptyCount} empty session(s)?", "Delete"))
            return;
        await Do("Deleting empty sessions", async () => StatusText = $"Deleted {await _controller.DeleteEmptySessions()} empty session(s)");
    }

    private bool CanTag() => Selected is { IsMapping: true } && AskTag is not null && AddSessionTag is not null;

    /// <summary>Adds a session tag to the selected mapping session (tags are removed on its page).</summary>
    [RelayCommand(CanExecute = nameof(CanTag))]
    private async Task Tags()
    {
        if (Selected is not { IsMapping: true } s || AskTag is null || AddSessionTag is null)
            return;
        var known = KnownTags is { } load ? await load() : [];
        if (await AskTag($"Tag for '{s.Name}'", known) is { Length: > 0 } text)
            await Do("Adding the tag", async () =>
            {
                await AddSessionTag(s.Id, text);
                await RefreshAsync();
            });
    }

    [RelayCommand]
    private void Theme() => ToggleTheme?.Invoke();

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

/// <summary>One row of the session list table.</summary>
/// <param name="mapping">The mapping data of a mapping session, if it has any.</param>
public sealed class SessionListRow(SessionSummary summary, bool isRecording, DateTimeOffset now, MappingSummary? mapping = null)
{
    public Guid Id => summary.Session.Id;
    public string Name => summary.Session.Name;
    public DateTimeOffset StartedAt => summary.Session.StartedAt;
    public string Kind => SessionListViewModel.KindText(summary.Session.Kind);
    public bool IsMapping => summary.Session.Kind == SessionKind.Mapping;
    public bool IsRecording => isRecording;
    public string StateText => isRecording ? "Recording" : summary.Session.Interrupted ? "Ended · interrupted" : "Ended";
    // A free recording has no runs.
    public string RunsText => summary.Session.Kind == SessionKind.Mapping ? (mapping?.RunCount ?? 0).ToString(CultureInfo.InvariantCulture) : "–";
    public string StartedText => summary.Session.StartedAt.LocalDateTime.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
    public string DurationText => Format.HoursMinutes(summary.Session.Duration(now));
    public string CountsText => $"{summary.CaptureCount} captures · {summary.RegularItemCount} items · {summary.StackableCount} stackables";
    public IReadOnlyList<string> Tags { get; } = mapping?.Tags.Select(t => t.ToString()).ToList() ?? [];
}
