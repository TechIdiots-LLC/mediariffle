using System.Security.Claims;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Options;
using MediaRiffle.Server.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Api;

public sealed record AvatarRequest(int Emoji);

/// <summary>Profile pictures (see <see cref="Avatars"/>): choosing a built-in one, sending a photo, or going back.</summary>
public static class AvatarEndpoints
{
    public static void MapAvatarEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/auth/avatar").RequireAuthorization();

        // One of the built-in pictures (the user's own photo, if any, goes)
        g.MapPut("", async (AvatarRequest req, ClaimsPrincipal me, UserManager<AppUser> users, IOptions<MediaRiffleOptions> options) =>
        {
            var user = await users.GetUserAsync(me);
            if (user is null) return Results.Unauthorized();
            if (req.Emoji is < 0 or >= Avatars.BuiltIn) return Results.BadRequest(new { error = "Unknown picture." });
            DeletePhoto(options.Value, user.Id);
            user.Avatar = $"emoji:{req.Emoji}";
            await users.UpdateAsync(user);
            return Results.Ok(Avatars.Of(user));
        });

        // The user's own photo: a small square JPEG the page made (cropped, shrunk, turned the right way up)
        g.MapPost("/photo", async (HttpRequest request, ClaimsPrincipal me, UserManager<AppUser> users, IOptions<MediaRiffleOptions> options, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(me);
            if (user is null) return Results.Unauthorized();
            using var body = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (body.Length + read > Avatars.MaxPhotoBytes) return Results.BadRequest(new { error = "That picture is too big." });
                body.Write(buffer, 0, read);
            }
            if (!Avatars.IsJpeg(body.GetBuffer().AsSpan(0, (int)body.Length))) return Results.BadRequest(new { error = "That isn't a picture this can use." });
            var dir = options.Value.AvatarDirectory;
            Directory.CreateDirectory(dir);
            var path = PhotoPath(options.Value, user.Id);
            await File.WriteAllBytesAsync(path + ".tmp", body.ToArray(), ct);
            File.Move(path + ".tmp", path, overwrite: true);
            user.Avatar = $"photo:{DateTime.UtcNow.Ticks}";
            await users.UpdateAsync(user);
            return Results.Ok(Avatars.Of(user));
        });

        // Back to the built-in picture their account picks
        g.MapDelete("", async (ClaimsPrincipal me, UserManager<AppUser> users, IOptions<MediaRiffleOptions> options) =>
        {
            var user = await users.GetUserAsync(me);
            if (user is null) return Results.Unauthorized();
            DeletePhoto(options.Value, user.Id);
            user.Avatar = null;
            await users.UpdateAsync(user);
            return Results.Ok(Avatars.Of(user));
        });

        // A user's photo: for anyone signed in, and on the sign-in page for the accounts signed in on this device
        app.MapGet("/api/avatars/{userId}", (string userId, HttpContext ctx, ClaimsPrincipal me, IOptions<MediaRiffleOptions> options) =>
        {
            var allowed = me.Identity?.IsAuthenticated == true || DeviceAccounts.Read(ctx).Any(a => a.UserId == userId);
            if (!allowed || userId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return Results.NotFound();
            var path = PhotoPath(options.Value, userId);
            if (!File.Exists(path)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=604800";   // ?v= changes with each new photo
            return Results.File(path, "image/jpeg");
        });
    }

    private static string PhotoPath(MediaRiffleOptions o, string userId) => Path.Combine(o.AvatarDirectory, $"{userId}.jpg");

    /// <summary>Removes a user's photo, if they have one (a new picture, or the account removed).</summary>
    public static void DeletePhoto(MediaRiffleOptions o, string userId)
    {
        try { File.Delete(PhotoPath(o, userId)); } catch (IOException) { }
    }
}
