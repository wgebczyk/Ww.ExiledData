using Avalonia;
using Ww.ExiledData.App.Overlay;
using Ww.ExiledData.Core.Sweeps;

namespace Ww.ExiledData.Tests;

/// <summary>Layout and hit testing of the parts around a sweep grid (title, handles, frame).</summary>
public class GridChromeTests
{
    private static readonly Rect Grid = new(100, 200, 480, 200);

    [Fact]
    public void Title_sits_above_the_grid_clear_of_the_handles()
    {
        var chrome = GridChrome.Of(Grid, 150, withHandles: true);
        Assert.Equal(new Rect(100, 200 - 12 - GridChrome.TitleHeight, 150, GridChrome.TitleHeight), chrome.Title);
        Assert.Equal(8, chrome.Handles.Count);
        Assert.True(chrome.Frame.Contains(chrome.Title) && chrome.Frame.Contains(Grid.Inflate(GridChrome.HoverSize / 2)));
    }

    [Fact]
    public void Title_goes_below_when_there_is_no_room_above()
    {
        var chrome = GridChrome.Of(Grid.WithY(10), 150, withHandles: false);
        Assert.True(chrome.Title.Top >= Grid.WithY(10).Bottom);
        Assert.Empty(chrome.Handles);
    }

    [Theory]
    [InlineData(100, 200, GridHandle.TopLeft)]
    [InlineData(340, 205, GridHandle.Top)]
    [InlineData(587, 300, GridHandle.Right)] // the grip is bigger than the drawn handle
    [InlineData(340, 400, GridHandle.Bottom)]
    [InlineData(580, 400, GridHandle.BottomRight)]
    [InlineData(150, 175, GridHandle.Move)] // the title
    public void Presses_grab_handles_and_the_title(double x, double y, GridHandle expected) =>
        Assert.Equal(expected, GridChrome.Of(Grid, 150, withHandles: true).HitTest(new Point(x, y)));

    [Theory]
    [InlineData(300, 300)] // inside the grid: the game's
    [InlineData(50, 50)]
    public void Presses_elsewhere_grab_nothing(double x, double y) =>
        Assert.Null(GridChrome.Of(Grid, 150, withHandles: true).HitTest(new Point(x, y)));

    [Fact]
    public void Without_handles_nothing_can_be_grabbed()
    {
        var chrome = GridChrome.Of(Grid, 150, withHandles: false);
        Assert.Null(chrome.HitTest(chrome.Title.Center));
        Assert.Empty(chrome.GrabAreas());
    }
}
