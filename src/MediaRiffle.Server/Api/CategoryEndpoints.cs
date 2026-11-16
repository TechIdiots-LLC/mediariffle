using System.Security.Claims;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using MediaRiffle.Server.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

/// <summary>
/// Categories made from an admin's list: the libraries' tags are everyone's categories already, and this adds the ones
/// an admin picked by hand, show by show. Making one copies the list, so the admin's own list stays theirs to change.
/// </summary>
public static class CategoryEndpoints
{
    public static void MapCategoryEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization("Admin").AddEndpointFilter(Mfa.RequireForAdmin);

        // The admin's list becomes a category of the same name, for everyone; onto a category that exists, the two are
        // one. Making it again adds what was put on the list since.
        g.MapPost("/lists/{tag}/category", async (string tag, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var userId = users.GetUserId(me)!;
            var name = tag.Trim();
            if (string.Equals(name, UserTag.Favorites, StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Favourites", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Favorites is personal; make a list of its own to share." });
            var list = await db.UserTags.Where(t => t.UserId == userId && t.Tag == name).ToListAsync();
            if (list.Count == 0) return Results.NotFound();
            var existing = await db.CategoryItems.Where(c => c.Tag == name).ToListAsync();
            var added = 0;
            foreach (var t in list)
            {
                if (existing.Any(e => e.ShowId == t.ShowId && e.MovieId == t.MovieId && e.ArtistId == t.ArtistId &&
                                      e.AlbumId == t.AlbumId && e.MediaFileId == t.MediaFileId && e.GameId == t.GameId)) continue;
                db.CategoryItems.Add(new CategoryItem
                {
                    Tag = existing.FirstOrDefault()?.Tag ?? list[0].Tag,
                    ShowId = t.ShowId, MovieId = t.MovieId, ArtistId = t.ArtistId, AlbumId = t.AlbumId, MediaFileId = t.MediaFileId, GameId = t.GameId,
                });
                added++;
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { tag = existing.FirstOrDefault()?.Tag ?? list[0].Tag, added });
        });

        // The categories made from lists (all, or `tag`) as a list file. The libraries' own tags aren't in it: they're
        // set on the library.
        g.MapGet("/categories/file", async (string? tag, AppDbContext db) =>
        {
            var one = tag?.Trim() is { Length: > 0 } name ? name : null;
            var items = db.CategoryItems.AsQueryable();
            if (one is not null) items = items.Where(c => c.Tag == one);
            var entries = (await items.ToListAsync())
                .OrderBy(c => c.Tag, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.CreatedUtc).ThenBy(c => c.Id).ToList();
            if (one is not null && entries.Count == 0) return Results.NotFound();
            var rows = await ListFile.RowsAsync(db, entries.Select(e => (e.Tag, (ITagged)e)).ToList());
            return Results.File(ListFile.ToCsv("Category", rows), "text/csv; charset=utf-8",
                ListFile.FileName(one is not null ? $"category-{entries[0].Tag}" : "categories"));
        });

        // A list file into the categories, as /api/list-file does into a user's lists (a file with a List column works too)
        g.MapPost("/categories/file", async (HttpContext ctx, string? mode, bool? preview, LibraryCatalog catalog, AppDbContext db, CancellationToken ct) =>
        {
            if (mode is not (null or "add" or "replace")) return Results.BadRequest(new { error = "mode is add or replace." });
            var (text, error) = await ListFile.ReadAsync(ctx.Request, ct);
            if (text is null) return Results.BadRequest(new { error });
            var (rows, parseError) = ListFile.Parse(text);
            if (rows is null) return Results.BadRequest(new { error = parseError });

            var enabled = catalog.Enabled.ToList();
            var plan = await ListFile.ResolveAsync(db, rows, enabled, CategoryName);
            var existing = await db.CategoryItems.ToListAsync(ct);
            var result = await ListFile.ApplyAsync(db, existing, plan, mode == "replace", enabled.Select(l => l.Id).ToHashSet(),
                (tag, e, when) => new CategoryItem
                {
                    Tag = tag, CreatedUtc = when,
                    ShowId = e.ShowId, MovieId = e.MovieId, ArtistId = e.ArtistId, AlbumId = e.AlbumId, MediaFileId = e.MediaFileId, GameId = e.GameId,
                });
            if (preview != true) await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        });

        // A user's lists as a list file, for an admin (to keep, or to make categories from)
        g.MapGet("/users/{id}/list-file", async (string id, UserManager<AppUser> users, AppDbContext db) =>
            await users.FindByIdAsync(id) is { } user
                ? await ListEndpoints.ExportAsync(db, user.Id, null, $"lists-{user.UserName}")
                : Results.NotFound());

        // The part of a category that came from lists, taken away (a library's tag is changed under Libraries)
        g.MapDelete("/categories/{tag}", async (string tag, AppDbContext db) =>
        {
            var removed = await db.CategoryItems.Where(c => c.Tag == tag.Trim()).ExecuteDeleteAsync();
            return removed == 0 ? Results.NotFound() : Results.Ok(new { removed });
        });
    }

    private static (string? Name, string? Error) CategoryName(string raw)
    {
        var t = ListEndpoints.Clean(raw);
        if (t.Length == 0) return (null, "No category name.");
        if (t.Length > UserTag.MaxLength) return (null, $"A category's name can be at most {UserTag.MaxLength} characters.");
        if (ListEndpoints.IsFavorites(t)) return (null, "Favorites is personal; it can't be a category.");
        return (t, null);
    }
}
