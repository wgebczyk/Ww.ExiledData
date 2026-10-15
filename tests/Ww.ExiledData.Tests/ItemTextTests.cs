using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Text;

namespace Ww.ExiledData.Tests;

public class ItemTextTests
{
    [Theory]
    [InlineData("Item Class: Waystones\nRarity: Magic", true)]
    [InlineData("hello world", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsItem_requires_item_class_prefix(string? text, bool expected) =>
        Assert.Equal(expected, ItemText.IsItem(text));

    [Theory]
    [InlineData(Fixture.BodyArmourRare, "Horror Salvation Ironclad Vestments")]
    [InlineData(Fixture.Waystone, "Waystone (Tier 1) of Hexwarding")]
    [InlineData(Fixture.Verisium, "Verisium")]
    public void Label_is_name_lines_after_rarity(string fixture, string expected) =>
        Assert.Equal(expected, ItemLabel.From(Fixture.Load(fixture)));

    [Fact]
    public void Normalize_unifies_line_endings_and_trailing_whitespace() =>
        Assert.Equal("a\nb\nc", ItemText.Normalize("a\r\nb\rc\r\n\r\n"));

    [Fact]
    public void Sections_split_on_separator()
    {
        var sections = ItemText.Sections(ItemText.Normalize(Fixture.Load(Fixture.Transmutation)));

        Assert.Equal(4, sections.Count);
        Assert.Equal("Stack Size: 2/40", Assert.Single(sections[1]));
    }

    [Fact]
    public void Regular_identity_ignores_line_endings() =>
        Assert.Equal(
            IdentityKeys.Regular(Fixture.Load(Fixture.BodyArmourRare)),
            IdentityKeys.Regular(Fixture.LoadCrLf(Fixture.BodyArmourRare)));

    [Fact]
    public void Screen_point_tolerance_is_per_axis()
    {
        var p = new ScreenPoint(100, 100);

        Assert.True(p.IsNear(new ScreenPoint(105, 95), 5));
        Assert.False(p.IsNear(new ScreenPoint(106, 100), 5));
    }
}
