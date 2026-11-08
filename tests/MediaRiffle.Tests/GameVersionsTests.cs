using MediaRiffle.Server.Library;

namespace MediaRiffle.Tests;

public class GameVersionsTests
{
    [Theory]
    [InlineData("Legend of Zelda, The - Link's Awakening DX (U) (V1.0) [C][T+Spa][b1]", "Legend of Zelda, The - Link's Awakening DX")]
    [InlineData("2333 Kung Fu Panda (US)", "Kung Fu Panda")]
    [InlineData("Final Fantasy II (U) (V1.1)", "Final Fantasy II")]
    [InlineData("Final Fantasy II", "Final Fantasy II")]
    [InlineData("[PSP Minis] Zombie Tycoon", "Zombie Tycoon")]
    [InlineData("(Hack)", "(Hack)")]
    public void NameDropsTheTags(string title, string name) => Assert.Equal(name, GameVersions.Name(title));

    [Theory]
    // Versions of one game share a key
    [InlineData("Final Fantasy II (U) (V1.1)", "Final Fantasy II")]
    [InlineData("2333 Kung Fu Panda (US)", "2418 Kung Fu Panda (DE)")]
    [InlineData("Pocket Monsters Kin (J) (V1.0) [C][b1]", "Pocket Monsters Kin (J) (V1.0) [C][h1] (Sonic)")]
    public void VersionsShareAKey(string a, string b) => Assert.Equal(GameVersions.Key(a), GameVersions.Key(b));

    [Theory]
    // and other games don't
    [InlineData("Super Mario World (U)", "Super Mario World 2 - Yoshi's Island")]
    [InlineData("Final Fantasy II (U)", "Final Fantasy III (U) (V1.1)")]
    public void OtherGamesDont(string a, string b) => Assert.NotEqual(GameVersions.Key(a), GameVersions.Key(b));

    [Fact]
    public void BestFirst()
    {
        string[] files =
        [
            "Zelda (G) [b1].gb",
            "Zelda (U) (V1.0) [T+Ita1.2_SadNES cITy].gb",
            "Zelda (J).gb",
            "Zelda (U) (V1.0).gb",
            "Zelda (U) (V1.1) [!].gb",
            "Zelda (G) [!].gb",
            "Zelda (U) (Beta).gb",
        ];
        Assert.Equal(
            ["Zelda (U) (V1.1) [!].gb", "Zelda (U) (V1.0).gb", "Zelda (G) [!].gb", "Zelda (J).gb",
             "Zelda (U) (V1.0) [T+Ita1.2_SadNES cITy].gb", "Zelda (U) (Beta).gb", "Zelda (G) [b1].gb"],
            GameVersions.Best(files, f => f));
    }

    [Fact]
    public void UntaggedComesAfterEnglishReleases()
    {
        Assert.Equal(["Final Fantasy II (U) (V1.1).smc", "Final Fantasy II.smc"],
            GameVersions.Best(["Final Fantasy II.smc", "Final Fantasy II (U) (V1.1).smc"], f => f));
    }

    [Theory]
    [InlineData("Zelda (U) (V1.1) [!]", "USA · v1.1 · verified")]
    [InlineData("Zelda (G) [C][b1]", "Germany · bad dump")]
    [InlineData("Zelda (U) (V1.0) [C][T+Spa][b1]", "USA · v1.0 · Spanish translation · bad dump")]
    [InlineData("Pocket Monsters Gin (J) (V1.0) [C][T-Eng][a2][h1C]", "Japan · v1.0 · English translation (unfinished) · alternate dump · hack")]
    [InlineData("Pocket Monsters Kin (J) (V1.0) [C][h1] (Sonic)", "Japan · v1.0 · hack · Sonic")]
    [InlineData("2383 WALL-E (US)(M2)", "USA · 2 languages")]
    [InlineData("Some Game (USA, Europe) (Rev A)", "USA, Europe · Rev A")]
    [InlineData("Zelda (U) (V1.2) [T+Spa1.0_DJTraducciones]", "USA · v1.2 · Spanish translation 1.0 by DJTraducciones")]
    [InlineData("Zelda (U) (V1.0) [T+Ita1.2_SadNES cITy]", "USA · v1.0 · Italian translation 1.2 by SadNES cITy")]
    [InlineData("Zelda (U) (V1.0) [T+Spa_Juanjo]", "USA · v1.0 · Spanish translation by Juanjo")]
    [InlineData("Zelda (U) [T+PorBR]", "USA · Brazilian Portuguese translation")]
    [InlineData("Final Fantasy II", "")]
    public void LabelsInWords(string name, string label) => Assert.Equal(label, GameVersions.Label(name));

    [Fact]
    public void SameLabelsGetTheirFileNames()
    {
        var labels = GameVersions.Labels(["A (U) [h1].gb", "A (U) [h2].gb", "A.gb", "A (J).gb"]);
        Assert.Equal(["USA · hack — A (U) [h1].gb", "USA · hack — A (U) [h2].gb", "A.gb", "Japan"], labels);
    }
}
