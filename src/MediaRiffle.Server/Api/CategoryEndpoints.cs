using System.Security.Claims;
using MediaRiffle.Server.Data;
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

        // The part of a category that came from lists, taken away (a library's tag is changed under Libraries)
        g.MapDelete("/categories/{tag}", async (string tag, AppDbContext db) =>
        {
            var removed = await db.CategoryItems.Where(c => c.Tag == tag.Trim()).ExecuteDeleteAsync();
            return removed == 0 ? Results.NotFound() : Results.Ok(new { removed });
        });
    }
}
