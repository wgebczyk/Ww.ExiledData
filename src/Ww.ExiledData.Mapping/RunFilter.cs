using System.Globalization;
using System.Text.RegularExpressions;

namespace Ww.ExiledData.Mapping;

/// <summary>How a tag filter compares (MAPPING_PLAN.md 4). Comparisons other than (not) equal need numeric values.</summary>
public enum TagOp
{
    Has,
    Missing,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

/// <summary>
/// A condition on runs for the Analyze scope (MAPPING_PLAN.md 4): tags now, mods later. Filters combine with AND.
/// <see cref="Parse"/> reads the short text typed in the "+ Add filter" box.
/// </summary>
public abstract partial record RunFilter
{
    public abstract bool Matches(MapSession session, MapRun run);

    /// <summary>The filter as a chip shows it.</summary>
    public abstract string Text { get; }

    /// <summary>The runs this filter does not match (B of a compare is "the rest of the scope").</summary>
    public RunFilter Negate() => this is NotFilter not ? not.Inner : new NotFilter(this);

    /// <summary>
    /// Reads a filter: <c>died</c> (has the tag), <c>!died</c> or <c>not died</c> (has not), <c>rituals &gt;= 5</c>
    /// (also <c>&gt; &lt; &lt;= = != ≥ ≤ ≠</c>), <c>rituals: 6</c> (equal), <c>runs 5-23</c>, <c>runs 5-</c>,
    /// <c>runs -10</c>, <c>runs 7</c>. Null with <paramref name="error"/> when the text is not a filter.
    /// </summary>
    public static RunFilter? Parse(string? text, out string? error)
    {
        error = null;
        text = text?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = "Type a filter.";
            return null;
        }

        if (RunsPattern().Match(text) is { Success: true } runs)
        {
            int? from = runs.Groups["from"].Success ? int.Parse(runs.Groups["from"].Value, CultureInfo.InvariantCulture) : null;
            int? to = runs.Groups["to"].Success ? int.Parse(runs.Groups["to"].Value, CultureInfo.InvariantCulture) : null;
            if (!runs.Groups["dash"].Success)
                to = from;
            if (from is null && to is null || from > to)
            {
                error = "Run range: \"runs 5-23\", \"runs 5-\", \"runs -10\" or \"runs 7\".";
                return null;
            }
            return new RunRangeFilter(from, to);
        }

        if (NotPattern().Match(text) is { Success: true } not)
            return new TagFilter(not.Groups["name"].Value.Trim(), TagOp.Missing, null);

        if (ComparePattern().Match(text) is { Success: true } compare)
        {
            var name = compare.Groups["name"].Value.Trim();
            var value = compare.Groups["value"].Value.Trim();
            var op = compare.Groups["op"].Value switch
            {
                ">=" or "≥" => TagOp.GreaterOrEqual,
                "<=" or "≤" => TagOp.LessOrEqual,
                ">" => TagOp.Greater,
                "<" => TagOp.Less,
                "!=" or "≠" => TagOp.NotEqual,
                _ => TagOp.Equal, // "=", "==", ":"
            };
            if (op is not (TagOp.Equal or TagOp.NotEqual) && MapTag.ParseNumber(value) is null)
            {
                error = $"\"{value}\" is not a number; only = and != compare text.";
                return null;
            }
            return new TagFilter(name, op, value);
        }

        if (text.IndexOfAny(['<', '>', '=', '≥', '≤', '≠', ':', '!']) >= 0)
        {
            error = "Tag filter: \"died\", \"!died\", \"rituals >= 5\" or \"rituals: 6\".";
            return null;
        }
        return new TagFilter(text, TagOp.Has, null);
    }

    [GeneratedRegex(@"^runs?\s*(?<from>\d+)?\s*(?<dash>[-–]\s*(?<to>\d+)?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex RunsPattern();

    [GeneratedRegex(@"^(!|not\s+)(?<name>[^<>=≥≤≠:!]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NotPattern();

    [GeneratedRegex(@"^(?<name>[^<>=≥≤≠:!]+?)\s*(?<op>>=|<=|==|!=|≥|≤|≠|>|<|=|:)\s*(?<value>.+)$")]
    private static partial Regex ComparePattern();
}

/// <summary>Runs with (or without) a tag, optionally comparing its value.</summary>
public sealed record TagFilter(string Name, TagOp Op, string? Value) : RunFilter
{
    public override bool Matches(MapSession session, MapRun run)
    {
        var tags = session.TagsOf(run.Id).Where(t => string.Equals(t.Name, Name, StringComparison.OrdinalIgnoreCase)).ToList();
        return Op switch
        {
            TagOp.Has => tags.Count > 0,
            TagOp.Missing => tags.Count == 0,
            TagOp.Equal => tags.Any(t => SameValue(t)),
            TagOp.NotEqual => !tags.Any(t => SameValue(t)),
            _ => tags.Any(t => t.Number is { } n && MapTag.ParseNumber(Value) is { } v && Op switch
            {
                TagOp.Less => n < v,
                TagOp.LessOrEqual => n <= v,
                TagOp.Greater => n > v,
                _ => n >= v,
            }),
        };
    }

    private bool SameValue(MapTag tag) =>
        tag.Number is { } n && MapTag.ParseNumber(Value) is { } v
            ? n == v
            : string.Equals(tag.Value ?? "", Value ?? "", StringComparison.OrdinalIgnoreCase);

    public override string Text => Op switch
    {
        TagOp.Has => $"Tag: {Name}",
        TagOp.Missing => $"Tag: no {Name}",
        TagOp.Equal => $"Tag: {Name} = {Value}",
        TagOp.NotEqual => $"Tag: {Name} ≠ {Value}",
        TagOp.Less => $"Tag: {Name} < {Value}",
        TagOp.LessOrEqual => $"Tag: {Name} ≤ {Value}",
        TagOp.Greater => $"Tag: {Name} > {Value}",
        _ => $"Tag: {Name} ≥ {Value}",
    };
}

/// <summary>Runs numbered from <see cref="From"/> to <see cref="To"/> (inclusive; an open end has no limit).</summary>
public sealed record RunRangeFilter(int? From, int? To) : RunFilter
{
    public override bool Matches(MapSession session, MapRun run) =>
        (From is null || run.Number >= From) && (To is null || run.Number <= To);

    public override string Text => (From, To) switch
    {
        ({ } f, { } t) when f == t => $"Run {f}",
        ({ } f, { } t) => $"Runs {f}–{t}",
        ({ } f, null) => $"Runs {f}–",
        _ => $"Runs –{To}",
    };
}

/// <summary>The runs <see cref="Inner"/> does not match.</summary>
public sealed record NotFilter(RunFilter Inner) : RunFilter
{
    public override bool Matches(MapSession session, MapRun run) => !Inner.Matches(session, run);
    public override string Text => $"not ({Inner.Text})";
}
