using MediaRiffle.Server.Api;
using MediaRiffle.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Tests;

public class RecentlyAddedTests : IAsyncLifetime
{
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private AppDbContext db = null!;
    private MediaLibrary lib = null!, off = null!;
    private readonly DateTime now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        conn.Open();
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new AppUser { Id = "me", UserName = "me" });
        lib = new MediaLibrary { Name = "L", Path = "/l" };
        off = new MediaLibrary { Name = "Off", Path = "/off" };
        db.Libraries.AddRange(lib, off);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        conn.Dispose();
    }

    private async Task<Show> ShowWith(string title, MediaLibrary library, params double[] daysAgo)
    {
        var show = new Show { LibraryId = library.Id, Title = title, Folder = title };
        db.Shows.Add(show);
        await db.SaveChangesAsync();
        for (var i = 0; i < daysAgo.Length; i++)
            db.MediaFiles.Add(new MediaFile { LibraryId = library.Id, RelativePath = $"{title}/{i}.mkv", Kind = MediaKind.Episode,
                ShowId = show.Id, EpisodeNumber = i + 1, Title = $"{title} {i + 1}", AddedUtc = now.AddDays(-daysAgo[i]) });
        await db.SaveChangesAsync();
        return show;
    }

    private async Task<Movie> MovieAdded(string title, params double[] daysAgo)
    {
        var movie = new Movie { LibraryId = lib.Id, Title = title, GroupKey = title };
        db.Movies.Add(movie);
        await db.SaveChangesAsync();
        for (var i = 0; i < daysAgo.Length; i++)
            db.MediaFiles.Add(new MediaFile { LibraryId = lib.Id, RelativePath = $"{title}.{i}.mkv", Kind = MediaKind.Movie,
                MovieId = movie.Id, PartNumber = i + 1, Title = title, AddedUtc = now.AddDays(-daysAgo[i]) });
        await db.SaveChangesAsync();
        return movie;
    }

    [Fact]
    public async Task ShowsComeNewestFirst_OncePerShow_CountingTheEpisodesThatCameWithTheNewest()
    {
        var old = await ShowWith("Old", lib, 40, 41);
        // A season added today, and an episode from two months back
        var season = await ShowWith("Season", lib, 0, 0.01, 0.02, 60);
        var single = await ShowWith("Single", lib, 3);

        var added = await LibraryEndpoints.AddedShows(db, "me", new HashSet<int> { lib.Id });

        Assert.Equal([season.Id, single.Id, old.Id], added.Select(a => a.Id));
        Assert.Equal(3, added[0].Count);
        Assert.Equal("Season 1", added[0].Episode.Title);
        Assert.Equal(1, added[1].Count);
        Assert.Equal(2, added[2].Count);
    }

    [Fact]
    public async Task LeavesOutHiddenShowsAndSwitchedOffLibraries()
    {
        var hidden = await ShowWith("Hidden", lib, 0);
        await ShowWith("Elsewhere", off, 0);
        var kept = await ShowWith("Kept", lib, 5);
        db.Hidden.Add(new HiddenItem { UserId = "me", ShowId = hidden.Id, CreatedUtc = now });
        await db.SaveChangesAsync();

        var added = await LibraryEndpoints.AddedShows(db, "me", new HashSet<int> { lib.Id });

        Assert.Equal([kept.Id], added.Select(a => a.Id));
    }

    [Fact]
    public async Task MoviesComeNewestFirst_AMovieInPartsOnce()
    {
        var older = await MovieAdded("Older", 10);
        var parts = await MovieAdded("Parts", 1, 1.1);
        var hidden = await MovieAdded("Hidden", 0);
        db.Hidden.Add(new HiddenItem { UserId = "me", MovieId = hidden.Id, CreatedUtc = now });
        await db.SaveChangesAsync();

        var added = await LibraryEndpoints.AddedMovies(db, "me", new HashSet<int> { lib.Id });

        Assert.Equal([parts.Id, older.Id], added.Select(a => a.Id));
        Assert.Equal(2, added[0].Parts);
    }
}
