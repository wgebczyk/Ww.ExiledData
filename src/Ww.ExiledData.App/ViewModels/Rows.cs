using System.Globalization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ww.ExiledData.Core.Tracking;
using Ww.ExiledData.Mapping;
using Ww.ExiledData.Storage;

namespace Ww.ExiledData.App.ViewModels;

internal static class Format
{
    public static string Time(DateTimeOffset t) => t.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    public static string ShortTime(DateTimeOffset t) => t.LocalDateTime.ToString("HH:mm:ss");

    public static string Duration(TimeSpan d) => d.TotalHours >= 1
        ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}"
        : $"{d.Minutes:00}:{d.Seconds:00}";

    /// <summary>Session-length style, e.g. <c>2h 14m</c>.</summary>
    public static string HoursMinutes(TimeSpan d) => $"{(int)d.TotalHours}h {d.Minutes:00}m";

    /// <summary>How old something is, coarse: <c>now</c>, <c>12m</c>, <c>3h</c>, <c>2d</c>.</summary>
    public static string Age(TimeSpan d) => d.TotalMinutes < 1 ? "now"
        : d.TotalHours < 1 ? $"{(int)d.TotalMinutes}m"
        : d.TotalDays < 1 ? $"{(int)d.TotalHours}h"
        : $"{(int)d.TotalDays}d";

    /// <summary>
    /// A value in <paramref name="unit"/>, as its amount and, when adaptive, the unit it landed in (<c>23.1</c> + <c>DIV</c>); a fixed
    /// unit is in the column header instead. A dash and no unit when unknown.
    /// </summary>
    public static (string Amount, string Unit) Value(CurrencyValue? value, PriceUnit unit)
    {
        if (value is not { } v)
            return (Amount(null), "");
        var (amount, shown) = v.In(unit);
        return (Amount(amount), unit == PriceUnit.Adaptive ? UnitCode(shown) : "");
    }

    /// <summary>The catalog code of a unit's orb.</summary>
    public static string UnitCode(PriceUnit unit) => unit switch
    {
        PriceUnit.Divine => "DIV",
        PriceUnit.Exalted => "EX",
        PriceUnit.Chaos => "ChO",
        _ => "",
    };

    /// <summary>A currency amount with about three significant digits (<c>1234</c>, <c>12.3</c>, <c>0.0421</c>); a dash when unknown.</summary>
    public static string Amount(double? value) => value switch
    {
        null => "–",
        0 => "0",
        >= 100 => value.Value.ToString("#,0", CultureInfo.InvariantCulture),
        >= 10 => value.Value.ToString("0.0", CultureInfo.InvariantCulture),
        >= 1 => value.Value.ToString("0.00", CultureInfo.InvariantCulture),
        _ => value.Value.ToString("0." + new string('0', Math.Min(8, 2 - (int)Math.Floor(Math.Log10(value.Value)))), CultureInfo.InvariantCulture),
    };
}

public sealed record ItemRow(Guid CaptureId, string Label, int X, int Y, string Cell, int CoveredCount, string LastSeen, string Flags, string RawText);

public sealed record SweepRow(string Label, string Grid, string Status, int ItemCount, string Started, string Ended, string Rectangle);

public sealed record StackableRow(string Key, string Name, int Count, int MaxStack, string ItemClass, string Updated, string RawText);

public sealed partial class SessionDetailViewModel : ObservableObject
{
    /// <param name="onlyCaptures">Show only these captures (a mapping run's): items by capture, stackables by the keys
    /// they copied (their counts stay the session's); null shows everything.</param>
    public SessionDetailViewModel(SessionData data, bool isActive, IReadOnlySet<Guid>? onlyCaptures = null)
    {
        var s = data.Session;
        Id = s.Id;
        IsActive = isActive;
        Name = s.Name;
        StartText = Format.Time(s.StartedAt);
        EndText = s.EndedAt is { } end ? Format.Time(end) : "running";
        DurationText = s.EndedAt is { } e ? Format.Duration(e - s.StartedAt) : "";
        Interrupted = s.Interrupted;
        var withdrawn = data.Captures.Count(c => c.IsWithdrawn);
        CapturesText = $"{data.Captures.Count} captures" + (withdrawn > 0 ? $" ({withdrawn} undone/removed)" : "");

        var sweepLabels = data.Sweeps.ToDictionary(w => w.Sweep.Id, w => w.Sweep.Label);
        var stackKeys = onlyCaptures is null ? null
            : data.Captures.Where(c => c.Kind == CaptureKind.Stackable && onlyCaptures.Contains(c.Id)).Select(c => c.IdentityKey).ToHashSet();
        foreach (var i in data.Items.Where(i => onlyCaptures?.Contains(i.CaptureId) != false).OrderByDescending(i => i.LastSeenAt))
        {
            var covered = i.CoveredCaptureIds.Count;
            var flags = string.Join(", ", new[]
            {
                i.PossibleDuplicate ? "possible duplicate" : null,
                covered > 0 ? $"multi-cell? +{covered} (possible duplicate)" : null,
                i.NeedsReview ? "needs review" : null,
            }.OfType<string>());
            var cell = i is { SweepId: { } sweep, Cell: { } c } ? $"{sweepLabels.GetValueOrDefault(sweep, "sweep")} {c}" : "";
            Items.Add(new ItemRow(i.CaptureId, i.Label, i.Position.X, i.Position.Y, cell, covered, Format.ShortTime(i.LastSeenAt), flags, i.RawText));
        }
        foreach (var k in data.Stackables.Where(k => stackKeys?.Contains(k.Key) != false).OrderBy(k => k.Name))
            Stackables.Add(new StackableRow(k.Key, k.Name, k.Count, k.MaxStack, k.ItemClass, Format.ShortTime(k.UpdatedAt), k.LatestRawText));
        foreach (var (w, count) in data.Sweeps.OrderByDescending(w => w.Sweep.StartedAt))
        {
            Sweeps.Add(new SweepRow(w.Label, w.Kind.ToString(), w.Status.ToString(), count, Format.ShortTime(w.StartedAt),
                w.EndedAt is { } ended ? Format.ShortTime(ended) : "running",
                $"{w.Grid.X:F0}, {w.Grid.Y:F0} · cell {w.Grid.CellSize:F1} px"));
        }
    }

    public Guid Id { get; }
    public bool IsActive { get; }
    public bool IsReadOnly => !IsActive;
    public string StartText { get; }
    public string EndText { get; }
    public string DurationText { get; }
    public bool Interrupted { get; }
    public string CapturesText { get; }
    public ObservableCollection<ItemRow> Items { get; } = [];
    public ObservableCollection<StackableRow> Stackables { get; } = [];
    public ObservableCollection<SweepRow> Sweeps { get; } = [];

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial ItemRow? SelectedItem { get; set; }

    [ObservableProperty]
    public partial StackableRow? SelectedStackable { get; set; }

    [ObservableProperty]
    public partial string RawText { get; set; } = "";

    partial void OnSelectedItemChanged(ItemRow? value)
    {
        if (value is not null)
            RawText = value.RawText;
    }

    partial void OnSelectedStackableChanged(StackableRow? value)
    {
        if (value is not null)
            RawText = value.RawText;
    }

    public void RestoreSelection(SessionDetailViewModel? previous)
    {
        if (previous?.Id != Id)
            return;
        SelectedItem = Items.FirstOrDefault(i => i.CaptureId == previous.SelectedItem?.CaptureId);
        SelectedStackable = Stackables.FirstOrDefault(s => s.Key == previous.SelectedStackable?.Key);
        if (SelectedItem is null && SelectedStackable is null)
            RawText = previous.RawText;
    }
}
