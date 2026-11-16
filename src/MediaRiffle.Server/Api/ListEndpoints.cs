using System.Security.Claims;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using MediaRiffle.Server.Security;
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
    public sealed record RenameListRequest(string? Name);

    public static void MapListEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // The user's lists with how many things each has (in all, and of each tab's: shows and movies, music, games),
        // Favorites first, then by name
        g.MapGet("/lists", async (ClaimsPrincipal me, UserManager<AppUser> users, LibraryCatalog catalog, AppDbContext db) =>
        {
            var userId = users.GetUserId(me);
            var rows = await Visible(db, catalog, db.UserTags.Where(t => t.UserId == userId))
                .Select(t => new { t.Tag, game = t.GameId != null, music = t.ArtistId != null || t.AlbumId != null || t.MediaFileId != null, movie = t.MovieId != null })
                .ToListAsync();
            return rows.GroupBy(r => r.Tag)
                .Select(x => new
                {
                    tag = x.Key, items = x.Count(),
                    shows = x.Count(r => !r.music && !r.game && !r.movie), movies = x.Count(r => r.movie),
                    music = x.Count(r => r.music), games = x.Count(r => r.game),
                    videos = x.Count(r => !r.music && !r.game),   // shows and movies, for apps from before they were apart
                })
                .OrderBy(r => !IsFavorites(r.tag)).ThenBy(r => r.tag, StringComparer.OrdinalIgnoreCase);
        }).AllowDevices();

        // A whole list gone: everything taken off it (nothing leaves the library), including what's in a library that's
        // switched off, which its page can't show. With `tab` (shows, movies, music, games), only that tab's part of it.
        g.MapDelete("/lists/{tag}", async (string tag, string? tab, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var userId = users.GetUserId(me);
            var name = tag.Trim();
            var entries = db.UserTags.Where(t => t.UserId == userId && t.Tag == name);
            entries = tab?.Trim().ToLowerInvariant() switch
            {
                Tabs.Music => entries.Where(t => t.ArtistId != null || t.AlbumId != null || t.MediaFileId != null),
                Tabs.Shows => entries.Where(t => t.ShowId != null),
                Tabs.Movies => entries.Where(t => t.MovieId != null),
                Tabs.Videos => entries.Where(t => t.ShowId != null || t.MovieId != null),
                Tabs.Games => entries.Where(t => t.GameId != null),
                null or "" => entries,
                _ => entries.Where(_ => false),
            };
            var removed = await entries.ExecuteDeleteAsync();
            return removed == 0 ? Results.NotFound() : Results.Ok(new { removed });
        });

        // A new name; onto an existing list's name, the two become one. Favorites keeps its name.
        g.MapPut("/lists/{tag}", async (string tag, RenameListRequest req, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var userId = users.GetUserId(me)!;
            var from = tag.Trim();
            var to = Clean(req.Name);
            if (to.Length == 0) return Results.BadRequest(new { error = "Give the list a name." });
            if (to.Length > UserTag.MaxLength) return Results.BadRequest(new { error = $"A list's name can be at most {UserTag.MaxLength} characters." });
            if (IsFavorites(from) || IsFavorites(to)) return Results.BadRequest(new { error = "Favorites keeps its name." });
            var moving = await db.UserTags.Where(t => t.UserId == userId && t.Tag == from).ToListAsync();
            if (moving.Count == 0) return Results.NotFound();
            // Joining a list that's there already: its spelling stays, and what's on both is on it once
            var existing = await db.UserTags.Where(t => t.UserId == userId && t.Tag == to).ToListAsync();
            var name = existing.FirstOrDefault()?.Tag ?? to;
            foreach (var t in moving)
            {
                var already = existing.Any(e => e.Id != t.Id && e.ShowId == t.ShowId && e.MovieId == t.MovieId &&
                    e.ArtistId == t.ArtistId && e.AlbumId == t.AlbumId && e.MediaFileId == t.MediaFileId && e.GameId == t.GameId);
                if (already) db.UserTags.Remove(t);
                else t.Tag = name;
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { tag = name });
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
            var games = await GameEndpoints.Tiles(db.Games.Where(x => enabled.Contains(x.LibraryId) && mine.Any(t => t.GameId == x.Id)));
            // The name as the user wrote it (the lookup ignores case)
            var stored = await mine.Select(t => t.Tag).FirstOrDefaultAsync() ?? name;
            return Results.Ok(new { tag = stored, favorites = IsFavorites(stored), shows, movies, artists, albums, tracks, games });
        }).AllowDevices();

        // The user's lists as a CSV file (all of them, or `tag`), to keep, edit and import again. Not "/lists/…": that
        // would hide a list of the same name.
        g.MapGet("/list-file", async (string? tag, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
            await ExportAsync(db, users.GetUserId(me)!, tag, "lists"));

        // A list file into the user's lists: `mode` add (the default) puts its rows on the lists it names; replace makes
        // those lists what it says, in its order. `preview` says what it would do without doing it.
        g.MapPost("/list-file", async (HttpContext ctx, string? mode, bool? preview, ClaimsPrincipal me, UserManager<AppUser> users,
            LibraryCatalog catalog, AppDbContext db, CancellationToken ct) =>
        {
            if (mode is not (null or "add" or "replace")) return Results.BadRequest(new { error = "mode is add or replace." });
            var (text, error) = await ListFile.ReadAsync(ctx.Request, ct);
            if (text is null) return Results.BadRequest(new { error });
            var (rows, parseError) = ListFile.Parse(text);
            if (rows is null) return Results.BadRequest(new { error = parseError });

            var userId = users.GetUserId(me)!;
            var enabled = catalog.Enabled.ToList();
            var plan = await ListFile.ResolveAsync(db, rows, enabled, ListName);
            var existing = await db.UserTags.Where(t => t.UserId == userId).ToListAsync(ct);
            var result = await ListFile.ApplyAsync(db, existing, plan, mode == "replace", enabled.Select(l => l.Id).ToHashSet(),
                (tag, e, when) => new UserTag
                {
                    UserId = userId, Tag = tag, CreatedUtc = when,
                    ShowId = e.ShowId, MovieId = e.MovieId, ArtistId = e.ArtistId, AlbumId = e.AlbumId, MediaFileId = e.MediaFileId, GameId = e.GameId,
                });
            if (existing.Count + result.Added - result.Removed > MaxEntriesPerUser)
                return Results.BadRequest(new { error = $"That's more list entries than one account can have ({MaxEntriesPerUser})." });
            if (preview != true) await db.SaveChangesAsync(ct);
            return Results.Ok(result);
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
                { Game: { } x } => await db.Games.AnyAsync(f => f.Id == x),
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
                    GameId = item.Game,
                });
            }
            await db.SaveChangesAsync();
            return Results.Ok(await Of(db, userId, item).Select(t => t.Tag).OrderBy(t => t).ToListAsync());
        });
    }

    /// <summary>What's being listed: exactly one of these.</summary>
    private sealed record ItemRef(int? Show = null, int? Movie = null, int? Artist = null, int? Album = null, int? Track = null, int? Game = null);

    private static ItemRef? Item(string kind, int id) => kind.ToLowerInvariant() switch
    {
        "show" => new ItemRef(Show: id),
        "movie" => new ItemRef(Movie: id),
        "artist" => new ItemRef(Artist: id),
        "album" => new ItemRef(Album: id),
        "track" => new ItemRef(Track: id),
        "game" => new ItemRef(Game: id),
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
            { Game: { } x } => mine.Where(t => t.GameId == x),
            _ => mine.Where(t => t.MediaFileId == item.Track),
        };
    }

    /// <summary>Entries whose show, movie, artist, album, track or game is in an enabled library.</summary>
    private static IQueryable<UserTag> Visible(AppDbContext db, LibraryCatalog catalog, IQueryable<UserTag> tags)
    {
        var enabled = EnabledIds(catalog);
        return tags.Where(t =>
            (t.ShowId != null && db.Shows.Any(s => s.Id == t.ShowId && enabled.Contains(s.LibraryId))) ||
            (t.MovieId != null && db.Movies.Any(m => m.Id == t.MovieId && enabled.Contains(m.LibraryId))) ||
            (t.ArtistId != null && db.Artists.Any(a => a.Id == t.ArtistId && enabled.Contains(a.LibraryId))) ||
            (t.AlbumId != null && db.Albums.Any(a => a.Id == t.AlbumId && enabled.Contains(a.LibraryId))) ||
            (t.MediaFileId != null && db.MediaFiles.Any(f => f.Id == t.MediaFileId && enabled.Contains(f.LibraryId))) ||
            (t.GameId != null && db.Games.Any(x => x.Id == t.GameId && enabled.Contains(x.LibraryId))));
    }

    /// <summary>
    /// The files a list holds: its shows' episodes, its movies, its artists' and albums' tracks, and its single tracks.
    /// </summary>
    public static IQueryable<MediaFile> FilesIn<T>(AppDbContext db, IQueryable<T> list, IQueryable<MediaFile> files) where T : class, ITagged =>
        files.Where(f => list.Any(t =>
            (t.ShowId != null && t.ShowId == f.ShowId) || (t.MovieId != null && t.MovieId == f.MovieId) ||
            (t.AlbumId != null && t.AlbumId == f.AlbumId) || (t.MediaFileId != null && t.MediaFileId == f.Id) ||
            (t.ArtistId != null && db.Albums.Any(a => a.Id == f.AlbumId && a.ArtistId == t.ArtistId))));

    /// <summary>
    /// A user's lists (or the one named <paramref name="tag"/>) as a list file, Favorites first and then by name, each
    /// in the order things were put on it. Everything on them, including what's in a library that's switched off.
    /// </summary>
    public static async Task<IResult> ExportAsync(AppDbContext db, string userId, string? tag, string fileName)
    {
        var mine = db.UserTags.Where(t => t.UserId == userId);
        var one = tag?.Trim() is { Length: > 0 } name ? name : null;
        if (one is not null) mine = mine.Where(t => t.Tag == one);
        var entries = (await mine.ToListAsync())
            .OrderBy(t => !IsFavorites(t.Tag)).ThenBy(t => t.Tag, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.CreatedUtc).ThenBy(t => t.Id).ToList();
        if (one is not null && entries.Count == 0) return Results.NotFound();
        var rows = await ListFile.RowsAsync(db, entries.Select(e => (e.Tag, (ITagged)e)).ToList());
        return Results.File(ListFile.ToCsv("List", rows), "text/csv; charset=utf-8", ListFile.FileName(one is not null ? entries[0].Tag : fileName));
    }

    /// <summary>A list file's list name as it's kept (Favourites is Favorites), or why it can't be one.</summary>
    private static (string? Name, string? Error) ListName(string raw)
    {
        var t = Clean(raw);
        if (t.Length == 0) return (null, "No list name.");
        if (t.Length > UserTag.MaxLength) return (null, $"A list's name can be at most {UserTag.MaxLength} characters.");
        return (IsFavorites(t) ? UserTag.Favorites : t, null);
    }

    private static HashSet<int> EnabledIds(LibraryCatalog catalog) => catalog.Enabled.Select(l => l.Id).ToHashSet();

    internal static bool IsFavorites(string tag) =>
        string.Equals(tag, UserTag.Favorites, StringComparison.OrdinalIgnoreCase) || string.Equals(tag, "Favourites", StringComparison.OrdinalIgnoreCase);

    /// <summary>Trimmed, inner runs of spaces made one, commas (the separator when typing several) and control characters dropped.</summary>
    internal static string Clean(string? raw) =>
        string.Join(' ', new string((raw ?? "").Where(c => !char.IsControl(c) && c != ',').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
