using MediaRiffle.Server.Games;

namespace MediaRiffle.Tests;

public class GameArtTests
{
    [Theory]
    // GoodTools names (the user's sets) and libretro's No-Intro ones come to the same key
    [InlineData("7th Saga, The (U) [!]", "7th Saga, The (USA)")]
    [InlineData("2020 Super Baseball (U)", "2020 Super Baseball (USA)")]
    [InlineData("0001 Electroplankton (JP)", "Electroplankton (Japan)")]
    [InlineData("metal_slug_x_-_super_vehicle-001", "Metal Slug X - Super Vehicle-001 (USA)")]
    [InlineData("Bill & Ted's Excellent Game Boy Adventure - A Bogus Journey! (U)", "Bill _ Ted's Excellent Game Boy Adventure - A Bogus Journey! (USA, Europe)")]
    [InlineData("Pokemon - Red Version (U) [S][!]", "Pokemon - Red Version (USA, Europe) (SGB Enhanced)")]
    [InlineData("The Legend of Zelda", "Legend of Zelda, The (USA)")]
    [InlineData("Pokémon Snap (U)", "Pokemon Snap (USA)")]
    [InlineData("[PSP Minis] Zombie Tycoon", "Zombie Tycoon (USA) (PSP Minis)")]
    public void NamesFromBothSetsMatch(string ours, string libretro) => Assert.Equal(GameArt.Key(libretro), GameArt.Key(ours));

    [Fact]
    public void DifferentGamesStayApart()
    {
        Assert.NotEqual(GameArt.Key("Theme Park (U)"), GameArt.Key("Park (USA)"));
        Assert.NotEqual(GameArt.Key("Zoop (U)"), GameArt.Key("Zool (USA)"));
    }

    [Theory]
    [InlineData("Game (U) [!].sfc", "usa")]
    [InlineData("Game (JU).z64", "usa")]
    [InlineData("Game (E) [h1].gen", "europe")]
    [InlineData("Game (JP).nds", "japan")]
    [InlineData("Game (USA, Europe)", "usa")]
    [InlineData("Game (W)", "world")]
    [InlineData("Game (4) [h1]", null)]
    public void RegionsAreRead(string name, string? region) => Assert.Equal(region, GameArt.Region(name));

    [Fact]
    public void TheGamesOwnRegionIsPicked_ThenUsa_AndReleasesBeforeBetas()
    {
        string[] names = ["Game (Japan)", "Game (Europe)", "Game (USA) (Beta)", "Game (USA)", "Game (USA) (Rev 1)"];
        Assert.Equal("Game (Europe)", GameArt.Pick("Game (E).gen", names));
        Assert.Equal("Game (USA)", GameArt.Pick("Game.gen", names));
        Assert.Equal("Game (Japan)", GameArt.Pick("Game (J).gen", names));
        Assert.Equal("Game (USA) (Beta)", GameArt.Pick("Game.gen", ["Game (USA) (Beta)"]));
    }
}
