using System.Text;
using MediaRiffle.Server.Api;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using MediaRiffle.Server.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Tests;

public class ListFileTests : IAsyncLifetime
{
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private AppDbContext db = null!;
    private MediaLibrary lib = null!, off = null!;
    private Movie heat = null!, heat2 = null!, hidden = null!;
    private Show office = null!;
    private MediaFile song = null!;
    private Game mario = null!;
    private const string User = "u1";

    public async Task InitializeAsync()
    {
        conn.Open();
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new AppUser { Id = User, UserName = "u" });
        lib = new MediaLibrary { Name = "Media", Path = "/m" };
        off = new MediaLibrary { Name = "Old", Path = "/o", Enabled = false };
        db.Libraries.AddRange(lib, off);
        await db.SaveChangesAsync();
        heat = new Movie { LibraryId = lib.Id, Title = "Heat", Year = 1995, GroupKey = "heat 1995" };
        heat2 = new Movie { LibraryId = lib.Id, Title = "Heat", Year = 1986, GroupKey = "heat 1986" };
        hidden = new Movie { LibraryId = off.Id, Title = "Gone", GroupKey = "gone" };
        office = new Show { LibraryId = lib.Id, Title = "The Office", Folder = "The Office" };
        var artist = new Artist { LibraryId = lib.Id, Name = "Björk" };
        db.AddRange(heat, heat2, hidden, office, artist);
        await db.SaveChangesAsync();
        var album = new Album { LibraryId = lib.Id, ArtistId = artist.Id, Title = "Post", GroupKey = "post", Folder = "Björk/Post" };
        mario = new Game { LibraryId = lib.Id, System = "nes", Title = "Super Mario Bros.", RelativePath = "NES/Super Mario Bros. (World).nes" };
        db.AddRange(album, mario);
        await db.SaveChangesAsync();
        song = new MediaFile { LibraryId = lib.Id, Kind = MediaKind.Track, AlbumId = album.Id, Title = "Army of Me", RelativePath = "Björk/Post/01.flac" };
        db.MediaFiles.AddRange(song, new MediaFile { LibraryId = lib.Id, Kind = MediaKind.Movie, MovieId = heat.Id, RelativePath = "Heat (1995).mkv", Title = "Heat" });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() { db.Dispose(); conn.Dispose(); return Task.CompletedTask; }

    private IReadOnlyCollection<LibraryInfo> Enabled => [new LibraryInfo(lib.Id, lib.Name, [LibraryKind.Movies], lib.Path, [], true)];

    private static (string?, string?) AsIs(string name) => name.Length == 0 ? (null, "No list name.") : (name, null);

    private async Task<ListFile.Result> Import(string csv, bool replace = false)
    {
        var (rows, error) = ListFile.Parse(csv);
        Assert.Null(error);
        var plan = await ListFile.ResolveAsync(db, rows!, Enabled, AsIs);
        var existing = await db.UserTags.Where(t => t.UserId == User).ToListAsync();
        var result = await ListFile.ApplyAsync(db, existing, plan, replace, new HashSet<int> { lib.Id }, (tag, e, when) => new UserTag
        {
            UserId = User, Tag = tag, CreatedUtc = when,
            ShowId = e.ShowId, MovieId = e.MovieId, ArtistId = e.ArtistId, AlbumId = e.AlbumId, MediaFileId = e.MediaFileId, GameId = e.GameId,
        });
        await db.SaveChangesAsync();
        return result;
    }

    private Task<List<string>> Titles(string tag) => db.UserTags.Where(t => t.UserId == User && t.Tag == tag).OrderBy(t => t.CreatedUtc)
        .Select(t => t.MovieId != null ? db.Movies.First(m => m.Id == t.MovieId).Title + " " + db.Movies.First(m => m.Id == t.MovieId).Year
            : t.ShowId != null ? "show" : t.MediaFileId != null ? "song" : t.GameId != null ? "game" : "other").ToListAsync();

    [Fact]
    public async Task AnExportImportsBackToTheSameThings()
    {
        db.UserTags.AddRange(
            new UserTag { UserId = User, Tag = "Mine", MovieId = heat.Id, CreatedUtc = DateTime.UtcNow.AddMinutes(-3) },
            new UserTag { UserId = User, Tag = "Mine", ShowId = office.Id, CreatedUtc = DateTime.UtcNow.AddMinutes(-2) },
            new UserTag { UserId = User, Tag = "Mine", MediaFileId = song.Id, CreatedUtc = DateTime.UtcNow.AddMinutes(-1) },
            new UserTag { UserId = User, Tag = "Mine", GameId = mario.Id });
        await db.SaveChangesAsync();
        var entries = await db.UserTags.OrderBy(t => t.CreatedUtc).ToListAsync();
        var rows = await ListFile.RowsAsync(db, entries.Select(e => (e.Tag, (ITagged)e)).ToList());
        var csv = ListFile.Decode(ListFile.ToCsv("List", rows));
        Assert.StartsWith("List,Kind,Title,Year,Artist,Album,System,Library,File\r\n", csv);
        Assert.Contains("Mine,Movie,Heat,1995,,,,Media,Heat (1995).mkv", csv);
        Assert.Contains("Mine,Song,Army of Me,,Björk,Post,,Media,Björk/Post/01.flac", csv);

        // Into an empty account: the same four, in the same order
        db.UserTags.RemoveRange(db.UserTags);
        await db.SaveChangesAsync();
        var result = await Import(csv.Replace("Mine,", "Again,"));
        Assert.Equal((1, 4, 0, 0), (result.Lists, result.Added, result.Removed, result.UnmatchedCount));
        Assert.Equal(["Heat 1995", "show", "song", "game"], await Titles("Again"));
    }

    [Fact]
    public async Task RowsMatchByTitleAndYearAndSayWhatTheyCouldNotFind()
    {
        var result = await Import("""
            list;kind;title;year
            Films;movie;heat;1986
            Films;Film;  The   office ;
            Films;Movie;Nope;
            Films;Spaceship;Heat;
            ;Movie;Heat;
            Films;Movie;Gone;
            """);
        Assert.Equal(1, await db.UserTags.CountAsync(t => t.MovieId == heat2.Id));
        Assert.Equal(0, await db.UserTags.CountAsync(t => t.MovieId == heat.Id));
        // "The office" is a show, not a movie
        Assert.Equal(1, result.Added);
        Assert.Equal(5, result.UnmatchedCount);
        // Gone is in a library that's switched off
        Assert.Equal([3, 4, 5, 6, 7], result.Unmatched.Select(u => u.Line));
    }

    [Fact]
    public async Task ATitleWithNoYearHasEveryMatchAndAFileOrYearPicksOne()
    {
        await Import("List,Kind,Title\nBoth,Movie,Heat\n");
        Assert.Equal(2, await db.UserTags.CountAsync(t => t.Tag == "Both"));
        await Import("List,Kind,Title,File\nOne,Movie,Heat,heat (1995).MKV\n");
        Assert.Equal(["Heat 1995"], await Titles("One"));
        // A title that's wrong, with a file that's right
        await Import("List,Kind,Title,File\nGames,Game,Mario,NES\\Super Mario Bros. (World).nes\n");
        Assert.Equal(["game"], await Titles("Games"));
    }

    [Fact]
    public async Task ReplaceMakesAListWhatTheFileSaysAndLeavesOthersAlone()
    {
        db.UserTags.AddRange(
            new UserTag { UserId = User, Tag = "Mine", MovieId = heat2.Id, CreatedUtc = DateTime.UtcNow.AddMinutes(-2) },
            new UserTag { UserId = User, Tag = "Mine", MovieId = heat.Id, CreatedUtc = DateTime.UtcNow.AddMinutes(-1) },
            new UserTag { UserId = User, Tag = "Mine", MovieId = hidden.Id },
            new UserTag { UserId = User, Tag = "Other", ShowId = office.Id });
        await db.SaveChangesAsync();
        var result = await Import("List,Kind,Title,Year\nmine,Movie,Heat,1995\nmine,Show,The Office,\n", replace: true);
        Assert.Equal((2, 1, 1), (result.Added + result.Kept, result.Added, result.Removed));
        // In the file's order, under the list's own spelling; what's in a switched-off library stays
        Assert.Equal(["Gone ", "Heat 1995", "show"], await Titles("Mine"));
        Assert.All(await db.UserTags.Where(t => t.Tag == "mine").Select(t => t.Tag).ToListAsync(), t => Assert.Equal("Mine", t));
        Assert.Equal(1, await db.UserTags.CountAsync(t => t.Tag == "Other"));
    }

    [Fact]
    public async Task AddLeavesWhatIsOnTheList()
    {
        db.UserTags.Add(new UserTag { UserId = User, Tag = "Mine", MovieId = heat2.Id });
        await db.SaveChangesAsync();
        var result = await Import("List,Kind,Title,Year\nMine,Movie,Heat,1995\nMine,Movie,Heat,1986\n");
        Assert.Equal((1, 1, 0), (result.Added, result.Kept, result.Removed));
        Assert.Equal(2, await db.UserTags.CountAsync(t => t.Tag == "Mine"));
    }

    [Fact]
    public void QuotedFieldsAndOtherSpellingsOfTheFileRead()
    {
        var (rows, _) = ListFile.Parse("﻿Title,\"List\",Kind\r\n\"Say \"\"Hi\"\", then\nleave\",\"A, B\",Movie\r\n,,\r\n");
        var row = Assert.Single(rows!).Row;
        Assert.Equal(("Say \"Hi\", then\nleave", "A, B", "Movie"), (row.Title, row.List, row.Kind));
        Assert.NotNull(ListFile.Parse("Name,Kind\nx,Movie").Error);

        // A spreadsheet's plain CSV on Windows is Windows-1252, and its Unicode text is UTF-16 with tabs
        var cp1252 = Encoding.Latin1.GetBytes("List,Kind,Title\nA,Artist,Björk\n");
        Assert.Contains("Björk", ListFile.Decode(cp1252));
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("List\tKind\tTitle\nA\tArtist\tBjörk\n")).ToArray();
        Assert.Equal("Björk", ListFile.Parse(ListFile.Decode(utf16)).Rows![0].Row.Title);
    }

    [Fact]
    public void FileNamesAreSafe() =>
        Assert.Equal("mediariffle-kids-80s-films.csv", ListFile.FileName("Kids' 80s / Films"));
}
