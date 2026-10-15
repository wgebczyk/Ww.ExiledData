using System.Globalization;
using System.Text;
using System.Text.Json;
using Ww.ExiledData.Core.Sweeps;

namespace Ww.ExiledData.Storage;

/// <summary>Per-session export. JSON carries everything (including the raw capture log); CSV is one row per item.</summary>
public static class SessionExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ToJson(SessionData data)
    {
        var s = data.Session;
        var export = new
        {
            session = new
            {
                s.Id,
                s.Name,
                s.StartedAt,
                s.EndedAt,
                durationSeconds = s.EndedAt is { } end ? (end - s.StartedAt).TotalSeconds : (double?)null,
                s.Interrupted,
            },
            items = data.Items.Select(i => new
            {
                i.Label,
                x = i.Position.X,
                y = i.Position.Y,
                sweepId = i.SweepId,
                cell = Cell(i.Cell),
                coveredCells = i.CoveredCaptureIds.Count,
                i.LastSeenAt,
                i.PossibleDuplicate,
                i.NeedsReview,
                i.RawText,
            }),
            stackables = data.Stackables.Select(k => new
            {
                k.ItemClass,
                k.Rarity,
                k.Name,
                k.Count,
                k.MaxStack,
                k.UpdatedAt,
                k.ParserVersion,
            }),
            sweeps = data.Sweeps.Select(w => new
            {
                w.Sweep.Id,
                w.Sweep.Label,
                grid = w.Sweep.Kind.Name,
                columns = w.Sweep.Kind.Columns,
                rows = w.Sweep.Kind.Rows,
                status = w.Sweep.Status.ToString(),
                w.Sweep.StartedAt,
                w.Sweep.EndedAt,
                w.ItemCount,
                rectangle = new { w.Sweep.Grid.X, w.Sweep.Grid.Y, w.Sweep.Grid.CellSize },
                cells = data.Cells.Where(c => c.SweepId == w.Sweep.Id).Select(c => new
                {
                    column = c.Cell.Column,
                    row = c.Cell.Row,
                    state = c.State.ToString(),
                    c.CaptureId,
                }),
            }),
            captures = data.Captures.Select(c => new
            {
                c.Id,
                c.CapturedAt,
                x = c.Position.X,
                y = c.Position.Y,
                c.SweepId,
                cell = Cell(c.Cell),
                kind = c.Kind.ToString(),
                c.WithdrawnAt,
                c.RawText,
            }),
        };
        return JsonSerializer.Serialize(export, JsonOptions);
    }

    public static string ToCsv(SessionData data)
    {
        var sweepLabels = data.Sweeps.ToDictionary(w => w.Sweep.Id, w => w.Sweep.Label);
        var sb = new StringBuilder();
        Row(sb, "Kind", "Name", "Count", "MaxStack", "X", "Y", "Sweep", "Cell", "Time", "Flags", "RawText");
        foreach (var i in data.Items)
        {
            var flags = string.Join(' ', new[]
            {
                i.PossibleDuplicate ? "possible-duplicate" : null,
                i.CoveredCaptureIds.Count > 0 ? $"covers-{i.CoveredCaptureIds.Count}-more-cells" : null,
                i.NeedsReview ? "needs-review" : null,
            }.OfType<string>());
            var sweep = i.SweepId is { } id ? sweepLabels.GetValueOrDefault(id, "") : "";
            Row(sb, "Item", i.Label, "", "", Num(i.Position.X), Num(i.Position.Y), sweep, i.Cell?.ToString() ?? "", Time(i.LastSeenAt), flags, i.RawText);
        }
        foreach (var k in data.Stackables)
            Row(sb, "Stackable", k.Name, Num(k.Count), Num(k.MaxStack), "", "", "", "", Time(k.UpdatedAt), "", k.LatestRawText);
        return sb.ToString();
    }

    private static object? Cell(GridCell? cell) => cell is { } c ? new { column = c.Column, row = c.Row } : null;
    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Time(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);

    private static void Row(StringBuilder sb, params string[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            var f = fields[i];
            if (f.AsSpan().IndexOfAny(",\"\r\n") >= 0)
                sb.Append('"').Append(f.Replace("\"", "\"\"")).Append('"');
            else
                sb.Append(f);
        }
        sb.Append("\r\n");
    }
}
