using MediaRiffle.Server.Media;

namespace MediaRiffle.Tests;

public class HomeLayoutTests
{
    [Fact]
    public void TheDefaultOrderIsStoredAsNothing()
    {
        Assert.Null(HomeLayout.With(null, "videos", HomeLayout.Tabs["videos"]));
        Assert.Null(HomeLayout.With(null, "music", null));
        Assert.Equal(HomeLayout.Tabs["videos"], HomeLayout.Of(null)["videos"]);
        Assert.Equal(HomeLayout.Tabs["music"], HomeLayout.Of(null)["music"]);
    }

    [Fact]
    public void KeepsTheOrderAndHiddenMarks_AndAddsMissingSectionsAtTheEnd() =>
        Assert.Equal(["continue", "-libraries", "lists", "favorites", "categories"], HomeLayout.Normalize("videos", ["continue", "-libraries", "lists"]));

    [Fact]
    public void DropsUnknownAndRepeatedSections() =>
        Assert.Equal(["categories", "lists", "favorites", "libraries", "continue"],
            HomeLayout.Normalize("videos", ["categories", "bogus", "CATEGORIES", "-categories", "", null!]));

    [Fact]
    public void MusicHasNoContinueWatching() =>
        Assert.Equal(["libraries", "lists", "favorites", "categories"], HomeLayout.Normalize("music", ["continue", "libraries"]));

    [Fact]
    public void HidingTheDefaultOrderIsStillALayout() =>
        Assert.Equal("videos:lists,favorites,categories,-libraries,continue",
            HomeLayout.With(null, "videos", ["lists", "favorites", "categories", "-libraries", "continue"]));

    [Fact]
    public void EachTabKeepsItsOwnLayout()
    {
        var stored = HomeLayout.With(null, "videos", ["continue"]);
        stored = HomeLayout.With(stored, "music", ["-lists"]);
        Assert.Equal("videos:continue,lists,favorites,categories,libraries;music:-lists,favorites,categories,libraries", stored);
        Assert.Equal(["continue", "lists", "favorites", "categories", "libraries"], HomeLayout.Of(stored)["videos"]);
        // Back to the default, a tab is no longer stored
        Assert.Equal("music:-lists,favorites,categories,libraries", HomeLayout.With(stored, "videos", null));
    }

    [Fact]
    public void TheHomePageLayoutFromBeforeTheTabsCountsForEveryTab()
    {
        var old = "continue,-libraries,lists,favorites,categories";
        Assert.Equal(["continue", "-libraries", "lists", "favorites", "categories"], HomeLayout.Of(old)["videos"]);
        Assert.Equal(["-libraries", "lists", "favorites", "categories"], HomeLayout.Of(old)["music"]);
    }
}
