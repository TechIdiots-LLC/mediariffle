using System.Security.Claims;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using MediaRiffle.Server.Media;
using MediaRiffle.Server.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

/// <summary>Music libraries: artists, their albums, an album's tracks, and a track's sound for the player.</summary>
public static class MusicEndpoints
{
    public static void MapMusicEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/libraries/{id:int}/artists", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, LibraryCatalog catalog, AppDbContext db) =>
            catalog.Get(id) is not { Enabled: true, Kind: LibraryKind.Music } ? Results.NotFound()
                : Results.Ok(await ArtistTiles(db, users.GetUserId(me)!, db.Artists.Where(a => a.LibraryId == id))));

        g.MapGet("/artists/{id:int}", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, LibraryCatalog catalog, AppDbContext db) =>
        {
            var artist = await db.Artists.FirstOrDefaultAsync(a => a.Id == id);
            if (artist is null || catalog.Get(artist.LibraryId) is not { Enabled: true }) return Results.NotFound();
            var userId = users.GetUserId(me)!;
            var albums = await AlbumTiles(db, userId, db.Albums.Where(a => a.ArtistId == id));
            // Albums by others that this artist plays on (a compilation's tracks)
            var appearsOn = await AlbumTiles(db, userId, db.Albums.Where(a => a.LibraryId == artist.LibraryId && a.ArtistId != id &&
                db.MediaFiles.Any(f => f.AlbumId == a.Id && f.TrackArtist == artist.Name)));
            return Results.Ok(new
            {
                artist.Id, artist.Name, artist.LibraryId, image = ArtistImage(id), albums, appearsOn,
                hidden = await db.Hidden.AnyAsync(x => x.UserId == userId && x.ArtistId == id),
            });
        });

        g.MapGet("/albums/{id:int}", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, LibraryCatalog catalog, AppDbContext db) =>
        {
            var album = await db.Albums.FirstOrDefaultAsync(a => a.Id == id);
            if (album is null || catalog.Get(album.LibraryId) is not { Enabled: true }) return Results.NotFound();
            var userId = users.GetUserId(me)!;
            var artist = await db.Artists.FirstAsync(a => a.Id == album.ArtistId);
            var tracks = await db.MediaFiles.Where(f => f.AlbumId == id)
                .OrderBy(f => f.DiscNumber ?? 1).ThenBy(f => f.TrackNumber ?? int.MaxValue).ThenBy(f => f.RelativePath).ToListAsync();
            var hiddenTracks = await HiddenEndpoints.HiddenFiles(db, userId, tracks.Select(t => t.Id));
            var ids = tracks.Select(t => t.Id).ToList();
            var favorites = (await db.UserTags.Where(x => x.UserId == userId && x.Tag == UserTag.Favorites && x.MediaFileId != null && ids.Contains(x.MediaFileId.Value))
                .Select(x => x.MediaFileId!.Value).ToListAsync()).ToHashSet();
            var discs = tracks.Select(t => t.DiscNumber ?? 1).Distinct().Count();
            return Results.Ok(new
            {
                album.Id, album.Title, album.Year, image = AlbumImage(id),
                artist = new { artist.Id, artist.Name },
                duration = tracks.Sum(t => t.DurationSeconds),
                // This album hidden, or its artist (every album of theirs is, then)
                hidden = await db.Hidden.AnyAsync(x => x.UserId == userId && x.AlbumId == id),
                artistHidden = await db.Hidden.AnyAsync(x => x.UserId == userId && x.ArtistId == artist.Id),
                tracks = tracks.Select(t => new
                {
                    t.Id, t.Title, duration = t.DurationSeconds,
                    disc = discs > 1 ? t.DiscNumber ?? 1 : (int?)null, track = t.TrackNumber,
                    // The track's own artist, where it isn't the album's (a compilation, a guest)
                    artist = t.TrackArtist is { } ta && !string.Equals(ta, artist.Name, StringComparison.OrdinalIgnoreCase) ? ta : null,
                    playable = t.ProbeError is null && !string.IsNullOrEmpty(t.ProbeJson),
                    hidden = hiddenTracks.Contains(t.Id),
                    favorite = favorites.Contains(t.Id),
                }),
            });
        });

        // One track, for the player
        g.MapGet("/tracks/{id:int}", async (int id, AppDbContext db) =>
        {
            var t = await db.MediaFiles.Where(f => f.Id == id && f.Kind == MediaKind.Track)
                .Select(f => new
                {
                    f.Id, f.Title, f.TrackArtist, duration = f.DurationSeconds, f.AlbumId,
                    Album = db.Albums.Where(a => a.Id == f.AlbumId).Select(a => new { a.Id, a.Title, a.Year, a.ArtistId }).FirstOrDefault(),
                })
                .FirstOrDefaultAsync();
            if (t?.Album is null) return Results.NotFound();
            var albumArtist = await db.Artists.Where(a => a.Id == t.Album.ArtistId).Select(a => new { a.Id, a.Name }).FirstAsync();
            return Results.Ok(new
            {
                t.Id, t.Title, t.duration, artist = t.TrackArtist ?? albumArtist.Name,
                album = new { t.Album.Id, t.Album.Title, t.Album.Year, image = AlbumImage(t.Album.Id) },
                albumArtist,
            });
        });

        // The sound: the file as it is when this browser plays it (?flac=1&ogg=1&opus=1 say what it plays besides
        // MP3, AAC and WAV), else converted to AAC. Ranges, so the player seeks.
        g.MapGet("/tracks/{id:int}/audio", async (int id, bool? flac, bool? ogg, bool? opus, AppDbContext db, LibraryFiles files, AudioService audio, CancellationToken ct) =>
        {
            var f = await db.MediaFiles.FirstOrDefaultAsync(x => x.Id == id && x.Kind == MediaKind.Track, ct);
            var path = f is null ? null : files.FullPath(f);
            if (path is null || !File.Exists(path)) return Results.NotFound();
            if (string.IsNullOrEmpty(f!.ProbeJson)) return Results.Json(new { error = "This file couldn't be read." }, statusCode: 422);
            var probe = ProbeResult.Parse(f.ProbeJson);
            if (AudioService.PlaysAsIs(probe, Path.GetExtension(path), new AudioCaps(flac == true, ogg == true, opus == true)))
                return Results.File(path, AudioService.ContentType(path), enableRangeProcessing: true);
            try
            {
                var converted = await audio.ConvertedAsync(f.Id, f.ModifiedUtc, path).WaitAsync(ct);
                return Results.File(converted, "audio/mp4", enableRangeProcessing: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                return Results.Json(new { error = "This track couldn't be converted: " + ex.Message }, statusCode: 500);
            }
        });

        // Cover art: beside the tracks (cover.jpg, folder.jpg), else the picture inside the first track that has one
        g.MapGet("/images/album/{id:int}", async (int id, AppDbContext db, LibraryFiles files, ImageService images, HttpContext ctx) =>
        {
            var album = await db.Albums.FirstOrDefaultAsync(a => a.Id == id);
            if (album is null) return Results.NotFound();
            return Image(ctx, ImageService.FolderArt(files.Resolve(album.LibraryId, album.Folder)) ?? await EmbeddedCover(db, files, images, id));
        });

        // An artist's: in their own folder (Artist/Album), else their first album's
        g.MapGet("/images/artist/{id:int}", async (int id, AppDbContext db, LibraryFiles files, ImageService images, HttpContext ctx) =>
        {
            var albums = await db.Albums.Where(a => a.ArtistId == id).OrderBy(a => a.Year).ThenBy(a => a.Title).ToListAsync();
            foreach (var album in albums)
            {
                var parent = album.Folder.Contains('/') ? album.Folder[..album.Folder.LastIndexOf('/')] : null;
                var art = (parent is null ? null : ImageService.FolderArt(files.Resolve(album.LibraryId, parent)))
                          ?? ImageService.FolderArt(files.Resolve(album.LibraryId, album.Folder));
                if (art is not null) return Image(ctx, art);
            }
            foreach (var album in albums)
                if (await EmbeddedCover(db, files, images, album.Id) is { } cover) return Image(ctx, cover);
            return Results.NotFound();
        });
    }

    /// <summary>The picture inside the album's first track that has one (by disc and track), or null.</summary>
    private static async Task<string?> EmbeddedCover(AppDbContext db, LibraryFiles files, ImageService images, int albumId)
    {
        var tracks = await db.MediaFiles.Where(f => f.AlbumId == albumId && f.ProbeJson != "")
            .OrderBy(f => f.DiscNumber ?? 1).ThenBy(f => f.TrackNumber ?? int.MaxValue).ToListAsync();
        foreach (var t in tracks)
        {
            if (!ProbeResult.Parse(t.ProbeJson).HasCoverArt || files.FullPath(t) is not { } path) continue;
            if (await images.CoverAsync(t.Id, t.ModifiedUtc, path) is { } cover) return cover;
        }
        return null;
    }

    internal static string AlbumImage(int id) => $"/api/images/album/{id}?v={ImageService.Version}";
    internal static string ArtistImage(int id) => $"/api/images/artist/{id}?v={ImageService.Version}";

    /// <summary>Artists as the browsing pages list them, by name, with how many albums each has and whether this user hid them.</summary>
    internal static async Task<List<object>> ArtistTiles(AppDbContext db, string userId, IQueryable<Artist> artists) =>
        (await artists.OrderBy(a => a.Name)
            .Select(a => new
            {
                a.Id, a.Name, albums = db.Albums.Count(x => x.ArtistId == a.Id),
                hidden = db.Hidden.Any(x => x.UserId == userId && x.ArtistId == a.Id),
            })
            .ToListAsync()).Select(a => (object)new { a.Id, a.Name, a.albums, a.hidden, image = ArtistImage(a.Id) }).ToList();

    /// <summary>Albums, newest first then by title, with their artist and track count, and whether this user hid them (or their artist).</summary>
    internal static async Task<List<object>> AlbumTiles(AppDbContext db, string userId, IQueryable<Album> albums) =>
        (await albums.OrderByDescending(a => a.Year).ThenBy(a => a.Title)
            .Select(a => new
            {
                a.Id, a.Title, a.Year,
                artist = db.Artists.Where(x => x.Id == a.ArtistId).Select(x => x.Name).FirstOrDefault(),
                tracks = db.MediaFiles.Count(f => f.AlbumId == a.Id),
                hidden = db.Hidden.Any(x => x.UserId == userId && (x.AlbumId == a.Id || x.ArtistId == a.ArtistId)),
            })
            .ToListAsync()).Select(a => (object)new { a.Id, a.Title, a.Year, a.artist, a.tracks, a.hidden, image = AlbumImage(a.Id) }).ToList();

    /// <summary>Single tracks (a playlist's), in the order given, with their artist and album.</summary>
    internal static async Task<List<object>> TrackRows(AppDbContext db, string userId, IQueryable<MediaFile> files)
    {
        var mine = HiddenEndpoints.NotHidden(db, userId, db.MediaFiles);
        return (await files
            .Select(f => new
            {
                f.Id, f.Title, duration = f.DurationSeconds, f.AlbumId,
                artist = f.TrackArtist ?? db.Albums.Where(a => a.Id == f.AlbumId).Join(db.Artists, a => a.ArtistId, x => x.Id, (a, x) => x.Name).FirstOrDefault(),
                album = db.Albums.Where(a => a.Id == f.AlbumId).Select(a => a.Title).FirstOrDefault(),
                playable = f.ProbeError == null && f.ProbeJson != "",
                hidden = !mine.Any(x => x.Id == f.Id),
            })
            .ToListAsync())
            .Select(t => (object)new { t.Id, t.Title, t.duration, t.artist, t.album, t.AlbumId, t.playable, t.hidden, image = t.AlbumId is { } a ? AlbumImage(a) : null })
            .ToList();
    }

    private static IResult Image(HttpContext ctx, string? path)
    {
        if (path is null) return Results.NotFound();
        ctx.Response.Headers.CacheControl = "private, max-age=86400";
        var type = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
        return Results.File(path, type, enableRangeProcessing: false);
    }
}
