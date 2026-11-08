using MediaRiffle.Server.Library;
using MediaRiffle.Server.Options;

namespace MediaRiffle.Tests;

public class TabsTests
{
    private static LibraryInfo Lib(LibraryKind kind, bool enabled = true) => new(1, kind.ToString(), [kind], "/x", [], enabled);

    [Fact]
    public void ATabShowsOnlyWithAnEnabledLibraryOfItsKind()
    {
        Assert.Empty(Tabs.For([]));
        Assert.Equal(["movies", "games"], Tabs.For([Lib(LibraryKind.Games), Lib(LibraryKind.Movies), Lib(LibraryKind.Music, enabled: false)]));
        Assert.Equal(["shows", "music"], Tabs.For([Lib(LibraryKind.Music), Lib(LibraryKind.Shows)]));
    }

    [Fact]
    public void EachTabHoldsItsOwnKindOfFile()
    {
        Assert.Equal([Server.Data.MediaKind.Episode], Tabs.Kinds("shows"));
        Assert.Equal([Server.Data.MediaKind.Movie], Tabs.Kinds("movies"));
        // Videos, as links and apps from before Shows and Movies had a tab each still ask: both
        Assert.Equal([Server.Data.MediaKind.Episode, Server.Data.MediaKind.Movie], Tabs.Kinds("videos"));
        Assert.Null(Tabs.Kinds(null));
    }
}
