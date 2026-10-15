using System.Security.Claims;
using MediaRiffle.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

/// <summary>
/// What each user doesn't want to see: shows, movies or single episodes. They're still listed (greyed out, to unhide
/// from where they are), but left out of Play all, Shuffle, Continue watching and the next episode. Only that user's.
/// </summary>
public static class HiddenEndpoints
{
    public sealed record HiddenRequest(bool Hidden);

    /// <summary>One hidden thing: Kind is show, movie or file (an episode); Detail says which show and episode.</summary>
    public sealed record HiddenEntry(string Kind, int Id, string Title, string? Detail, DateTime CreatedUtc);

    public static void MapHiddenEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // Everything this user hid, newest first, for Profile → Hidden
        g.MapGet("/hidden", async (ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
            await ListAsync(db, users.GetUserId(me)!));

        g.MapPut("/items/{kind}/{id:int}/hidden", async (string kind, int id, HiddenRequest req, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var userId = users.GetUserId(me)!;
            var mine = db.Hidden.Where(x => x.UserId == userId);
            (IQueryable<HiddenItem> Rows, bool Exists, HiddenItem New)? item = kind.ToLowerInvariant() switch
            {
                "show" => (mine.Where(x => x.ShowId == id), await db.Shows.AnyAsync(s => s.Id == id), new HiddenItem { UserId = userId, ShowId = id }),
                "movie" => (mine.Where(x => x.MovieId == id), await db.Movies.AnyAsync(m => m.Id == id), new HiddenItem { UserId = userId, MovieId = id }),
                "file" => (mine.Where(x => x.MediaFileId == id), await db.MediaFiles.AnyAsync(f => f.Id == id), new HiddenItem { UserId = userId, MediaFileId = id }),
                _ => null,
            };
            if (item is not { Exists: true } it) return Results.NotFound();

            var current = await it.Rows.ToListAsync();
            if (!req.Hidden) db.Hidden.RemoveRange(current);
            else if (current.Count == 0) db.Hidden.Add(it.New);
            await db.SaveChangesAsync();
            return Results.Ok(new { hidden = req.Hidden });
        });
    }

    /// <summary>Everything this user hid, newest first, for Profile → Hidden.</summary>
    public static async Task<List<HiddenEntry>> ListAsync(AppDbContext db, string userId)
    {
        var rows = await db.Hidden.Where(x => x.UserId == userId).OrderByDescending(x => x.CreatedUtc)
            .Select(x => new
            {
                x.ShowId, x.MovieId, x.MediaFileId, x.CreatedUtc,
                Show = db.Shows.Where(s => s.Id == x.ShowId).Select(s => s.Title).FirstOrDefault(),
                Movie = db.Movies.Where(m => m.Id == x.MovieId).Select(m => m.Title).FirstOrDefault(),
                File = db.MediaFiles.Where(f => f.Id == x.MediaFileId).Select(f => f.Title).FirstOrDefault(),
                Episode = db.MediaFiles.Where(f => f.Id == x.MediaFileId).Select(f => f.EpisodeNumber).FirstOrDefault(),
                FileShow = db.MediaFiles.Where(f => f.Id == x.MediaFileId)
                    .Join(db.Shows, f => f.ShowId, s => s.Id, (f, s) => s.Title).FirstOrDefault(),
                Season = db.MediaFiles.Where(f => f.Id == x.MediaFileId)
                    .Join(db.Seasons, f => f.SeasonId, s => s.Id, (f, s) => (int?)s.Number).FirstOrDefault(),
            })
            .ToListAsync();
        return rows.Select(r =>
            r.ShowId is { } sid ? new HiddenEntry("show", sid, r.Show ?? "", null, r.CreatedUtc)
            : r.MovieId is { } mid ? new HiddenEntry("movie", mid, r.Movie ?? "", null, r.CreatedUtc)
            : new HiddenEntry("file", r.MediaFileId!.Value, r.File ?? "",
                // e.g. "The Show S01E02"
                $"{r.FileShow} {(r.Season is { } s ? $"S{s:00}" : "")}{(r.Episode is { } e ? $"E{e:00}" : "")}".Trim() is { Length: > 0 } d ? d : null,
                r.CreatedUtc)).ToList();
    }

    /// <summary>The files this user hasn't hidden, on their own or with their show or movie.</summary>
    public static IQueryable<MediaFile> NotHidden(AppDbContext db, string userId, IQueryable<MediaFile> files)
    {
        var mine = db.Hidden.Where(x => x.UserId == userId);
        return files.Where(f => !mine.Any(x =>
            x.MediaFileId == f.Id || (x.ShowId != null && x.ShowId == f.ShowId) || (x.MovieId != null && x.MovieId == f.MovieId)));
    }

    /// <summary>Which of these files this user hid one by one (not counting a hidden show or movie).</summary>
    internal static async Task<HashSet<int>> HiddenFiles(AppDbContext db, string userId, IEnumerable<int> fileIds)
    {
        var ids = fileIds.ToList();
        return (await db.Hidden.Where(x => x.UserId == userId && x.MediaFileId != null && ids.Contains(x.MediaFileId.Value))
            .Select(x => x.MediaFileId!.Value).ToListAsync()).ToHashSet();
    }
}
