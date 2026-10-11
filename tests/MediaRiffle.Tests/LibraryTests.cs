using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;

namespace MediaRiffle.Tests;

public class LibraryFilesTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "mr-lib");

    [Fact]
    public void ResolvesInsideTheLibrary() =>
        Assert.Equal(Path.Combine(Root, "Show", "ep.mkv"), LibraryFiles.Resolve(Root, "Show/ep.mkv"));

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("Show/../../etc/passwd")]
    [InlineData("../mr-lib-other/x.mkv")]   // a sibling folder whose name starts the same
    public void RefusesPathsOutsideTheLibrary(string rel) => Assert.Null(LibraryFiles.Resolve(Root, rel));
}

public class TagTests
{
    [Fact]
    public void TagsAreTrimmedAndDeduplicatedLikeMediaLibraryManager()
    {
        Assert.Equal("Cartoons, Kids", MediaLibrary.NormalizeTags(" Cartoons ,Kids, , kids,cartoons "));
        Assert.Null(MediaLibrary.NormalizeTags(" , "));
        Assert.Equal(["Comedy", "80s"], MediaLibrary.ParseTags("Comedy, 80s, comedy"));
    }
}
