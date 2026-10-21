using System.Security.Claims;
using System.Security.Cryptography;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

/// <summary>
/// Play in VLC (or another player): the file as it is, so the player decodes everything itself (4K HEVC, Dolby
/// Vision, TrueHD) and the server only reads the file. The player fetches it without the user's sign-in cookie, so
/// the link carries its own key, as a Chromecast's music links do: the file and the user, encrypted with the app's
/// data protection keys, good for <see cref="Lifetime"/> and only while the user's account exists and has no quality
/// cap (the file as it is would get round one).
/// </summary>
public static class ExternalPlayerEndpoints
{
    /// <summary>Long enough to watch a long film after pausing a while.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private const string Purpose = "MediaRiffle.ExternalPlayer";

    public static void MapExternalPlayerEndpoints(this WebApplication app)
    {
        // A link to the file, for the player page's "Play in VLC"
        app.MapPost("/api/external/{fileId:int}", async (int fileId, ClaimsPrincipal me, UserManager<AppUser> users,
            AppDbContext db, LibraryFiles files, IDataProtectionProvider protection, HttpContext ctx) =>
        {
            var account = await users.GetUserAsync(me);
            if (account is null) return Results.Unauthorized();
            if (Media.Quality.Level(account.MaxQuality) is not null)
                return Results.Json(new { error = "Your account's video quality is limited, so the file can't be sent as it is." }, statusCode: 403);
            var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == fileId && (f.Kind == MediaKind.Episode || f.Kind == MediaKind.Movie));
            if (file is null || files.FullPath(file) is not { } path) return Results.NotFound();
            var token = Protector(protection).Protect($"{file.Id}:{account.Id}", DateTimeOffset.UtcNow + Lifetime);
            // Absolute (the player isn't this page), named after the file so the player shows it and knows its type
            var name = Uri.EscapeDataString(Path.GetFileName(path));
            return Results.Ok(new { url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/stream/file/{token}/{name}", name = Path.GetFileName(path) });
        }).RequireAuthorization();

        app.MapGet("/stream/file/{token}/{name}", async (string token, IDataProtectionProvider protection, UserManager<AppUser> users,
            AppDbContext db, LibraryFiles files) =>
        {
            string payload;
            try { payload = Protector(protection).Unprotect(token); }
            catch (CryptographicException) { return Results.NotFound(); }   // altered, expired, or from other keys
            var parts = payload.Split(':', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var fileId)) return Results.NotFound();
            // Checked each time: a link stops working once the account is gone or capped
            if (await users.FindByIdAsync(parts[1]) is not { } account || Media.Quality.Level(account.MaxQuality) is not null) return Results.NotFound();
            var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == fileId);
            if (file is null || files.FullPath(file) is not { } path || !File.Exists(path)) return Results.NotFound();
            return Results.File(path, PlaybackEndpoints.ContentType(path), enableRangeProcessing: true);
        }).RequireCors("stream");
    }

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider protection) =>
        protection.CreateProtector(Purpose).ToTimeLimitedDataProtector();
}
