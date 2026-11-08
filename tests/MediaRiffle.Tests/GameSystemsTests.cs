using MediaRiffle.Server.Library;

namespace MediaRiffle.Tests;

public class GameSystemsTests
{
    [Theory]
    [InlineData("Super Mario World (USA).sfc", "snes")]
    [InlineData("anything/Zelda.Z64", "n64")]
    [InlineData("Handhelds/Pokemon Red.gb", "gb")]
    [InlineData("Pokemon Gold.gbc", "gbc")]
    [InlineData("Sonic.gen", "segaMD")]
    public void ItsOwnExtensionDecides(string path, string system) => Assert.Equal(system, GameSystems.Classify(path)?.Id);

    [Theory]
    [InlineData("Sega Genesis/Sonic.zip", "segaMD")]
    [InlineData("SEGA_GENESIS/Sonic.bin", "segaMD")]
    [InlineData("Sony - PlayStation/Crash/Crash Bandicoot.cue", "psx")]
    [InlineData("PlayStation 2/God of War.iso", "ps2")]
    [InlineData("PSP/Patapon.iso", "psp")]
    [InlineData("Roms/snes/Super Metroid.zip", "snes")]
    public void ASharedExtensionNeedsAFolderNamedForTheSystem(string path, string system) =>
        Assert.Equal(system, GameSystems.Classify(path)?.Id);

    [Theory]
    [InlineData("Sonic.zip")]
    [InlineData("Docs/readme.txt")]
    [InlineData("Genesis/cover.png")]
    [InlineData("PlayStation/bios/scph5501.bin")]
    [InlineData("SNES/Boxart/Mario.sfc")]
    [InlineData("SNES/EMULATOR/snes9x-1.53-x64.zip")]
    [InlineData("Xbox360_homebrew/GTA/data/maps/city.col")]
    [InlineData("(PSP) Gameboy Emulator/EBOOT.PBP")]
    [InlineData("noextension")]
    public void OtherFilesArentGames(string path) => Assert.Null(GameSystems.Classify(path));

    [Theory]
    [InlineData("Super Mario World (USA) [!].sfc", "Super Mario World (USA)")]
    [InlineData("Sonic (W) [b1].gen", "Sonic (W) [b1]")]
    [InlineData("[!].bin", "[!]")]
    public void TitlesDropOnlyTheGoodDumpMark(string file, string title) => Assert.Equal(title, GameSystems.Title(file));

    [Fact]
    public void AZipBesideTheSameGameUnpackedIsLeftOut() =>
        Assert.Equal(["DS/0001 Electroplankton (JP).zip"], GameSystems.Repacked(
            ["DS/0001 Electroplankton (JP).nds", "DS/0001 Electroplankton (JP).zip", "DS/0002 Other.zip", "Genesis/Sonic.zip"]));

    [Fact]
    public void FoldersWithProgramsAndNoGamesAreSkipped()
    {
        var programs = GameSystems.ProgramFolders([
            "PSX/duckstation/duckstation-qt.exe", "PSX/duckstation/resources/cheats.zip",
            "SNES/snes9x.exe", "SNES/Mario.smc", "setup.exe", "PSX/Roms/game.bin"]);
        Assert.Equal(["PSX/duckstation/"], programs);
        Assert.True(GameSystems.IsUnder("PSX/duckstation/resources/cheats.zip", programs));
        Assert.False(GameSystems.IsUnder("PSX/Roms/game.bin", programs));
        Assert.False(GameSystems.IsUnder("SNES/Mario.smc", programs));
    }

    [Fact]
    public void ACueNamesItsTracks_AndAnM3uItsDiscs()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var cue = Path.Combine(dir.FullName, "Game.cue");
            File.WriteAllText(cue, "FILE \"Game (Track 1).bin\" BINARY\n  TRACK 01 MODE2/2352\nFILE Game2.bin BINARY\n");
            Assert.Equal(["Game (Track 1).bin", "Game2.bin"], GameSystems.PartsOf(cue));
            var m3u = Path.Combine(dir.FullName, "Game.m3u");
            File.WriteAllText(m3u, "#EXTM3U\nDisc 1/Game (Disc 1).cue\r\nGame (Disc 2).chd\n");
            Assert.Equal(["Disc 1/Game (Disc 1).cue", "Game (Disc 2).chd"], GameSystems.PartsOf(m3u));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void PicturesAreFoundBesideTheGameOrInABoxartFolder()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "SNES/Mario.sfc", "SNES/Named_Boxarts/Mario.png", "SNES/Zelda.sfc", "SNES/zelda.JPG", "SNES/Metroid.sfc" };
        Assert.Equal("SNES/Named_Boxarts/Mario.png", GameSystems.FindPicture("SNES/Mario.sfc", files));
        Assert.Equal("SNES/zelda.JPG", GameSystems.FindPicture("SNES/Zelda.sfc", files));
        Assert.Null(GameSystems.FindPicture("SNES/Metroid.sfc", files));
    }
}
