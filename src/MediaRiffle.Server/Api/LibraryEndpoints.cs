using System.Security.Claims;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using MediaRiffle.Server.Media;
using MediaRiffle.Server.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Api;

/// <summary>Browsing: libraries, shows and their seasons, movies, one file's details, continue watching, pictures.</summary>
public static class LibraryEndpoints
{
    public static void MapLibraryEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/libraries", async (IOptions<MediaRiffleOptions> o, AppDbContext db) =>
        {
            var counts = await db.MediaFiles.GroupBy(f => f.Library).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
            return o.Value.Libraries.Select(l => new { l.Name, kind = l.Kind.ToString(), files = counts.GetValueOrDefault(l.Name) });
        });

        g.MapGet("/libraries/{name}/shows", async (string name, AppDbContext db) =>
            await db.Shows.Where(s => s.Library == name).OrderBy(s => s.Title)
                .Select(s => new { s.Id, s.Title, episodes = db.MediaFiles.Count(f => f.ShowId == s.Id), image = $"/api/images/show/{s.Id}" })
                .ToListAsync());

        g.MapGet("/libraries/{name}/movies", async (string name, AppDbContext db) =>
            await db.Movies.Where(m => m.Library == name).OrderBy(m => m.Title)
                .Select(m => new { m.Id, m.Title, m.Year, parts = m.Parts.Count, image = $"/api/images/movie/{m.Id}" })
                .ToListAsync());

        g.MapGet("/shows/{id:int}", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var show = await db.Shows.FirstOrDefaultAsync(s => s.Id == id);
            if (show is null) return Results.NotFound();
            var userId = users.GetUserId(me)!;
            var seasons = await db.Seasons.Where(s => s.ShowId == id).OrderBy(s => s.Number).ToListAsync();
            var files = await db.MediaFiles.Where(f => f.ShowId == id)
                .OrderBy(f => f.EpisodeNumber ?? int.MaxValue).ThenBy(f => f.RelativePath).ToListAsync();
            var progress = await ProgressFor(db, userId, files.Select(f => f.Id));
            return Results.Ok(new
            {
                show.Id, show.Title, image = $"/api/images/show/{id}",
                seasons = seasons.Select(s => new
                {
                    s.Id, s.Number, s.Title,
                    episodes = files.Where(f => f.SeasonId == s.Id).Select(f => FileSummary(f, progress)),
                }),
            });
        });

        g.MapGet("/movies/{id:int}", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var movie = await db.Movies.Include(m => m.Parts).FirstOrDefaultAsync(m => m.Id == id);
            if (movie is null) return Results.NotFound();
            var parts = movie.Parts.OrderBy(p => p.PartNumber).ToList();
            var progress = await ProgressFor(db, users.GetUserId(me)!, parts.Select(p => p.Id));
            return Results.Ok(new
            {
                movie.Id, movie.Title, movie.Year, image = $"/api/images/movie/{id}",
                parts = parts.Select(p => FileSummary(p, progress)),
            });
        });

        g.MapGet("/files/{id:int}", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db, LibraryFiles files) =>
        {
            var f = await db.MediaFiles.FirstOrDefaultAsync(x => x.Id == id);
            if (f is null) return Results.NotFound();
            var progress = await ProgressFor(db, users.GetUserId(me)!, [id]);
            var probe = string.IsNullOrEmpty(f.ProbeJson) ? new ProbeResult() : ProbeResult.Parse(f.ProbeJson);
            var path = files.FullPath(f);
            var cap = Quality.Level((await users.GetUserAsync(me))?.MaxQuality);
            return Results.Ok(new
            {
                summary = FileSummary(f, progress),
                f.Container, f.VideoCodec, f.Width, f.Height, f.ProbeError,
                bitrateKbps = probe.BitRateKbps,
                // The qualities this user may pick, best first; Original only without a cap
                qualities = (cap is null ? new[] { new { id = Quality.Original, label = "Original", kbps = probe.BitRateKbps } } : [])
                    .Concat(Quality.Ladder.Where(l => cap is null || l.Height <= cap.Height)
                        .Select(l => new { id = l.Id, label = $"{l.Label} ({l.TotalKbps / 1000.0:0.#} Mbit/s)", kbps = l.TotalKbps })),
                audio = probe.Audio.Select(a => new { a.Index, a.Label, a.IsDefault }),
                subtitles = probe.Subtitles.Select(s => new { id = $"s{s.Index}", s.Label, text = s.IsTextSubtitle })
                    .Concat((path is null ? new() : LibraryFiles.Sidecars(path)).Select(x => new { id = $"x{x.Number}", x.Label, text = true })),
                next = await NextEpisode(db, f),
            });
        });

        // Started but not finished, newest first: the home page's "Continue watching"
        g.MapGet("/continue", async (ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var userId = users.GetUserId(me)!;
            var recent = await db.Progress.Where(p => p.UserId == userId && !p.Finished && p.PositionSeconds > 30)
                .OrderByDescending(p => p.UpdatedUtc).Take(20).ToListAsync();
            var ids = recent.Select(p => p.MediaFileId).ToList();
            var fileRows = await db.MediaFiles.Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id);
            var showNames = await db.Shows.Where(s => fileRows.Values.Select(f => f.ShowId).Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Title);
            var progress = recent.ToDictionary(p => p.MediaFileId);
            return recent.Where(p => fileRows.ContainsKey(p.MediaFileId)).Select(p =>
            {
                var f = fileRows[p.MediaFileId];
                return new
                {
                    file = FileSummary(f, progress),
                    show = f.ShowId is { } sid ? showNames.GetValueOrDefault(sid) : null,
                };
            });
        });

        g.MapGet("/images/file/{id:int}", async (int id, AppDbContext db, LibraryFiles files, ImageService images, HttpContext ctx) =>
        {
            var f = await db.MediaFiles.FirstOrDefaultAsync(x => x.Id == id);
            var path = f is null ? null : files.FullPath(f);
            if (path is null) return Results.NotFound();
            return Image(ctx, await images.FrameAsync(f!.Id, path, f.DurationSeconds));
        });

        g.MapGet("/images/show/{id:int}", async (int id, AppDbContext db, LibraryFiles files, ImageService images, HttpContext ctx) =>
        {
            var show = await db.Shows.FirstOrDefaultAsync(s => s.Id == id);
            if (show is null) return Results.NotFound();
            var art = ImageService.FolderArt(files.Resolve(show.Library, show.Folder));
            if (art is not null) return Image(ctx, art);
            var first = await db.MediaFiles.Where(f => f.ShowId == id).OrderBy(f => f.SeasonId).ThenBy(f => f.EpisodeNumber).FirstOrDefaultAsync();
            var path = first is null ? null : files.FullPath(first);
            return path is null ? Results.NotFound() : Image(ctx, await images.FrameAsync(first!.Id, path, first.DurationSeconds));
        });

        g.MapGet("/images/movie/{id:int}", async (int id, AppDbContext db, LibraryFiles files, ImageService images, HttpContext ctx) =>
        {
            var first = await db.MediaFiles.Where(f => f.MovieId == id).OrderBy(f => f.PartNumber).FirstOrDefaultAsync();
            var path = first is null ? null : files.FullPath(first);
            if (path is null) return Results.NotFound();
            // A movie in its own folder can have folder art; one beside others only art named after it
            var inOwnFolder = first!.RelativePath.Contains('/') && !await db.MediaFiles.AnyAsync(f =>
                f.Library == first.Library && f.MovieId != id && f.RelativePath.StartsWith(first.RelativePath.Substring(0, first.RelativePath.LastIndexOf('/') + 1)));
            var art = ImageService.FileArt(path) ?? (inOwnFolder ? ImageService.FolderArt(Path.GetDirectoryName(path)) : null);
            return Image(ctx, art ?? await images.FrameAsync(first.Id, path, first.DurationSeconds));
        });
    }

    private static IResult Image(HttpContext ctx, string? path)
    {
        if (path is null) return Results.NotFound();
        ctx.Response.Headers.CacheControl = "private, max-age=86400";
        var type = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
        return Results.File(path, type, enableRangeProcessing: false);
    }

    private static async Task<Dictionary<int, PlaybackProgress>> ProgressFor(AppDbContext db, string userId, IEnumerable<int> fileIds)
    {
        var ids = fileIds.ToList();
        return await db.Progress.Where(p => p.UserId == userId && ids.Contains(p.MediaFileId)).ToDictionaryAsync(p => p.MediaFileId);
    }

    private static object FileSummary(MediaFile f, Dictionary<int, PlaybackProgress> progress)
    {
        progress.TryGetValue(f.Id, out var p);
        return new
        {
            f.Id, f.Title, f.EpisodeNumber, f.PartNumber, duration = f.DurationSeconds,
            playable = f.ProbeError is null && !string.IsNullOrEmpty(f.ProbeJson),
            position = p?.PositionSeconds ?? 0, finished = p?.Finished ?? false,
            image = $"/api/images/file/{f.Id}",
        };
    }

    /// <summary>The episode after this one in its show, or null.</summary>
    private static async Task<int?> NextEpisode(AppDbContext db, MediaFile f)
    {
        if (f.ShowId is null) return null;
        var all = await db.MediaFiles.Where(x => x.ShowId == f.ShowId)
            .Join(db.Seasons, x => x.SeasonId, s => s.Id, (x, s) => new { x.Id, s.Number, Ep = x.EpisodeNumber ?? int.MaxValue, x.RelativePath })
            .ToListAsync();
        var ordered = all.OrderBy(x => x.Number).ThenBy(x => x.Ep).ThenBy(x => x.RelativePath).Select(x => x.Id).ToList();
        var i = ordered.IndexOf(f.Id);
        return i >= 0 && i + 1 < ordered.Count ? ordered[i + 1] : null;
    }
}
