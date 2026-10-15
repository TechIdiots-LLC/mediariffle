using System.Security.Claims;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

/// <summary>
/// Each user's own lists: their tags on shows, movies, artists, albums and single tracks, Favorites among them. Only the
/// user sees them; everything tagged the same way is one list, to browse, play through or shuffle (/api/queue?list=…)
/// like a category. A list of tracks is a playlist.
/// </summary>
public static class ListEndpoints
{
    private const int MaxTagsPerItem = 50;
    private const int MaxEntriesPerUser = 10000;

    public sealed record ItemListsRequest(List<string>? Tags);

    public static void MapListEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // The user's lists with how many things each has, Favorites first, then by name
        g.MapGet("/lists", async (ClaimsPrincipal me, UserManager<AppUser> users, LibraryCatalog catalog, AppDbContext db) =>
        {
            var userId = users.GetUserId(me);
            var rows = await Visible(db, catalog, db.UserTags.Where(t => t.UserId == userId))
                .GroupBy(t => t.Tag).Select(x => new { tag = x.Key, items = x.Count() }).ToListAsync();
            return rows.OrderBy(r => !IsFavorites(r.tag)).ThenBy(r => r.tag, StringComparer.OrdinalIgnoreCase);
        });

        g.MapGet("/lists/{tag}", async (string tag, ClaimsPrincipal me, UserManager<AppUser> users, LibraryCatalog catalog, AppDbContext db) =>
        {
            var userId = users.GetUserId(me);
            var name = tag.Trim();
            var mine = db.UserTags.Where(t => t.UserId == userId && t.Tag == name);
            var enabled = EnabledIds(catalog);
            var shows = await LibraryEndpoints.ShowTiles(db, userId!, db.Shows.Where(s => enabled.Contains(s.LibraryId) && mine.Any(t => t.ShowId == s.Id)));
            var movies = await LibraryEndpoints.MovieTiles(db, userId!, db.Movies.Where(m => enabled.Contains(m.LibraryId) && mine.Any(t => t.MovieId == m.Id)));
            var artists = await MusicEndpoints.ArtistTiles(db, userId!, db.Artists.Where(a => enabled.Contains(a.LibraryId) && mine.Any(t => t.ArtistId == a.Id)));
            var albums = await MusicEndpoints.AlbumTiles(db, userId!, db.Albums.Where(a => enabled.Contains(a.LibraryId) && mine.Any(t => t.AlbumId == a.Id)));
            // Tracks in the order they were added, as a playlist
            var tracks = await MusicEndpoints.TrackRows(db, userId!, mine.Where(t => t.MediaFileId != null)
                .Join(db.MediaFiles.Where(f => enabled.Contains(f.LibraryId)), t => t.MediaFileId, f => f.Id, (t, f) => new { t.CreatedUtc, f })
                .OrderBy(x => x.CreatedUtc).Select(x => x.f));
            // The name as the user wrote it (the lookup ignores case)
            var stored = await mine.Select(t => t.Tag).FirstOrDefaultAsync() ?? name;
            return Results.Ok(new { tag = stored, favorites = IsFavorites(stored), shows, movies, artists, albums, tracks });
        });

        // One show's or movie's place in the user's lists, and setting it (the whole set at once)
        g.MapGet("/items/{kind}/{id:int}/lists", async (string kind, int id, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            if (Item(kind, id) is not { } item) return Results.NotFound();
            var userId = users.GetUserId(me);
            return Results.Ok(await Of(db, userId!, item).Select(t => t.Tag).OrderBy(t => t).ToListAsync());
        });

        g.MapPut("/items/{kind}/{id:int}/lists", async (string kind, int id, ItemListsRequest req, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            if (Item(kind, id) is not { } item) return Results.NotFound();
            var exists = item switch
            {
                { Show: { } x } => await db.Shows.AnyAsync(s => s.Id == x),
                { Movie: { } x } => await db.Movies.AnyAsync(m => m.Id == x),
                { Artist: { } x } => await db.Artists.AnyAsync(a => a.Id == x),
                { Album: { } x } => await db.Albums.AnyAsync(a => a.Id == x),
                { Track: { } x } => await db.MediaFiles.AnyAsync(f => f.Id == x && f.Kind == MediaKind.Track),
                _ => false,
            };
            if (!exists) return Results.NotFound();

            var wanted = new List<string>();
            foreach (var raw in req.Tags ?? [])
            {
                var t = Clean(raw);
                if (t.Length == 0) continue;
                if (t.Length > UserTag.MaxLength) return Results.BadRequest(new { error = $"A list's name can be at most {UserTag.MaxLength} characters." });
                if (IsFavorites(t)) t = UserTag.Favorites;
                if (!wanted.Contains(t, StringComparer.OrdinalIgnoreCase)) wanted.Add(t);
            }
            if (wanted.Count > MaxTagsPerItem) return Results.BadRequest(new { error = $"At most {MaxTagsPerItem} lists per show or movie." });

            var userId = users.GetUserId(me)!;
            var current = await Of(db, userId, item).ToListAsync();
            db.UserTags.RemoveRange(current.Where(c => !wanted.Contains(c.Tag, StringComparer.OrdinalIgnoreCase)));
            var adding = wanted.Where(w => !current.Any(c => string.Equals(c.Tag, w, StringComparison.OrdinalIgnoreCase))).ToList();
            if (adding.Count > 0 && await db.UserTags.CountAsync(t => t.UserId == userId) + adding.Count > MaxEntriesPerUser)
                return Results.BadRequest(new { error = "That's more list entries than one account can have." });
            foreach (var w in adding)
            {
                // A list keeps the spelling it was started with
                var existing = await db.UserTags.Where(t => t.UserId == userId && t.Tag == w).Select(t => t.Tag).FirstOrDefaultAsync();
                db.UserTags.Add(new UserTag
                {
                    UserId = userId, Tag = existing ?? w,
                    ShowId = item.Show, MovieId = item.Movie, ArtistId = item.Artist, AlbumId = item.Album, MediaFileId = item.Track,
                });
            }
            await db.SaveChangesAsync();
            return Results.Ok(await Of(db, userId, item).Select(t => t.Tag).OrderBy(t => t).ToListAsync());
        });
    }

    /// <summary>What's being listed: exactly one of these.</summary>
    private sealed record ItemRef(int? Show = null, int? Movie = null, int? Artist = null, int? Album = null, int? Track = null);

    private static ItemRef? Item(string kind, int id) => kind.ToLowerInvariant() switch
    {
        "show" => new ItemRef(Show: id),
        "movie" => new ItemRef(Movie: id),
        "artist" => new ItemRef(Artist: id),
        "album" => new ItemRef(Album: id),
        "track" => new ItemRef(Track: id),
        _ => null,
    };

    private static IQueryable<UserTag> Of(AppDbContext db, string userId, ItemRef item)
    {
        var mine = db.UserTags.Where(t => t.UserId == userId);
        return item switch
        {
            { Show: { } x } => mine.Where(t => t.ShowId == x),
            { Movie: { } x } => mine.Where(t => t.MovieId == x),
            { Artist: { } x } => mine.Where(t => t.ArtistId == x),
            { Album: { } x } => mine.Where(t => t.AlbumId == x),
            _ => mine.Where(t => t.MediaFileId == item.Track),
        };
    }

    /// <summary>Entries whose show, movie, artist, album or track is in an enabled library.</summary>
    private static IQueryable<UserTag> Visible(AppDbContext db, LibraryCatalog catalog, IQueryable<UserTag> tags)
    {
        var enabled = EnabledIds(catalog);
        return tags.Where(t =>
            (t.ShowId != null && db.Shows.Any(s => s.Id == t.ShowId && enabled.Contains(s.LibraryId))) ||
            (t.MovieId != null && db.Movies.Any(m => m.Id == t.MovieId && enabled.Contains(m.LibraryId))) ||
            (t.ArtistId != null && db.Artists.Any(a => a.Id == t.ArtistId && enabled.Contains(a.LibraryId))) ||
            (t.AlbumId != null && db.Albums.Any(a => a.Id == t.AlbumId && enabled.Contains(a.LibraryId))) ||
            (t.MediaFileId != null && db.MediaFiles.Any(f => f.Id == t.MediaFileId && enabled.Contains(f.LibraryId))));
    }

    /// <summary>
    /// The files a list holds: its shows' episodes, its movies, its artists' and albums' tracks, and its single tracks.
    /// </summary>
    public static IQueryable<MediaFile> FilesIn(AppDbContext db, IQueryable<UserTag> list, IQueryable<MediaFile> files) =>
        files.Where(f => list.Any(t =>
            (t.ShowId != null && t.ShowId == f.ShowId) || (t.MovieId != null && t.MovieId == f.MovieId) ||
            (t.AlbumId != null && t.AlbumId == f.AlbumId) || (t.MediaFileId != null && t.MediaFileId == f.Id) ||
            (t.ArtistId != null && db.Albums.Any(a => a.Id == f.AlbumId && a.ArtistId == t.ArtistId))));

    private static HashSet<int> EnabledIds(LibraryCatalog catalog) => catalog.Enabled.Select(l => l.Id).ToHashSet();

    private static bool IsFavorites(string tag) =>
        string.Equals(tag, UserTag.Favorites, StringComparison.OrdinalIgnoreCase) || string.Equals(tag, "Favourites", StringComparison.OrdinalIgnoreCase);

    /// <summary>Trimmed, inner runs of spaces made one, commas (the separator when typing several) and control characters dropped.</summary>
    private static string Clean(string? raw) =>
        string.Join(' ', new string((raw ?? "").Where(c => !char.IsControl(c) && c != ',').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
