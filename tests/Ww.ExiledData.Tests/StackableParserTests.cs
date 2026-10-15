using Ww.ExiledData.Core.Parsing;

namespace Ww.ExiledData.Tests;

public class StackableParserTests
{
    [Theory]
    [InlineData(Fixture.ChanceShard, "Chance Shard", 1, 10)]
    [InlineData(Fixture.Verisium, "Verisium", 31, 1000)]
    [InlineData(Fixture.ArtificersOrb, "Artificer's Orb", 3, 20)]
    [InlineData(Fixture.Transmutation, "Orb of Transmutation", 2, 40)]
    public void Parses_currency_samples(string fixture, string name, int count, int max)
    {
        var r = StackableParser.Parse(Fixture.Load(fixture));

        Assert.Equal(StackParseStatus.Parsed, r.Status);
        Assert.Equal("Stackable Currency", r.ItemClass);
        Assert.Equal("Currency", r.Rarity);
        Assert.Equal(name, r.Name);
        Assert.Equal(count, r.Count);
        Assert.Equal(max, r.MaxStack);
        Assert.Null(r.Warning);
    }

    [Fact]
    public void Parses_the_mapping_plan_samples_counts_above_max_and_greater_perfect_names()
    {
        var parsed = Fixture.Samples(Fixture.CurrencySamples).Select(StackableParser.Parse).ToList();

        Assert.All(parsed, r => Assert.Equal(StackParseStatus.Parsed, r.Status));
        Assert.Equal(
            [("Orb of Transmutation", 606, 40), ("Greater Orb of Transmutation", 164, 40), ("Perfect Orb of Transmutation", 7, 40),
             ("Chaos Orb", 200, 20), ("Greater Chaos Orb", 17, 20), ("Orb of Annulment", 7, 20)],
            parsed.Select(r => (r.Name!, r.Count, r.MaxStack)));
    }

    [Theory]
    [InlineData(Fixture.Waystone)]
    [InlineData(Fixture.BodyArmourRare)]
    public void Non_stack_items_are_not_stackable(string fixture) =>
        Assert.Equal(StackParseStatus.NotStackable, StackableParser.Parse(Fixture.Load(fixture)).Status);

    [Fact]
    public void Crlf_parses_same_as_lf() =>
        Assert.Equal(StackableParser.Parse(Fixture.Load(Fixture.Verisium)), StackableParser.Parse(Fixture.LoadCrLf(Fixture.Verisium)));

    [Fact]
    public void Missing_name_is_header_unparsed()
    {
        var text = "Item Class: Stackable Currency\nRarity: Currency\n--------\nStack Size: 5/10\n";

        var r = StackableParser.Parse(text);

        Assert.Equal(StackParseStatus.HeaderUnparsed, r.Status);
        Assert.NotNull(r.Warning);
    }

    [Theory]
    [InlineData("Stack Size: lots")]
    [InlineData("Stack Size: 5")]
    [InlineData("Stack Size: 5/0")]
    public void Bad_count_is_count_unparsed(string stackLine)
    {
        var text = $"Item Class: Stackable Currency\nRarity: Currency\nChance Shard\n--------\n{stackLine}\n";

        var r = StackableParser.Parse(text);

        Assert.Equal(StackParseStatus.CountUnparsed, r.Status);
        Assert.NotNull(r.Warning);
    }
}
