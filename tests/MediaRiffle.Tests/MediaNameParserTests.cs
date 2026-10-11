using MediaRiffle.Server.Library;

namespace MediaRiffle.Tests;

public class MediaNameParserTests
{
    [Theory]
    [InlineData("The Simpsons/Season 01/The Simpsons - S01E02 - Bart the Genius.mkv", "The Simpsons", 1, 2, "Bart the Genius")]
    [InlineData("Archer/Season 2/Archer.S02E10.720p.HDTV.x264.mkv", "Archer", 2, 10, "Episode 10")]
    [InlineData("Show/Season 03/3x07 Title Here.avi", "Show", 3, 7, "Title Here")]
    [InlineData("Show/Specials/Show - S00E01 - Pilot.mkv", "Show", 0, 1, "Pilot")]
    [InlineData("Show/Show S04E01.mp4", "Show", 4, 1, "Episode 1")]
    public void Episodes(string path, string show, int season, int episode, string title)
    {
        var e = MediaNameParser.ParseEpisode(path)!;
        Assert.Equal(show, e.Show);
        Assert.Equal(season, e.Season);
        Assert.Equal(episode, e.Episode);
        Assert.Equal(title, e.Title);
    }

    [Fact]
    public void EpisodeWithoutNumberKeepsItsFolderSeason()
    {
        var e = MediaNameParser.ParseEpisode("Show/Season 05/Some Special Thing.mkv")!;
        Assert.Equal(5, e.Season);
        Assert.Null(e.Episode);
        Assert.Equal("Some Special Thing", e.Title);
    }

    [Fact]
    public void FileInShowsRootIsNotAnEpisode() => Assert.Null(MediaNameParser.ParseEpisode("loose.mkv"));

    [Theory]
    // The original Tongue layout: HD/SD folders, and cd1/cd2 parts
    [InlineData("HD/The Big Lebowski.mkv", "The Big Lebowski", null, 1)]
    [InlineData("HD/The Matrix (1999).mkv", "The Matrix", 1999, 1)]
    [InlineData("SD/fargo/cd2.avi", "fargo", null, 2)]
    [InlineData("SD/American History X/American.History.X[1998] - cd1.avi", "American History X", null, 1)]
    [InlineData("Blade Runner 1982 1080p BluRay x264.mkv", "Blade Runner", 1982, 1)]
    [InlineData("2001 A Space Odyssey (1968).mkv", "2001 A Space Odyssey", 1968, 1)]
    public void Movies(string path, string title, int? year, int part)
    {
        var m = MediaNameParser.ParseMovie(path);
        Assert.Equal(title, m.Title);
        Assert.Equal(year, m.Year);
        Assert.Equal(part, m.Part);
    }

    [Fact]
    public void PartsOfOneMovieShareAGroup()
    {
        var a = MediaNameParser.ParseMovie("SD/American History X/American.History.X[1998] - cd1.avi");
        var b = MediaNameParser.ParseMovie("SD/American History X/American.History.X[1998] - cd2.avi");
        Assert.Equal(a.GroupKey, b.GroupKey);
        Assert.NotEqual(a.Part, b.Part);
    }

    [Fact]
    public void MoviesSideBySideAreSeparate()
    {
        var a = MediaNameParser.ParseMovie("HD/The Big Lebowski.mkv");
        var b = MediaNameParser.ParseMovie("HD/The Matrix.mkv");
        Assert.NotEqual(a.GroupKey, b.GroupKey);
    }
}
