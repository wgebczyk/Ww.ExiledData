using System.Text.Json;
using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Tracking;
using Ww.ExiledData.Storage;

namespace Ww.ExiledData.Tests;

public class SessionExporterTests
{
    private static SessionData Sample()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        var tracker = new SessionTracker(SessionLifecycle.Start(null, time.Now, "Run, \"one\""), 8, time);
        tracker.Apply(Fixture.Load(Fixture.Waystone), new ScreenPoint(1, 2));
        tracker.Apply(Fixture.Load(Fixture.Waystone), new ScreenPoint(90, 2));
        tracker.Apply(Fixture.Load(Fixture.Verisium), new ScreenPoint(5, 5));
        using var store = SqliteStore.Open(":memory:");
        store.SaveTracker(tracker);
        return store.LoadSession(tracker.Session.Id)!;
    }

    [Fact]
    public void Json_contains_session_state_and_raw_log()
    {
        using var doc = JsonDocument.Parse(SessionExporter.ToJson(Sample()));
        var root = doc.RootElement;

        Assert.Equal("Run, \"one\"", root.GetProperty("session").GetProperty("name").GetString());
        Assert.Equal(2, root.GetProperty("items").GetArrayLength());
        Assert.True(root.GetProperty("items")[1].GetProperty("possibleDuplicate").GetBoolean());
        Assert.Equal(1000, root.GetProperty("stackables")[0].GetProperty("maxStack").GetInt32());
        Assert.Equal(3, root.GetProperty("captures").GetArrayLength());
    }

    [Fact]
    public void Csv_has_header_one_row_per_item_and_quotes_multiline_text()
    {
        var csv = SessionExporter.ToCsv(Sample());
        var lines = csv.Split("\r\n");

        Assert.Equal("Kind,Name,Count,MaxStack,X,Y,Sweep,Cell,Time,Flags,RawText", lines[0]);
        Assert.StartsWith("Item,Waystone (Tier 1) of Hexwarding,,,1,2,,,", lines[1]);
        Assert.Contains("possible-duplicate", csv);
        Assert.Contains("Stackable,Verisium,31,1000,,,,,", csv);
        Assert.Contains("\"Item Class: Waystones\n", csv);
    }
}
