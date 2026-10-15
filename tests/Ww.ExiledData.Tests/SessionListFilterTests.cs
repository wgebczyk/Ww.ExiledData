using Ww.ExiledData.App.ViewModels;

namespace Ww.ExiledData.Tests;

/// <summary>The session list's Started filter (it took over the classic window's date filter).</summary>
public sealed class SessionListFilterTests
{
    private static readonly DateTime Today = new(2026, 10, 12);

    private static bool Matches(string filter, DateTime started, DateTime? from = null, DateTime? to = null) =>
        SessionListViewModel.StartedMatches(filter, started, Today, from, to);

    [Fact]
    public void Any_matches_everything() => Assert.True(Matches(SessionListViewModel.StartedAny, new DateTime(2020, 1, 1)));

    [Fact]
    public void Today_is_the_local_calendar_day()
    {
        Assert.True(Matches(SessionListViewModel.StartedToday, Today.AddHours(23.5)));
        Assert.False(Matches(SessionListViewModel.StartedToday, Today.AddMinutes(-1)));
    }

    [Fact]
    public void Last_days_include_today_and_the_days_before()
    {
        Assert.True(Matches(SessionListViewModel.StartedWeek, Today.AddDays(-6)));
        Assert.False(Matches(SessionListViewModel.StartedWeek, Today.AddDays(-7)));
        Assert.True(Matches(SessionListViewModel.StartedMonth, Today.AddDays(-29).AddHours(1)));
        Assert.False(Matches(SessionListViewModel.StartedMonth, Today.AddDays(-30)));
    }

    [Fact]
    public void Between_is_inclusive_and_an_empty_end_has_no_limit()
    {
        var from = new DateTime(2026, 10, 1);
        var to = new DateTime(2026, 10, 5);

        Assert.True(Matches(SessionListViewModel.StartedBetween, from.AddHours(0.5), from, to));
        Assert.True(Matches(SessionListViewModel.StartedBetween, to.AddHours(23), from, to));
        Assert.False(Matches(SessionListViewModel.StartedBetween, to.AddDays(1), from, to));
        Assert.False(Matches(SessionListViewModel.StartedBetween, from.AddMinutes(-1), from, to));
        Assert.True(Matches(SessionListViewModel.StartedBetween, new DateTime(2000, 1, 1), from: null, to));
        Assert.True(Matches(SessionListViewModel.StartedBetween, new DateTime(2099, 1, 1), from, to: null));
    }
}
