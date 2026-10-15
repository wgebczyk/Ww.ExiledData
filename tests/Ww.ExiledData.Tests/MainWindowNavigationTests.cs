using Microsoft.Extensions.Logging.Abstractions;
using Ww.ExiledData.App.Services;
using Ww.ExiledData.App.ViewModels;
using Ww.ExiledData.Storage;

namespace Ww.ExiledData.Tests;

/// <summary>The single window's navigation: back stack, kept session pages, a deleted session's page.</summary>
public sealed class MainWindowNavigationTests : IDisposable
{
    private readonly SqliteStore _store = SqliteStore.Open(":memory:");
    private readonly SessionController _controller;
    private readonly SessionListViewModel _list;
    private readonly MainWindowViewModel _main;

    public MainWindowNavigationTests()
    {
        _controller = new SessionController(_store, 8);
        var sweeps = new SweepControlViewModel(_controller);
        _list = new SessionListViewModel(_controller, NullLogger<SessionListViewModel>.Instance);
        _main = new MainWindowViewModel(_list, _controller,
            id => new SessionViewModel(_controller, sweeps, id, NullLogger<SessionViewModel>.Instance));
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task Back_returns_through_the_visited_pages_to_the_list()
    {
        var a = await _controller.StartNewSession("A");
        var b = await _controller.StartNewSession("B");

        Assert.True(_main.IsListPage);
        Assert.False(_main.BackCommand.CanExecute(null));

        _main.Open(a);
        _main.Open(b);
        _main.Open(b); // already shown: no extra back entry
        Assert.Equal(b, _main.CurrentSession?.Id);

        _main.BackCommand.Execute(null);
        Assert.Equal(a, _main.CurrentSession?.Id);
        _main.BackCommand.Execute(null);
        Assert.True(_main.IsListPage);
        Assert.False(_main.BackCommand.CanExecute(null));
    }

    [Fact]
    public async Task Going_back_to_a_session_shows_the_same_page()
    {
        var a = await _controller.StartNewSession("A");
        _main.Open(a);
        var page = _main.CurrentSession;
        page!.IsAnalyseMode = true;

        _main.ShowListCommand.Execute(null);
        _main.BackCommand.Execute(null);

        Assert.Same(page, _main.CurrentSession);
        Assert.True(_main.CurrentSession!.IsAnalyseMode);
    }

    [Fact]
    public async Task Only_the_last_visited_pages_and_the_recording_one_stay_alive()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < MainWindowViewModel.KeptSessions + 3; i++)
            ids.Add(await _controller.StartNewSession($"S{i}"));
        var recording = ids[^1]; // the last started records
        await _main.RefreshRecording();

        _main.Open(recording);
        foreach (var id in ids.SkipLast(1))
            _main.Open(id);

        var alive = _main.Sessions.Select(s => s.Id).ToList();
        Assert.Contains(recording, alive);
        Assert.Equal(MainWindowViewModel.KeptSessions + 1, alive.Count);
        Assert.Equal(ids[^2], _main.CurrentSession?.Id);
        Assert.DoesNotContain(ids[0], alive);
    }

    [Fact]
    public async Task A_deleted_session_leaves_the_back_stack_and_its_page_shows_the_list()
    {
        var a = await _controller.StartNewSession("A");
        var b = await _controller.StartNewSession("B");
        _main.Open(a);
        _main.Open(b);
        _main.Open(a);
        var page = _main.CurrentSession!;

        await _controller.DeleteSession(a);
        await page.LoadAsync(); // the page finds its session gone

        Assert.True(_main.IsListPage);
        Assert.DoesNotContain(_main.Sessions, s => s.Id == a);
        _main.BackCommand.Execute(null);
        Assert.Equal(b, _main.CurrentSession?.Id);
        _main.BackCommand.Execute(null); // A's entry is gone: B came from the list
        Assert.True(_main.IsListPage);
        Assert.False(_main.BackCommand.CanExecute(null));
    }
}
