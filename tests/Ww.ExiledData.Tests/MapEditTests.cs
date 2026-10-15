using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.Tests;

/// <summary>Tags, notes and the undo/redo of mapping edits (MAPPING_PLAN.md 3.5, 6).</summary>
public sealed class MapEditTests
{
    private readonly MapFeed _feed = new();
    private MapSession Session => _feed.Session;

    [Theory]
    [InlineData("rituals: 6", "rituals", "6", 6.0)]
    [InlineData("died", "died", null, null)]
    [InlineData("  boss :  Arbiter of Ash ", "boss", "Arbiter of Ash", null)]
    [InlineData("ratio: 1,5", "ratio", "1,5", 1.5)]
    [InlineData("note:", "note", null, null)]
    [InlineData("time: 10:30", "time", "10:30", null)]
    public void Tags_parse_as_name_and_optional_value(string text, string name, string? value, double? number)
    {
        var tag = MapTag.Parse(text)!;

        Assert.Equal((name, value, number), (tag.Name, tag.Value, tag.Number));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(": 6")]
    public void Blank_text_or_name_is_no_tag(string text) => Assert.Null(MapTag.Parse(text));

    [Fact]
    public void Tag_prints_as_typed() =>
        Assert.Equal(["rituals: 6", "died"], new[] { MapTag.Parse("rituals:6")!, MapTag.Parse("died")! }.Select(t => t.ToString()));

    [Fact]
    public void Tags_and_notes_go_on_the_session_or_a_run()
    {
        _feed.Copy(MapItems.Waystone(15));
        var run = Session.CurrentRun!.Id;

        Session.AddTag(Session.SessionId, "farming");
        Session.AddTag(run, "rituals: 6");
        Session.AddTag(run, "rituals: 6"); // not twice
        Session.SetNote(run, "  lots of rares  ");

        Assert.Equal([new MapTag("farming")], Session.TagsOf(Session.SessionId));
        Assert.Equal([new MapTag("rituals", "6")], Session.TagsOf(run));
        Assert.Equal("lots of rares", Session.NoteOf(run));
        Assert.Throws<InvalidOperationException>(() => Session.AddTag(Guid.NewGuid(), "x"));
    }

    [Fact]
    public void Edits_undo_and_redo_in_order()
    {
        _feed.Copy(MapItems.Waystone(15));
        var run = Session.CurrentRun!.Id;
        Session.AddTag(run, "died");
        Session.SetNote(run, "first");
        Session.SetNote(run, "second");
        Session.RemoveTag(run, new MapTag("died"));

        Session.Undo();
        Assert.Equal([new MapTag("died")], Session.TagsOf(run));
        Session.Undo();
        Assert.Equal("first", Session.NoteOf(run));
        Session.Undo();
        Session.Undo();
        Assert.Empty(Session.TagsOf(run));
        Assert.Null(Session.NoteOf(run));
        Assert.False(Session.CanUndo);

        Session.Redo();
        Assert.Equal([new MapTag("died")], Session.TagsOf(run));
        Session.SetNote(run, "new"); // a new edit clears the redo stack
        Assert.False(Session.CanRedo);
    }

    [Fact]
    public void No_op_edits_are_not_recorded()
    {
        _feed.Copy(MapItems.Waystone(15));

        Assert.False(Session.SetNote(Session.SessionId, "  "));
        Assert.False(Session.RemoveTag(Session.SessionId, new MapTag("absent")));
        Assert.False(Session.CanUndo);
    }

    [Fact]
    public void End_run_undoes_until_a_later_run_starts()
    {
        _feed.Copy(MapItems.Waystone(15));
        Session.EndRun(_feed.Now);
        Session.Undo();
        Assert.NotNull(Session.CurrentRun);

        Session.EndRun(_feed.Now);
        Session.AddTag(Session.SessionId, "kept");
        _feed.Copy(MapItems.Waystone(16));

        Session.Undo(); // the tag; ending run 1 is no longer undoable
        Assert.False(Session.CanUndo);
        Assert.NotNull(Session.Runs[0].EndedAt);
    }
}
