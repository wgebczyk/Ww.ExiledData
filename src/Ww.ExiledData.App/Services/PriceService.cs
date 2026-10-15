using Microsoft.Extensions.Logging;
using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.App.Services;

/// <summary>
/// Keeps the poe.ninja prices fresh: the last pull is loaded from <c>prices.json</c> at start, and a pull older than
/// <see cref="MaxAge"/> (or of another league) is replaced, checked every few minutes and on demand (the app bar's
/// price chip). A failed pull keeps the old prices and is retried after <see cref="RetryAfter"/>.
/// </summary>
public sealed class PriceService(string cachePath, Func<string> league, ILogger logger, Func<DateTimeOffset>? clock = null, PoeNinja? ninja = null)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);
    /// <summary>
    /// Pulls never start closer than this to the previous one (also the cached one, so a restart does not reset it),
    /// however often the chip is clicked: poe.ninja is a free service and its prices move slowly.
    /// </summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly PoeNinja _ninja = ninja ?? new(PoeNinja.CreateHttpClient());
    private DateTimeOffset? _lastFailure;
    private DateTimeOffset? _lastPull;

    public PriceBook Prices { get; private set; } = PriceBook.Load(cachePath);
    public bool IsRefreshing { get; private set; }
    /// <summary>Why the last pull failed; null once one succeeds.</summary>
    public string? LastError { get; private set; }

    /// <summary>Prices or the refresh state changed (raised on the calling thread).</summary>
    public event Action? Changed;

    public bool IsStale => Prices.Age(_now()) is not { } age || age >= MaxAge;

    /// <summary>When the next pull may start; null when one may start now.</summary>
    public DateTimeOffset? NextPullAt =>
        new[] { _lastPull, Prices.FetchedAt }.Max() is { } last && last + MinInterval is var next && next > _now() ? next : null;

    /// <summary>
    /// Pulls when the prices are stale (and the last failure is old enough), or always when <paramref name="force"/>;
    /// either way not within <see cref="MinInterval"/> of the previous pull (<see cref="NextPullAt"/>).
    /// </summary>
    public async Task RefreshIfNeeded(bool force = false)
    {
        if (IsRefreshing)
            return;
        var configured = league().Trim();
        var wrongLeague = configured.Length > 0 && !string.Equals(configured, Prices.League, StringComparison.OrdinalIgnoreCase);
        if (!force && !IsStale && !wrongLeague)
            return;
        if (!force && _lastFailure is { } failed && _now() - failed < RetryAfter)
            return;
        if (NextPullAt is not null)
            return;

        _lastPull = _now();
        IsRefreshing = true;
        Changed?.Invoke();
        try
        {
            var name = configured.Length > 0 ? configured : await _ninja.CurrentLeague();
            var prices = await _ninja.Fetch(name, _now());
            prices.Save(cachePath);
            Prices = prices;
            LastError = null;
            _lastFailure = null;
            logger.LogInformation("Prices of {League} pulled from poe.ninja: {Count} items, 1 div = {Ex} ex = {Chaos} c",
                name, prices.Divine.Count, prices.ExaltedPerDivine, prices.ChaosPerDivine);
        }
        catch (Exception ex)
        {
            _lastFailure = _now();
            LastError = ex.Message;
            logger.LogWarning(ex, "Pulling prices from poe.ninja failed");
        }
        finally
        {
            IsRefreshing = false;
            Changed?.Invoke();
        }
    }
}
