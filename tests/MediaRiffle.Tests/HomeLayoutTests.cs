using MediaRiffle.Server.Media;

namespace MediaRiffle.Tests;

public class HomeLayoutTests
{
    [Fact]
    public void TheDefaultOrderIsStoredAsNothing()
    {
        Assert.Null(HomeLayout.With(null, "shows", HomeLayout.Tabs["shows"]));
        Assert.Null(HomeLayout.With(null, "music", null));
        Assert.Equal(HomeLayout.Tabs["shows"], HomeLayout.Of(null)["shows"]);
        Assert.Equal(HomeLayout.Tabs["music"], HomeLayout.Of(null)["music"]);
    }

    [Fact]
    public void KeepsTheOrderAndHiddenMarks_AndAddsMissingSectionsAtTheEnd() =>
        Assert.Equal(["continue", "-libraries", "lists", "favorites", "categories"], HomeLayout.Normalize("shows", ["continue", "-libraries", "lists"]));

    [Fact]
    public void DropsUnknownAndRepeatedSections() =>
        Assert.Equal(["categories", "lists", "favorites", "libraries", "continue"],
            HomeLayout.Normalize("shows", ["categories", "bogus", "CATEGORIES", "-categories", "", null!]));

    [Fact]
    public void MusicHasNoContinueWatching() =>
        Assert.Equal(["libraries", "lists", "favorites", "categories"], HomeLayout.Normalize("music", ["continue", "libraries"]));

    [Fact]
    public void HidingTheDefaultOrderIsStillALayout() =>
        Assert.Equal("shows:lists,favorites,categories,-libraries,continue",
            HomeLayout.With(null, "shows", ["lists", "favorites", "categories", "-libraries", "continue"]));

    [Fact]
    public void EachTabKeepsItsOwnLayout()
    {
        var stored = HomeLayout.With(null, "shows", ["continue"]);
        stored = HomeLayout.With(stored, "music", ["-lists"]);
        Assert.Equal("shows:continue,lists,favorites,categories,libraries;music:-lists,favorites,categories,libraries", stored);
        Assert.Equal(["continue", "lists", "favorites", "categories", "libraries"], HomeLayout.Of(stored)["shows"]);
        // Back to the default, a tab is no longer stored
        Assert.Equal("music:-lists,favorites,categories,libraries", HomeLayout.With(stored, "shows", null));
    }

    [Fact]
    public void TheHomePageLayoutFromBeforeTheTabsCountsForEveryTab()
    {
        var old = "continue,-libraries,lists,favorites,categories";
        Assert.Equal(["continue", "-libraries", "lists", "favorites", "categories"], HomeLayout.Of(old)["shows"]);
        Assert.Equal(["-libraries", "lists", "favorites", "categories"], HomeLayout.Of(old)["music"]);
    }

    [Fact]
    public void AVideosLayoutFromBeforeShowsAndMoviesCountsForBoth()
    {
        var stored = "videos:continue,-libraries,lists,favorites,categories;music:-lists";
        Assert.Equal(["continue", "-libraries", "lists", "favorites", "categories"], HomeLayout.Of(stored)["shows"]);
        Assert.Equal(["continue", "-libraries", "lists", "favorites", "categories"], HomeLayout.Of(stored)["movies"]);
        // Shows changed since: that one's its own, Movies keeps the Videos one, and Videos is no longer stored
        var changed = HomeLayout.With(stored, "shows", ["lists"]);
        Assert.Equal(["lists", "favorites", "categories", "libraries", "continue"], HomeLayout.Of(changed)["shows"]);
        Assert.Equal(["continue", "-libraries", "lists", "favorites", "categories"], HomeLayout.Of(changed)["movies"]);
        Assert.DoesNotContain("videos", changed);
    }
}