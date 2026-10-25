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

        g.MapGet("/libraries", async (LibraryCatalog catalog, AppDbContext db) =>
        {
            var counts = await db.MediaFiles.GroupBy(f => f.LibraryId).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
            return catalog.Enabled.Select(l => new { l.Id, l.Name, kind = l.Kind.ToString(), l.Tags, files = counts.GetValueOrDefault(l.Id) });
        });

        g.MapGet("/libraries/{id:int}", (int id, LibraryCatalog catalog) =>
            catalog.Get(id) is { Enabled: true } l ? Results.Ok(new { l.Id, l.Name, kind = l.Kind.ToString(), l.Tags }) : Results.NotFound());

        g.MapGet("/libraries/{id:int}/shows", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, LibraryCatalog catalog, AppDbContext db) =>
            catalog.Get(id) is not { Enabled: true } ? Results.NotFound() : Results.Ok(await ShowsIn(db, users.GetUserId(me)!, [id])));

        g.MapGet("/libraries/{id:int}/movies", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, LibraryCatalog catalog, AppDbContext db) =>
            catalog.Get(id) is not { Enabled: true } ? Results.NotFound() : Results.Ok(await MoviesIn(db, users.GetUserId(me)!, [id])));

        // Categories: the tags on the libraries (as in MediaLibraryManager), each with what's in it
        g.MapGet("/tags", async (LibraryCatalog catalog, AppDbContext db) =>
        {
            var counts = await db.MediaFiles.GroupBy(f => f.LibraryId).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
            return catalog.Tags.Select(t => new { tag = t, files = catalog.WithTag(t).Sum(id => counts.GetValueOrDefault(id)) });
        });

        g.MapGet("/tags/{tag}", async (string tag, ClaimsPrincipal me, UserManager<AppUser> users, LibraryCatalog catalog, AppDbContext db) =>
        {
            var ids = catalog.WithTag(tag);
            if (ids.Count == 0) return Results.NotFound();
            var userId = users.GetUserId(me)!;
            return Results.Ok(new
            {
                tag, shows = await ShowsIn(db, userId, ids), movies = await MoviesIn(db, userId, ids),
                artists = await MusicEndpoints.ArtistTiles(db, userId, db.Artists.Where(a => ids.Contains(a.LibraryId))),
            });
        });

        // What to play in a row: a category, one of the user's lists, a library, a show, an album or an artist, in order or shuffled.
        // Episodes in season order, movies part by part; shuffled, every episode and movie part is mixed together.
        // What the user hid (a show, a movie or an episode) is left out.
        g.MapGet("/queue", async (string? tag, string? list, int? library, int? show, int? album, int? artist, bool? shuffle,
            ClaimsPrincipal me, UserManager<AppUser> users, LibraryCatalog catalog, AppDbContext db) =>
        {
            var enabled = catalog.Enabled.Select(l => l.Id).ToHashSet();
            var userId = users.GetUserId(me)!;
            var q = HiddenEndpoints.NotHidden(db, userId, db.MediaFiles.Where(f => f.ProbeError == null && f.ProbeJson != ""));
            Dictionary<int, DateTime>? playlist = null;
            if (show is { } sid) q = q.Where(f => f.ShowId == sid);
            else if (album is { } alid) q = q.Where(f => f.AlbumId == alid);
            else if (artist is { } arid) q = q.Where(f => db.Albums.Any(a => a.Id == f.AlbumId && a.ArtistId == arid));
            else if (library is { } lid) q = q.Where(f => f.LibraryId == lid);
            else if (!string.IsNullOrWhiteSpace(tag)) { var ids = catalog.WithTag(tag); q = q.Where(f => ids.Contains(f.LibraryId)); }
            else if (!string.IsNullOrWhiteSpace(list))
            {
                var name = list.Trim();
                var mine = db.UserTags.Where(t => t.UserId == userId && t.Tag == name);
                q = ListEndpoints.FilesIn(db, mine, q);
                // A playlist (single tracks only) plays in the order its tracks were added
                playlist = await mine.Where(t => t.MediaFileId != null).ToDictionaryAsync(t => t.MediaFileId!.Value, t => t.CreatedUtc);
            }
            else return Results.BadRequest(new { error = "Say what to play: a tag, a list, a library, a show, an album or an artist." });

            var rows = await q.Where(f => enabled.Contains(f.LibraryId))
                .Select(f => new
                {
                    f.Id, f.Title, f.EpisodeNumber, f.PartNumber, f.RelativePath, f.Kind, f.TrackArtist, f.DiscNumber, f.TrackNumber,
                    Show = db.Shows.Where(s => s.Id == f.ShowId).Select(s => s.Title).FirstOrDefault(),
                    Season = db.Seasons.Where(s => s.Id == f.SeasonId).Select(s => (int?)s.Number).FirstOrDefault(),
                    Series = db.Seasons.Where(s => s.Id == f.SeasonId).Select(s => s.Series).FirstOrDefault(),
                    Movie = db.Movies.Where(m => m.Id == f.MovieId).Select(m => m.Title).FirstOrDefault(),
                    Album = db.Albums.Where(a => a.Id == f.AlbumId).Select(a => a.Title).FirstOrDefault(),
                    AlbumYear = db.Albums.Where(a => a.Id == f.AlbumId).Select(a => a.Year).FirstOrDefault(),
                    AlbumArtist = db.Albums.Where(a => a.Id == f.AlbumId)
                        .Join(db.Artists, a => a.ArtistId, x => x.Id, (a, x) => x.Name).FirstOrDefault(),
                })
                .ToListAsync();
            // In order: shows and movies by title, episodes in season order; music by artist, their albums oldest
            // first, then disc and track
            var ordered = shuffle == true
                ? rows.OrderBy(_ => Random.Shared.Next()).ToList()
                : playlist is not null && rows.Count > 0 && rows.All(r => playlist.ContainsKey(r.Id))
                ? rows.OrderBy(r => playlist[r.Id]).ToList()
                : rows.OrderBy(r => r.Show ?? r.Movie ?? r.AlbumArtist ?? r.Title, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.AlbumYear ?? 0).ThenBy(r => r.Album ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Series ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Season ?? 0).ThenBy(r => r.DiscNumber ?? 1).ThenBy(r => r.EpisodeNumber ?? r.TrackNumber ?? int.MaxValue).ThenBy(r => r.PartNumber)
                    .ThenBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            return Results.Ok(ordered.Take(2000).Select(r => new
            {
                id = r.Id,
                title = r.Show is not null
                    ? $"{r.Show} · {(string.IsNullOrEmpty(r.Series) ? "" : r.Series + " ")}{(r.Season is { } s ? $"S{s:00}" : "")}{(r.EpisodeNumber is { } e ? $"E{e:00}" : "")} {r.Title}".Replace("·  ", "· ")
                    : r.Kind == MediaKind.Track ? $"{r.TrackArtist ?? r.AlbumArtist} · {r.Title}"
                    : r.Movie is not null && r.PartNumber > 1 ? $"{r.Movie} (part {r.PartNumber})" : r.Movie ?? r.Title,
                // Music plays on the listening page, the rest in the video player
                audio = r.Kind == MediaKind.Track,
            }));
        });

        g.MapGet("/shows/{id:int}", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var show = await db.Shows.FirstOrDefaultAsync(s => s.Id == id);
            if (show is null) return Results.NotFound();
            var userId = users.GetUserId(me)!;
            // The show's own seasons first, then each of its other series' (UK, All Stars, …) by name
            var seasons = (await db.Seasons.Where(s => s.ShowId == id).ToListAsync())
                .OrderBy(s => s.Series.Length > 0).ThenBy(s => s.Series, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Number).ToList();
            var files = await db.MediaFiles.Where(f => f.ShowId == id)
                .OrderBy(f => f.EpisodeNumber ?? int.MaxValue).ThenBy(f => f.RelativePath).ToListAsync();
            var progress = await ProgressFor(db, userId, files.Select(f => f.Id));
            var hiddenFiles = await HiddenEndpoints.HiddenFiles(db, userId, files.Select(f => f.Id));
            return Results.Ok(new
            {
                show.Id, show.Title, image = $"/api/images/show/{id}?v={ImageService.Version}",
                hidden = await db.Hidden.AnyAsync(x => x.UserId == userId && x.ShowId == id),
                seasons = seasons.Select(s => new
                {
                    s.Id, s.Number, s.Title, s.Series,
                    episodes = files.Where(f => f.SeasonId == s.Id).Select(f => FileSummary(f, progress, hiddenFiles)),
                }),
            });
        });

        g.MapGet("/movies/{id:int}", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var movie = await db.Movies.Include(m => m.Parts).FirstOrDefaultAsync(m => m.Id == id);
            if (movie is null) return Results.NotFound();
            var parts = movie.Parts.OrderBy(p => p.PartNumber).ToList();
            var userId = users.GetUserId(me)!;
            var progress = await ProgressFor(db, userId, parts.Select(p => p.Id));
            return Results.Ok(new
            {
                movie.Id, movie.Title, movie.Year, image = $"/api/images/movie/{id}?v={ImageService.Version}",
                hidden = await db.Hidden.AnyAsync(x => x.UserId == userId && x.MovieId == id),
                parts = parts.Select(p => FileSummary(p, progress)),
            });
        });

        g.MapGet("/files/{id:int}", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db, LibraryFiles files, IOptions<MediaRiffleOptions> options) =>
        {
            var f = await db.MediaFiles.FirstOrDefaultAsync(x => x.Id == id);
            if (f is null) return Results.NotFound();
            var userId = users.GetUserId(me)!;
            var progress = await ProgressFor(db, userId, [id]);
            var probe = string.IsNullOrEmpty(f.ProbeJson) ? new ProbeResult() : ProbeResult.Parse(f.ProbeJson);
            var path = files.FullPath(f);
            var neighbours = await Neighbours(db, userId, f);
            var mine = db.Hidden.Where(x => x.UserId == userId);
            var account = await users.GetUserAsync(me);
            var cap = Quality.Level(account?.MaxQuality);
            return Results.Ok(new
            {
                summary = FileSummary(f, progress),
                kind = f.Kind.ToString(),
                // How the user likes Auto to pick (Profile → Video quality), and the ceiling that puts on it
                preferredQuality = account?.PreferredQuality ?? "auto",
                autoCap = Quality.Lower(account?.MaxQuality, Quality.PreferenceCap(account?.PreferredQuality)),
                f.ShowId, f.MovieId,
                f.Container, f.VideoCodec, f.Width, f.Height, f.ProbeError,
                bitrateKbps = probe.BitRateKbps,
                // The qualities this user may pick, best first; Original only without a cap
                qualities = (cap is null ? new[] { new { id = Quality.Original, label = "Original", kbps = probe.BitRateKbps } } : [])
                    // The levels this file can have here: within the server's maximum and the user's cap, none (much) bigger than the picture
                    .Concat(Quality.AdaptiveLevels(f.Height, options.Value.Transcoding.MaxHeight, cap?.Id)
                        .Select(l => new { id = l.Id, label = $"{l.Label} ({l.TotalKbps / 1000.0:0.#} Mbit/s)", kbps = l.TotalKbps })),
                audio = probe.Audio.Select(a => new { a.Index, a.Label, a.IsDefault }),
                // PGS: drawn by the browser (libpgs) rather than burned in
                subtitles = probe.Subtitles.Select(s => new { id = $"s{s.Index}", s.Label, text = s.IsTextSubtitle, pgs = s.IsPgs })
                    .Concat((path is null ? new() : LibraryFiles.Sidecars(path)).Select(x => new { id = $"x{x.Number}", x.Label, text = true, pgs = false })),
                next = neighbours.Next,
                previous = neighbours.Previous,
                // Whether the user hid this episode, its show or this movie
                hidden = new
                {
                    file = await mine.AnyAsync(x => x.MediaFileId == id),
                    show = f.ShowId is { } sid && await mine.AnyAsync(x => x.ShowId == sid),
                    movie = f.MovieId is { } mid && await mine.AnyAsync(x => x.MovieId == mid),
                },
            });
        });

        // Started but not finished, newest first: the home page's "Continue watching" (leaving out what the user hid)
        // Off Continue watching (its ×), without hiding it: the place is kept, and playing it again puts it back
        g.MapDelete("/continue/{fileId:int}", async (int fileId, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var userId = users.GetUserId(me)!;
            await db.Progress.Where(p => p.UserId == userId && p.MediaFileId == fileId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.LeftContinue, true));
            return Results.Ok();
        });

        g.MapGet("/continue", async (ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var userId = users.GetUserId(me)!;
            // Songs aren't resumed: only episodes and movies
            var visible = HiddenEndpoints.NotHidden(db, userId, db.MediaFiles.Where(f => f.Kind != MediaKind.Track));
            var recent = await db.Progress.Where(p => p.UserId == userId && !p.Finished && !p.LeftContinue && p.PositionSeconds > 30 && visible.Any(f => f.Id == p.MediaFileId))
                .OrderByDescending(p => p.UpdatedUtc).Take(60).ToListAsync();
            var ids = recent.Select(p => p.MediaFileId).ToList();
            var fileRows = await db.MediaFiles.Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id);
            // Stopped at the credits (saved before that counted as watched) isn't something to continue
            recent = recent.Where(p => fileRows.TryGetValue(p.MediaFileId, out var f) && !PlaybackEndpoints.NearlyDone(p.PositionSeconds, f.DurationSeconds))
                .Take(20).ToList();
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
            var art = ImageService.FolderArt(files.Resolve(show.LibraryId, show.Folder));
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
                f.LibraryId == first.LibraryId && f.MovieId != id && f.RelativePath.StartsWith(first.RelativePath.Substring(0, first.RelativePath.LastIndexOf('/') + 1)));
            var art = ImageService.FileArt(path) ?? (inOwnFolder ? ImageService.FolderArt(Path.GetDirectoryName(path)) : null);
            return Image(ctx, art ?? await images.FrameAsync(first.Id, path, first.DurationSeconds));
        });
    }

    private static Task<List<object>> ShowsIn(AppDbContext db, string userId, ICollection<int> libraries) =>
        ShowTiles(db, userId, db.Shows.Where(s => libraries.Contains(s.LibraryId)));

    private static Task<List<object>> MoviesIn(AppDbContext db, string userId, ICollection<int> libraries) =>
        MovieTiles(db, userId, db.Movies.Where(m => libraries.Contains(m.LibraryId)));

    /// <summary>Shows as the browsing pages list them, by title, with whether this user hid them.</summary>
    internal static async Task<List<object>> ShowTiles(AppDbContext db, string userId, IQueryable<Show> shows) =>
        (await shows.OrderBy(s => s.Title)
            .Select(s => new
            {
                s.Id, s.Title, episodes = db.MediaFiles.Count(f => f.ShowId == s.Id), image = $"/api/images/show/{s.Id}?v={ImageService.Version}",
                hidden = db.Hidden.Any(x => x.UserId == userId && x.ShowId == s.Id),
            })
            .ToListAsync()).Cast<object>().ToList();

    /// <summary>Movies as the browsing pages list them, by title, with whether this user hid them.</summary>
    internal static async Task<List<object>> MovieTiles(AppDbContext db, string userId, IQueryable<Movie> movies) =>
        (await movies.OrderBy(m => m.Title)
            .Select(m => new
            {
                m.Id, m.Title, m.Year, parts = m.Parts.Count, image = $"/api/images/movie/{m.Id}?v={ImageService.Version}",
                hidden = db.Hidden.Any(x => x.UserId == userId && x.MovieId == m.Id),
            })
            .ToListAsync()).Cast<object>().ToList();

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

    private static object FileSummary(MediaFile f, Dictionary<int, PlaybackProgress> progress, HashSet<int>? hiddenFiles = null)
    {
        progress.TryGetValue(f.Id, out var p);
        return new
        {
            f.Id, f.Title, f.EpisodeNumber, f.PartNumber, duration = f.DurationSeconds,
            playable = f.ProbeError is null && !string.IsNullOrEmpty(f.ProbeJson),
            hidden = hiddenFiles?.Contains(f.Id) ?? false,
            position = p?.PositionSeconds ?? 0, finished = p?.Finished ?? false,
            image = $"/api/images/file/{f.Id}?v={ImageService.Version}",
        };
    }

    /// <summary>
    /// The episodes before and after this one in its show (season, then episode order), skipping episodes the user hid,
    /// or a movie's other parts.
    /// </summary>
    private static async Task<(int? Previous, int? Next)> Neighbours(AppDbContext db, string userId, MediaFile f)
    {
        List<int> ordered;
        if (f.ShowId is not null)
        {
            var hiddenFiles = db.Hidden.Where(h => h.UserId == userId && h.MediaFileId != null).Select(h => h.MediaFileId);
            var all = await db.MediaFiles.Where(x => x.ShowId == f.ShowId && (x.Id == f.Id || !hiddenFiles.Contains(x.Id)))
                .Join(db.Seasons, x => x.SeasonId, s => s.Id, (x, s) => new { x.Id, s.Number, s.Series, Ep = x.EpisodeNumber ?? int.MaxValue, x.RelativePath })
                .ToListAsync();
            ordered = all.OrderBy(x => x.Series.Length > 0).ThenBy(x => x.Series, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Number).ThenBy(x => x.Ep).ThenBy(x => x.RelativePath).Select(x => x.Id).ToList();
        }
        else if (f.MovieId is not null)
        {
            ordered = await db.MediaFiles.Where(x => x.MovieId == f.MovieId).OrderBy(x => x.PartNumber).Select(x => x.Id).ToListAsync();
        }
        else return (null, null);
        var i = ordered.IndexOf(f.Id);
        return (i > 0 ? ordered[i - 1] : null, i >= 0 && i + 1 < ordered.Count ? ordered[i + 1] : null);
    }
}
