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
        Assert.Equal(["videos", "games"], Tabs.For([Lib(LibraryKind.Games), Lib(LibraryKind.Movies), Lib(LibraryKind.Music, enabled: false)]));
        Assert.Equal(["videos", "music"], Tabs.For([Lib(LibraryKind.Music), Lib(LibraryKind.Shows)]));
    }
}
