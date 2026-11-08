using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using MediaRiffle.Server.Options;

namespace MediaRiffle.Tests;

public class LibraryKindsTests
{
    static LibraryInfo Lib(params LibraryKind[] kinds) => new(1, "Mixed", kinds, "/media", [], true);

    [Theory]
    // With Shows and Movies, an episode by its name or season folder; the rest movies
    [InlineData("Show/Season 01/Show - S01E02 - Pilot.mkv", MediaKind.Episode)]
    [InlineData("Show/Show 1x03.mkv", MediaKind.Episode)]
    [InlineData("Show/Specials/Christmas.mkv", MediaKind.Episode)]
    [InlineData("Movie (1998)/Movie (1998).mkv", MediaKind.Movie)]
    [InlineData("Movie (1998).mkv", MediaKind.Movie)]
    [InlineData("Artist/Album/01 - Song.flac", MediaKind.Track)]
    public void FilesEachUnderWhatItIs(string path, MediaKind kind) =>
        Assert.Equal(kind, LibraryScanner.KindOf(Lib(LibraryKind.Shows, LibraryKind.Movies, LibraryKind.Music), path));

    [Fact]
    public void OneKindTakesAllItsFiles()
    {
        // A Shows library alone files every video as an episode, as before; a Movies one every video as a movie
        Assert.Equal(MediaKind.Episode, LibraryScanner.KindOf(Lib(LibraryKind.Shows), "Movie (1998)/Movie (1998).mkv"));
        Assert.Equal(MediaKind.Movie, LibraryScanner.KindOf(Lib(LibraryKind.Movies), "Show/Season 01/Show - S01E02.mkv"));
    }

    [Fact]
    public void KindsItDoesntHaveTakeNothing()
    {
        Assert.Null(LibraryScanner.KindOf(Lib(LibraryKind.Shows), "Artist/Album/01 - Song.mp3"));
        Assert.Null(LibraryScanner.KindOf(Lib(LibraryKind.Music), "Movie.mkv"));
        Assert.Null(LibraryScanner.KindOf(Lib(LibraryKind.Games), "Movie.mkv"));
        Assert.Null(LibraryScanner.KindOf(Lib(LibraryKind.Shows, LibraryKind.Music), "notes.txt"));
    }

    [Fact]
    public void KindsAreStoredInOrderWithTheFirstAsKind()
    {
        var lib = new MediaLibrary();
        lib.SetKinds([LibraryKind.Music, LibraryKind.Shows, LibraryKind.Music]);
        Assert.Equal(LibraryKind.Shows, lib.Kind);
        Assert.Equal([LibraryKind.Shows, LibraryKind.Music], lib.KindList);

        lib.SetKinds([LibraryKind.Games]);
        Assert.Null(lib.Kinds);
        Assert.Equal([LibraryKind.Games], lib.KindList);
    }

    [Fact]
    public void ALibraryFromBeforeKindsHasItsKind() =>
        Assert.Equal([LibraryKind.Movies], new MediaLibrary { Kind = LibraryKind.Movies }.KindList);

    [Fact]
    public void TabsComeFromEveryKind() =>
        Assert.Equal(["videos", "music"], Tabs.For([Lib(LibraryKind.Shows, LibraryKind.Movies, LibraryKind.Music)]));
}
