using System.Security.Claims;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

/// <summary>
/// Each user's own lists: their tags on shows and movies, Favorites among them. Only the user sees them; everything
/// tagged the same way is one list, to browse, play through or shuffle (/api/queue?list=…) like a category.
/// </summary>
public static class ListEndpoints
{
    private const int MaxTagsPerItem = 50;
    private const int MaxEntriesPerUser = 10000;

    public sealed record ItemListsRequest(List<string>? Tags);

    public static void MapListEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // The user's lists with how many shows and movies each has, Favorites first, then by name
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
            var shows = await LibraryEndpoints.ShowTiles(db, db.Shows.Where(s => enabled.Contains(s.LibraryId) && mine.Any(t => t.ShowId == s.Id)));
            var movies = await LibraryEndpoints.MovieTiles(db, db.Movies.Where(m => enabled.Contains(m.LibraryId) && mine.Any(t => t.MovieId == m.Id)));
            // The name as the user wrote it (the lookup ignores case)
            var stored = await mine.Select(t => t.Tag).FirstOrDefaultAsync() ?? name;
            return Results.Ok(new { tag = stored, favorites = IsFavorites(stored), shows, movies });
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
            var exists = item.Show is { } sid ? await db.Shows.AnyAsync(s => s.Id == sid) : await db.Movies.AnyAsync(m => m.Id == item.Movie);
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
                db.UserTags.Add(new UserTag { UserId = userId, Tag = existing ?? w, ShowId = item.Show, MovieId = item.Movie });
            }
            await db.SaveChangesAsync();
            return Results.Ok(await Of(db, userId, item).Select(t => t.Tag).OrderBy(t => t).ToListAsync());
        });
    }

    private sealed record ItemRef(int? Show, int? Movie);

    private static ItemRef? Item(string kind, int id) => kind.ToLowerInvariant() switch
    {
        "show" => new ItemRef(id, null),
        "movie" => new ItemRef(null, id),
        _ => null,
    };

    private static IQueryable<UserTag> Of(AppDbContext db, string userId, ItemRef item) =>
        item.Show is { } sid ? db.UserTags.Where(t => t.UserId == userId && t.ShowId == sid)
        : db.UserTags.Where(t => t.UserId == userId && t.MovieId == item.Movie);

    /// <summary>Entries whose show or movie is in an enabled library.</summary>
    private static IQueryable<UserTag> Visible(AppDbContext db, LibraryCatalog catalog, IQueryable<UserTag> tags)
    {
        var enabled = EnabledIds(catalog);
        return tags.Where(t =>
            (t.ShowId != null && db.Shows.Any(s => s.Id == t.ShowId && enabled.Contains(s.LibraryId))) ||
            (t.MovieId != null && db.Movies.Any(m => m.Id == t.MovieId && enabled.Contains(m.LibraryId))));
    }

    private static HashSet<int> EnabledIds(LibraryCatalog catalog) => catalog.Enabled.Select(l => l.Id).ToHashSet();

    private static bool IsFavorites(string tag) =>
        string.Equals(tag, UserTag.Favorites, StringComparison.OrdinalIgnoreCase) || string.Equals(tag, "Favourites", StringComparison.OrdinalIgnoreCase);

    /// <summary>Trimmed, inner runs of spaces made one, commas (the separator when typing several) and control characters dropped.</summary>
    private static string Clean(string? raw) =>
        string.Join(' ', new string((raw ?? "").Where(c => !char.IsControl(c) && c != ',').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
