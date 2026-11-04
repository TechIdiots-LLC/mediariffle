using MediaRiffle.Server.Media;

namespace MediaRiffle.Tests;

public class HomeLayoutTests
{
    [Fact]
    public void TheDefaultOrderIsStoredAsNothing()
    {
        Assert.Null(HomeLayout.Normalize(HomeLayout.Sections));
        Assert.Null(HomeLayout.Normalize(null));
        Assert.Equal(HomeLayout.Sections, HomeLayout.Of(null));
    }

    [Fact]
    public void KeepsTheOrderAndHiddenMarks_AndAddsMissingSectionsAtTheEnd()
    {
        Assert.Equal("continue,-libraries,lists,favorites,categories", HomeLayout.Normalize(["continue", "-libraries", "lists"]));
    }

    [Fact]
    public void DropsUnknownAndRepeatedSections()
    {
        Assert.Equal("categories,lists,favorites,libraries,continue", HomeLayout.Normalize(["categories", "bogus", "CATEGORIES", "-categories", "", null!]));
    }

    [Fact]
    public void HidingTheDefaultOrderIsStillALayout() =>
        Assert.Equal("lists,favorites,categories,-libraries,continue", HomeLayout.Normalize(["lists", "favorites", "categories", "-libraries", "continue"]));
}
