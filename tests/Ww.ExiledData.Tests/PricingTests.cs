using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Ww.ExiledData.App.Services;
using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.Tests;

/// <summary>poe.ninja prices: parsing, the cache file, values in Divine / Exalted / Chaos, and when a pull happens.</summary>
public sealed class PricingTests : IDisposable
{
    private const string IndexState = """
        {"economyLeagues":[{"name":"HC Rites","hardcore":true},{"name":"Rites","hardcore":false},{"name":"Standard","hardcore":false}]}
        """;

    // As poe.ninja sends it: values in Divine Orbs, rates per Divine.
    private const string CurrencyOverview = """
        {"core":{"items":[],"rates":{"exalted":500,"chaos":10},"primary":"divine","secondary":"chaos"},
         "lines":[{"id":"chaos","primaryValue":0.1},{"id":"exalted","primaryValue":0.002},{"id":"junk","primaryValue":0}],
         "items":[{"id":"chaos","name":"Chaos Orb"},{"id":"exalted","name":"Exalted Orb"},{"id":"junk","name":"Junk"}]}
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ww-prices-" + Guid.NewGuid().ToString("N"));
    private readonly FakeNinja _handler = new();
    private DateTimeOffset _now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string CachePath => Path.Combine(_dir, "prices.json");

    private PoeNinja Ninja() => new(new HttpClient(_handler) { BaseAddress = new Uri(PoeNinja.BaseUrl) }) { RequestGap = TimeSpan.Zero };

    private PriceService Service(string league = "") => new(CachePath, () => league, NullLogger.Instance, () => _now, Ninja());

    [Fact]
    public async Task A_pull_prices_items_by_name_in_divines_with_the_rates_of_the_pull()
    {
        var prices = await Ninja().Fetch("Rites", _now, TestContext.Current.CancellationToken);

        Assert.Equal(0.1, prices.Divine["Chaos Orb"]);
        Assert.Equal(1, prices.Divine["Divine Orb"]);
        Assert.False(prices.Divine.ContainsKey("Junk")); // no price is not a zero price
        Assert.Equal(new CurrencyValue(0.2, 100, 2), new CurrencyBook().WithPrices(prices).ValueOf("Chaos Orb", 2));
        Assert.Null(new CurrencyBook().WithPrices(prices).ValueOf("Mirror of Kalandra", 1));
    }

    [Fact]
    public async Task The_current_league_is_the_first_softcore_one_that_is_not_Standard()
    {
        Assert.Equal("Rites", await Ninja().CurrentLeague(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_pull_is_saved_and_a_restart_shows_it_without_pulling()
    {
        await Service().RefreshIfNeeded();
        var requests = _handler.Requests;

        var restarted = Service();
        await restarted.RefreshIfNeeded();

        Assert.Equal("Rites", restarted.Prices.League);
        Assert.Equal(0.1, restarted.Prices.Divine["Chaos Orb"]);
        Assert.Equal(requests, _handler.Requests);
    }

    [Fact]
    public async Task Prices_are_pulled_again_once_an_hour_old()
    {
        var service = Service();
        await service.RefreshIfNeeded();
        var first = service.Prices;

        _now += TimeSpan.FromMinutes(59);
        await service.RefreshIfNeeded();
        Assert.Same(first, service.Prices);
        Assert.False(service.IsStale);

        _now += TimeSpan.FromMinutes(1);
        Assert.True(service.IsStale);
        await service.RefreshIfNeeded();
        Assert.NotSame(first, service.Prices);
    }

    [Fact]
    public async Task Pulls_by_hand_are_at_least_five_minutes_apart_even_across_a_restart()
    {
        var service = Service();
        await service.RefreshIfNeeded(force: true);
        var requests = _handler.Requests;

        _now += TimeSpan.FromMinutes(4);
        Assert.Equal(_now + TimeSpan.FromMinutes(1), service.NextPullAt);
        await service.RefreshIfNeeded(force: true);
        await Service().RefreshIfNeeded(force: true); // restarted: the cached pull counts
        Assert.Equal(requests, _handler.Requests);

        _now += TimeSpan.FromMinutes(1);
        Assert.Null(service.NextPullAt);
        await service.RefreshIfNeeded(force: true);
        Assert.True(_handler.Requests > requests);
    }

    [Fact]
    public async Task A_failed_pull_keeps_the_old_prices_and_waits_before_retrying()
    {
        var service = Service();
        await service.RefreshIfNeeded();
        var first = service.Prices;

        _handler.Fail = true;
        _now += TimeSpan.FromHours(2);
        await service.RefreshIfNeeded();
        Assert.Same(first, service.Prices);
        Assert.NotNull(service.LastError);

        var requests = _handler.Requests;
        _now += TimeSpan.FromMinutes(5);
        await service.RefreshIfNeeded();
        Assert.Equal(requests, _handler.Requests);
    }

    [Fact]
    public async Task A_configured_league_is_used_and_a_change_of_it_pulls_at_once()
    {
        var service = Service("Rites");
        await service.RefreshIfNeeded();
        Assert.DoesNotContain(_handler.Urls, u => u.Contains("index-state"));

        var other = new PriceService(CachePath, () => "Other League", NullLogger.Instance, () => _now + PriceService.MinInterval, Ninja());
        await other.RefreshIfNeeded();
        Assert.Equal("Other League", other.Prices.League);
        Assert.Contains(_handler.Urls, u => u.Contains("league=Other%20League"));
    }

    [Theory]
    [InlineData(PriceUnit.Adaptive, 2.5, PriceUnit.Divine)]
    [InlineData(PriceUnit.Adaptive, 1, PriceUnit.Divine)]
    [InlineData(PriceUnit.Adaptive, 0.5, PriceUnit.Chaos)]      // 5 c
    [InlineData(PriceUnit.Adaptive, 0.05, PriceUnit.Exalted)]   // 0.5 c, 25 ex
    [InlineData(PriceUnit.Exalted, 2.5, PriceUnit.Exalted)]
    [InlineData(PriceUnit.Chaos, 0.001, PriceUnit.Chaos)]
    public void Adaptive_values_step_down_from_divine_to_chaos_to_exalted_below_one(PriceUnit setting, double divine, PriceUnit shown)
    {
        var value = new CurrencyValue(divine, divine * 500, divine * 10);

        var (amount, unit) = value.In(setting);

        Assert.Equal(shown, unit);
        Assert.Equal(unit switch { PriceUnit.Divine => divine, PriceUnit.Chaos => divine * 10, _ => divine * 500 }, amount);
    }

    private sealed class FakeNinja : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public int Requests => Urls.Count;
        public bool Fail { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Urls.Add(url);
            if (Fail)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var body = url.Contains("index-state") ? IndexState
                : url.Contains("type=Currency") ? CurrencyOverview
                : """{"core":null,"lines":[],"items":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
