using MediaRiffle.Server.Api;
using MediaRiffle.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Tests;

public class CategoryTests : IAsyncLifetime
{
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private AppDbContext db = null!;
    private int tagged, other, movieFile;

    public async Task InitializeAsync()
    {
        conn.Open();
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        var lib = new MediaLibrary { Name = "L", Path = "/l" };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var show = new Show { LibraryId = lib.Id, Title = "Picked", Folder = "Picked" };
        var movie = new Movie { LibraryId = lib.Id, Title = "M", GroupKey = "M" };
        db.AddRange(show, movie, new Show { LibraryId = lib.Id, Title = "Other", Folder = "Other" });
        await db.SaveChangesAsync();
        MediaFile File(string path, int? s = null, int? m = null) =>
            new() { LibraryId = lib.Id, RelativePath = path, ShowId = s, MovieId = m, Title = path };
        var a = File("Picked/1.mkv", show.Id); var b = File("Other/1.mkv", show.Id + 1); var c = File("M.mkv", m: movie.Id);
        db.MediaFiles.AddRange(a, b, c);
        db.CategoryItems.AddRange(new CategoryItem { Tag = "Kids", ShowId = show.Id }, new CategoryItem { Tag = "Kids", MovieId = movie.Id });
        await db.SaveChangesAsync();
        (tagged, other, movieFile) = (a.Id, b.Id, c.Id);
    }

    public Task DisposeAsync() { db.Dispose(); conn.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task ACategoryHoldsTheFilesOfWhatWasPicked()
    {
        var files = await ListEndpoints.FilesIn(db, db.CategoryItems.Where(c => c.Tag == "kids"), db.MediaFiles).Select(f => f.Id).ToListAsync();
        Assert.Equal([tagged, movieFile], files.Order());
        Assert.DoesNotContain(other, files);
    }

    [Fact]
    public async Task TheQueriesTheEndpointsMakeTranslate()
    {
        // What /queue does for a tag: tagged libraries' files joined with the category's, then filtered and counted
        var ids = new HashSet<int> { 12345 };
        var made = db.CategoryItems.Where(c => c.Tag == "Kids");
        var q = db.MediaFiles.Where(f => ids.Contains(f.LibraryId)).Union(ListEndpoints.FilesIn(db, made, db.MediaFiles));
        Assert.Equal(2, await q.Where(f => f.ProbeError == null).CountAsync());
        // And the page: shows picked, unless their library is off
        var enabled = new HashSet<int>(await db.Libraries.Select(l => l.Id).ToListAsync());
        Assert.Equal(["Picked"], await db.Shows.Where(s => ids.Contains(s.LibraryId) || (enabled.Contains(s.LibraryId) && made.Any(c => c.ShowId == s.Id))).Select(s => s.Title).ToListAsync());
        Assert.Empty(await db.Shows.Where(s => ids.Contains(s.LibraryId) || (new HashSet<int>().Contains(s.LibraryId) && made.Any(c => c.ShowId == s.Id))).ToListAsync());
    }

    [Fact]
    public async Task ACategoryGoesWithWhatItTags()
    {
        db.Shows.RemoveRange(db.Shows.Where(s => s.Title == "Picked"));
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.CategoryItems.CountAsync());
    }
}
