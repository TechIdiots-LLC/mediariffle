using System.Security.Claims;
using System.Security.Cryptography;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using MediaRiffle.Server.Media;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

/// <summary>
/// Music on a Chromecast. The Chromecast fetches a track's sound and cover itself, without the user's sign-in cookie,
/// so the page asks for links that carry their own key instead: the track and the user, encrypted with the app's data
/// protection keys, good for <see cref="Lifetime"/> and only while the user's account exists. Like the video
/// streams' session ids, they're read-only and work from any origin.
/// </summary>
public static class MusicCastEndpoints
{
    /// <summary>What a Chromecast plays besides MP3, AAC and WAV.</summary>
    public static readonly AudioCaps Chromecast = new(Flac: true, Ogg: true, Opus: true);

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private const string Purpose = "MediaRiffle.MusicCast";
    private const int MaxTracks = 200;

    public sealed record CastTracksRequest(List<int>? Ids);

    public static void MapMusicCastEndpoints(this WebApplication app)
    {
        // Links for these tracks, in the order asked for (a window of the play queue, for the Chromecast's own queue)
        app.MapPost("/api/cast/tracks", async (CastTracksRequest req, ClaimsPrincipal me, UserManager<AppUser> users,
            AppDbContext db, LibraryFiles files, IDataProtectionProvider protection) =>
        {
            var ids = (req.Ids ?? []).Distinct().Take(MaxTracks).ToList();
            var userId = users.GetUserId(me)!;
            var rows = await db.MediaFiles.Where(f => ids.Contains(f.Id) && f.Kind == MediaKind.Track && f.ProbeJson != "")
                .Select(f => new
                {
                    File = f,
                    Album = db.Albums.Where(a => a.Id == f.AlbumId).Select(a => a.Title).FirstOrDefault(),
                    AlbumArtist = db.Albums.Where(a => a.Id == f.AlbumId).Join(db.Artists, a => a.ArtistId, x => x.Id, (a, x) => x.Name).FirstOrDefault(),
                })
                .ToDictionaryAsync(r => r.File.Id);
            return ids.Where(rows.ContainsKey).Select(id => rows[id]).Where(r => files.FullPath(r.File) is not null).Select(r =>
            {
                var f = r.File;
                var token = Token(protection, f.Id, userId, DateTimeOffset.UtcNow + Lifetime);
                var asIs = AudioService.PlaysAsIs(ProbeResult.Parse(f.ProbeJson), Path.GetExtension(f.RelativePath), Chromecast);
                return new
                {
                    id = f.Id,
                    url = $"/stream/music/{token}/audio",
                    cover = $"/stream/music/{token}/cover",
                    contentType = asIs ? AudioService.ContentType(f.RelativePath) : "audio/mp4",
                    title = f.Title, artist = f.TrackArtist ?? r.AlbumArtist, album = r.Album, track = f.TrackNumber,
                    duration = f.DurationSeconds,
                };
            }).ToList();
        }).RequireAuthorization();

        var g = app.MapGroup("/stream/music/{token}").RequireCors("stream");

        g.MapGet("/audio", async (string token, IDataProtectionProvider protection, UserManager<AppUser> users, AppDbContext db,
            LibraryFiles files, AudioService audio, CancellationToken ct) =>
            await ResolveAsync(token, protection, UserExists(users), db) is { } f ? await MusicEndpoints.AudioAsync(f, Chromecast, files, audio, ct) : Results.NotFound());

        g.MapGet("/cover", async (string token, IDataProtectionProvider protection, UserManager<AppUser> users, AppDbContext db,
            LibraryFiles files, ImageService images, HttpContext ctx) =>
            await ResolveAsync(token, protection, UserExists(users), db) is { AlbumId: { } albumId }
                ? MusicEndpoints.Image(ctx, await MusicEndpoints.AlbumArtAsync(db, files, images, albumId))
                : Results.NotFound());
    }

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider protection) =>
        protection.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    /// <summary>The key in a track's Chromecast links: the track and the user, encrypted, good until <paramref name="expires"/>.</summary>
    public static string Token(IDataProtectionProvider protection, int fileId, string userId, DateTimeOffset expires) =>
        Protector(protection).Protect($"{fileId}:{userId}", expires);

    private static Func<string, Task<bool>> UserExists(UserManager<AppUser> users) => async id => await users.FindByIdAsync(id) is not null;

    /// <summary>The track a link is for, while it's in date and its user still exists; null otherwise.</summary>
    public static async Task<MediaFile?> ResolveAsync(string token, IDataProtectionProvider protection, Func<string, Task<bool>> userExists, AppDbContext db)
    {
        string payload;
        try { payload = Protector(protection).Unprotect(token); }
        catch (CryptographicException) { return null; }   // altered, expired, or from other keys
        var parts = payload.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var fileId)) return null;
        if (!await userExists(parts[1])) return null;
        return await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.Kind == MediaKind.Track);
    }
}
