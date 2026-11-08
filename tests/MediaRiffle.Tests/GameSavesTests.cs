using MediaRiffle.Server.Games;

namespace MediaRiffle.Tests;

public class GameSavesTests
{
    [Theory]
    // FNV-1a 32-bit's published values; the game page (game-saves.js) hashes the same way
    [InlineData("", "811c9dc5")]
    [InlineData("a", "e40c292c")]
    [InlineData("foobar", "bf9cf968")]
    public void HashIsFnv1a(string text, string hash) => Assert.Equal(hash, GameSaves.Hash(System.Text.Encoding.ASCII.GetBytes(text)));
}
