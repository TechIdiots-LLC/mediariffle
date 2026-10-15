using MediaRiffle.Server.Api;
using MediaRiffle.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Tests;

public class HiddenTests : IAsyncLifetime
{
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private AppDbContext db = null!;
    private Show keep = null!, gone = null!;
    private Movie movie = null!;
    private MediaFile e1 = null!, e2 = null!, g1 = null!, m1 = null!;

    // A show with two episodes (the second hidden on its own), a hidden show, a hidden movie, and another user's hidden episode
    public async Task InitializeAsync()
    {
        conn.Open();
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        db.Users.AddRange(new AppUser { Id = "me", UserName = "me" }, new AppUser { Id = "other", UserName = "other" });
        var lib = new MediaLibrary { Name = "L", Path = "/l" };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        keep = new Show { LibraryId = lib.Id, Title = "Keep", Folder = "Keep" };
        gone = new Show { LibraryId = lib.Id, Title = "Gone", Folder = "Gone" };
        movie = new Movie { LibraryId = lib.Id, Title = "M", GroupKey = "M" };
        db.AddRange(keep, gone, movie);
        await db.SaveChangesAsync();
        var season = new Season { ShowId = keep.Id, Number = 1, Title = "Season 1" };
        db.Seasons.Add(season);
        await db.SaveChangesAsync();
        MediaFile File(string path, int? show = null, int? mov = null, int? ep = null) =>
            new() { LibraryId = lib.Id, RelativePath = path, ShowId = show, MovieId = mov, EpisodeNumber = ep, Title = path };
        e1 = File("Keep/1.mkv", keep.Id, ep: 1);
        e2 = File("Keep/2.mkv", keep.Id, ep: 2);
        e2.SeasonId = season.Id;
        g1 = File("Gone/1.mkv", gone.Id);
        m1 = File("M.mkv", mov: movie.Id);
        db.MediaFiles.AddRange(e1, e2, g1, m1);
        await db.SaveChangesAsync();

        var t = DateTime.UtcNow;
        db.Hidden.AddRange(
            new HiddenItem { UserId = "me", MediaFileId = e2.Id, CreatedUtc = t },
            new HiddenItem { UserId = "me", ShowId = gone.Id, CreatedUtc = t.AddMinutes(-1) },
            new HiddenItem { UserId = "me", MovieId = movie.Id, CreatedUtc = t.AddMinutes(-2) },
            new HiddenItem { UserId = "other", MediaFileId = e1.Id, CreatedUtc = t });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        conn.Dispose();
    }

    [Fact]
    public async Task NotHiddenLeavesOutHiddenEpisodesShowsAndMoviesForThatUserOnly()
    {
        var mine = await HiddenEndpoints.NotHidden(db, "me", db.MediaFiles).Select(f => f.Id).ToListAsync();
        var theirs = await HiddenEndpoints.NotHidden(db, "other", db.MediaFiles).Select(f => f.Id).OrderBy(x => x).ToListAsync();

        Assert.Equal([e1.Id], mine);
        Assert.Equal(new[] { e2.Id, g1.Id, m1.Id }.Order(), theirs);
    }

    [Fact]
    public async Task ListAsyncNamesEachHiddenThingNewestFirst()
    {
        var list = await HiddenEndpoints.ListAsync(db, "me");

        Assert.Equal(
            [
                new HiddenEndpoints.HiddenEntry("file", e2.Id, "Keep/2.mkv", "Keep S01E02", list[0].CreatedUtc),
                new HiddenEndpoints.HiddenEntry("show", gone.Id, "Gone", null, list[1].CreatedUtc),
                new HiddenEndpoints.HiddenEntry("movie", movie.Id, "M", null, list[2].CreatedUtc),
            ],
            list);
    }
}
