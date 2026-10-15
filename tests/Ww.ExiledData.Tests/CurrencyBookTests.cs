using Ww.ExiledData.Mapping;

namespace Ww.ExiledData.Tests;

/// <summary>Currency codes and the loot grid layout (MAPPING_PLAN.md 5, appendix A).</summary>
public sealed class CurrencyBookTests
{
    private static readonly CurrencyBook Book = CurrencyBook.Default;

    [Theory]
    [InlineData("Orb of Transmutation", "TRN", null)]
    [InlineData("Greater Orb of Transmutation", "TRN", "G")]
    [InlineData("Perfect Orb of Transmutation", "TRN", "P")]
    [InlineData("Greater Chaos Orb", "ChO", "G")]
    [InlineData("Lesser Jeweller's Orb", "JO", "L")] // no plain Jeweller's Orb in the catalog
    [InlineData("Chance Shard", "CS", null)]
    [InlineData("Liquid Paranoia", "lP", null)]
    public void Known_currencies_use_the_catalog_codes_with_the_tier_letter_as_a_badge(string name, string code, string? badge)
    {
        var display = Book.Describe(name);

        Assert.Equal((code, badge), (display.Code, display.Badge));
    }

    [Fact]
    public void A_tier_code_without_its_tier_letter_is_shown_whole()
    {
        var catalog = new CurrencyCatalog([new CatalogEntry("Greater Odd Orb", "GOO", "Currency", null, 20)]);
        var book = new CurrencyBook(catalog: catalog);

        var display = book.Describe("Greater Odd Orb");

        Assert.Equal(("Greater Odd Orb", "GOO", (string?)null), (display.BaseName, display.Code, display.Badge));
    }

    [Theory]
    [InlineData("Chaos Orb", "Currency/CurrencyRerollRare.webp")]
    [InlineData("Greater Chaos Orb", "Currency/CurrencyRerollRare.webp")]
    [InlineData("Not A Currency", null)]
    public void The_catalog_gives_the_icon_path(string name, string? image) =>
        Assert.Equal(image, Book.Describe(name).Image);

    [Theory]
    [InlineData("Exalted Orb", "EXA")]
    [InlineData("Orb of Augmentation", "AUG")]
    [InlineData("Regal Orb", "REG")]
    [InlineData("Ex", "EX")]
    [InlineData("Orb of the Ox", "OX")]
    [InlineData("Orb", "ORB")]
    public void Currencies_without_a_code_get_one_from_their_name(string name, string code) =>
        Assert.Equal(code, CurrencyBook.GenerateCode(name));

    [Fact]
    public void A_variant_outside_the_catalog_uses_the_generated_base_code()
    {
        var display = Book.Describe("Greater Unheard Orb");

        Assert.Equal(("Unheard Orb", "UNH", "G"), (display.BaseName, display.Code, display.Badge));
    }

    [Fact]
    public void The_built_in_book_has_no_loot_grid() => Assert.Empty(Book.Layout.Rows);

    [Fact]
    public void A_layout_has_rows_of_nine_and_no_empty_rows_at_the_end()
    {
        var layout = LootLayout.From([
            ["Chaos Orb", " ", " Divine Orb "],
            [],
            ["A", "B", "C", "D", "E", "F", "G", "H", "I", "too many"],
            [null, null],
            null,
        ]);

        Assert.Equal(3, layout.Rows.Count);
        Assert.All(layout.Rows, r => Assert.Equal(LootLayout.Columns, r.Count));
        Assert.Equal(["Chaos Orb", null, "Divine Orb", null, null, null, null, null, null], layout.Rows[0]);
        Assert.All(layout.Rows[1], Assert.Null); // a gap in between stays
        Assert.Equal("I", layout.Rows[2][8]);
        Assert.Same(LootLayout.Empty, LootLayout.From([[null], []]));
        Assert.True(layout.SameAs(LootLayout.From(layout.Rows)));
    }
}
