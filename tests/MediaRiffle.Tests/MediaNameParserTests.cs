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

public class ShowSeriesTests
{
    // A show's other series in its folders ("UK - Season 01") and loose files named for them, as in a real library
    [Theory]
    [InlineData("Ru Pauls Drag Race/Season 01/RuPauls.Drag.Race.S01E02.mkv", "", 1, 2)]
    [InlineData("Ru Pauls Drag Race/All Stars - Season 02/RuPauls.Drag.Race.All.Stars.S02E01.mkv", "All Stars", 2, 1)]
    [InlineData("Ru Pauls Drag Race/Canada - Season 01/Canadas.Drag.Race.S01E03.mkv", "Canada", 1, 3)]
    [InlineData("Ru Pauls Drag Race/Canada vs The World - Season 02/x.S02E04.mkv", "Canada vs The World", 2, 4)]
    [InlineData("Ru Pauls Drag Race/Down Under - S03/Drag.Race.Down.Under.S03E05.mkv", "Down Under", 3, 5)]
    [InlineData("Ru Pauls Drag Race/Global All Stars Season 01/x.S01E06.mkv", "Global All Stars", 1, 6)]
    [InlineData("Ru Pauls Drag Race/Secret Celebrity - S02/x.S02E01.mkv", "Secret Celebrity", 2, 1)]
    [InlineData("Ru Pauls Drag Race/Philippines Slaysian Royale - Season 01/x.S01E01.mkv", "Philippines Slaysian Royale", 1, 1)]
    // The folder's season wins over the file's
    [InlineData("Ru Pauls Drag Race/UK - Season 07/x.S01E02.mkv", "UK", 7, 2)]
    // Loose in the show's folder: named for another series, or for the show itself
    [InlineData("Ru Pauls Drag Race/drag.race.espana.s01e01.1080p.web.h264.mkv", "Drag Race Espana", 1, 1)]
    [InlineData("Ru Pauls Drag Race/drag.race.espana.all.stars.s01e02.1080p.mkv", "Drag Race Espana All Stars", 1, 2)]
    [InlineData("Ru Pauls Drag Race/RuPauls.Drag.Race.S05E01.mkv", "", 5, 1)]
    [InlineData("The Office/The.Office.US.S02E01.mkv", "", 2, 1)]
    // Plain season folders and specials stay the show's own
    [InlineData("Show/Season 3/Show S03E01.mkv", "", 3, 1)]
    [InlineData("Show/Specials/Show S00E01.mkv", "", 0, 1)]
    public void SeriesAreToldApart(string path, string series, int season, int episode)
    {
        var e = MediaNameParser.ParseEpisode(path)!;
        Assert.Equal(series, e.Series);
        Assert.Equal(season, e.Season);
        Assert.Equal(episode, e.Episode);
    }

    [Fact]
    public void SeasonTitlesNameTheirSeries()
    {
        Assert.Equal("Season 2", MediaRiffle.Server.Data.Season.TitleFor("", 2));
        Assert.Equal("UK · Season 2", MediaRiffle.Server.Data.Season.TitleFor("UK", 2));
        Assert.Equal("All Stars · Specials", MediaRiffle.Server.Data.Season.TitleFor("All Stars", 0));
    }
}
