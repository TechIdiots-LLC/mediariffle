using System.Security.Claims;
using System.Security.Cryptography;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using MediaRiffle.Server.Media;
using MediaRiffle.Server.Security;
using MediaRiffle.Server.Streaming;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

public sealed record InviteRequest(bool IsAdmin, int Days);
public sealed record QualityCapRequest(string? MaxQuality);
public sealed record RoleRequest(bool IsAdmin);
public sealed record LibraryRequest(string Name, Options.LibraryKind Kind, string Path, string? Tags, bool Enabled = true);
public sealed record PathCheckRequest(string Path, Options.LibraryKind Kind = Options.LibraryKind.Shows);

/// <summary>Admin only: invites, users, library scans and the streams running now.</summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        // Admins, and only after two-step verification (a passkey or an authenticator code) in the last few hours
        var g = app.MapGroup("/api/admin").RequireAuthorization("Admin").AddEndpointFilter(Mfa.RequireForAdmin);

        g.MapPost("/invites", async (InviteRequest req, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db, HttpContext ctx) =>
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');
            db.Invites.Add(new Invite
            {
                TokenHash = AuthEndpoints.HashToken(token),
                CreatedByUserId = users.GetUserId(me)!,
                IsAdmin = req.IsAdmin,
                ExpiresUtc = DateTime.UtcNow.AddDays(Math.Clamp(req.Days, 1, 30)),
            });
            await db.SaveChangesAsync();
            // Shown once: only the hash is kept
            return Results.Ok(new { link = $"{ctx.Request.Scheme}://{ctx.Request.Host}/#/join/{token}" });
        });

        g.MapGet("/invites", async (AppDbContext db) =>
            await db.Invites.OrderByDescending(i => i.Id)
                .Select(i => new { i.Id, i.IsAdmin, i.ExpiresUtc, i.UsedUtc })
                .ToListAsync());

        g.MapDelete("/invites/{id:int}", async (int id, AppDbContext db) =>
        {
            await db.Invites.Where(i => i.Id == id).ExecuteDeleteAsync();
            return Results.Ok();
        });

        g.MapGet("/users", async (UserManager<AppUser> users) =>
        {
            var admins = (await users.GetUsersInRoleAsync(Roles.Admin)).Select(u => u.Id).ToHashSet();
            var list = new List<object>();
            foreach (var u in await users.Users.OrderBy(u => u.UserName).ToListAsync())
                list.Add(new
                {
                    u.Id, u.UserName, u.CreatedUtc, isAdmin = admins.Contains(u.Id), lockedOut = u.LockoutEnd > DateTimeOffset.UtcNow, u.MaxQuality,
                    preferredQuality = u.PreferredQuality ?? "auto",
                    // Has a passkey or an authenticator app (two-step verification)
                    mfa = await Mfa.IsSetUpAsync(users, u),
                });
            return list;
        });

        // How Auto picks for a user (the same preference they can change under Profile): their default, set by an admin
        g.MapPost("/users/{id}/preferred-quality", async (string id, PreferencesRequest req, UserManager<AppUser> users) =>
        {
            var user = await users.FindByIdAsync(id);
            if (user is null) return Results.NotFound();
            var q = (req.Quality ?? "auto").Trim().ToLowerInvariant();
            if (!Quality.Preferences.Contains(q)) return Results.BadRequest(new { error = "Unknown quality preference." });
            user.PreferredQuality = q == "auto" ? null : q;
            await users.UpdateAsync(user);
            return Results.Ok();
        });

        // The best quality a user may stream (e.g. "720" for someone on a slow connection), or none for no limit
        g.MapPost("/users/{id}/quality", async (string id, QualityCapRequest req, UserManager<AppUser> users) =>
        {
            var user = await users.FindByIdAsync(id);
            if (user is null) return Results.NotFound();
            if (req.MaxQuality is not null && Quality.Level(req.MaxQuality) is null)
                return Results.BadRequest(new { error = "Unknown quality." });
            user.MaxQuality = req.MaxQuality;
            await users.UpdateAsync(user);
            return Results.Ok();
        });

        // Make a viewer an admin, or an admin a viewer. Not yourself (so the last admin can't lock everyone out of
        // Admin by accident); the change reaches the user's open sessions within a minute (see Program.cs).
        g.MapPost("/users/{id}/role", async (string id, RoleRequest req, ClaimsPrincipal me, UserManager<AppUser> users) =>
        {
            if (users.GetUserId(me) == id) return Results.BadRequest(new { error = "You can't change your own role; another admin can." });
            var user = await users.FindByIdAsync(id);
            if (user is null) return Results.NotFound();
            var isAdmin = await users.IsInRoleAsync(user, Roles.Admin);
            if (req.IsAdmin == isAdmin) return Results.Ok();
            var result = req.IsAdmin ? await users.AddToRoleAsync(user, Roles.Admin) : await users.RemoveFromRoleAsync(user, Roles.Admin);
            return result.Succeeded ? Results.Ok() : Results.BadRequest(new { error = string.Join(" ", result.Errors.Select(e => e.Description)) });
        });

        g.MapGet("/qualities", () => Quality.Ladder.Select(l => new { l.Id, l.Label }));

        g.MapDelete("/users/{id}", async (string id, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            if (users.GetUserId(me) == id) return Results.BadRequest(new { error = "You can't remove your own account." });
            var user = await users.FindByIdAsync(id);
            if (user is null) return Results.NotFound();
            await db.Progress.Where(p => p.UserId == id).ExecuteDeleteAsync();
            await users.DeleteAsync(user);
            return Results.Ok();
        });

        // Someone who lost their passkeys, authenticator and recovery codes: removes them all, so they can set it up
        // again (an admin is guided through it the next time they open Admin). Not yourself.
        g.MapPost("/users/{id}/reset-mfa", async (string id, ClaimsPrincipal me, UserManager<AppUser> users) =>
        {
            if (users.GetUserId(me) == id) return Results.BadRequest(new { error = "Change your own under Profile; another admin can reset it." });
            var user = await users.FindByIdAsync(id);
            if (user is null) return Results.NotFound();
            foreach (var p in await users.GetPasskeysAsync(user)) await users.RemovePasskeyAsync(user, p.CredentialId);
            user.AuthenticatorEnabled = false;
            await users.UpdateAsync(user);
            await users.ResetAuthenticatorKeyAsync(user);
            await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
            return Results.Ok();
        });

        g.MapPost("/users/{id}/unlock", async (string id, UserManager<AppUser> users) =>
        {
            var user = await users.FindByIdAsync(id);
            if (user is null) return Results.NotFound();
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            return Results.Ok();
        });

        // ── Libraries ──
        g.MapGet("/libraries", async (AppDbContext db, LibraryScanner scanner) =>
        {
            var counts = await db.MediaFiles.GroupBy(f => f.LibraryId)
                .Select(x => new { x.Key, n = x.Count(), bad = x.Count(f => f.ProbeError != null) })
                .ToDictionaryAsync(x => x.Key);
            return (await db.Libraries.OrderBy(l => l.Name).ToListAsync())
                .Select(l => new
                {
                    l.Id, l.Name, kind = l.Kind.ToString(), l.Path, l.Tags, l.Enabled,
                    files = counts.TryGetValue(l.Id, out var c) ? c.n : 0,
                    unreadable = c?.bad ?? 0,
                    status = scanner.Status.TryGetValue(l.Id, out var s) ? s : null,
                });
        });

        g.MapPost("/libraries", async (LibraryRequest req, AppDbContext db, LibraryCatalog catalog) =>
        {
            if (await Validate(req, db, null) is { } problem) return problem;
            db.Libraries.Add(new MediaLibrary
            {
                Name = req.Name.Trim(), Kind = req.Kind, Path = req.Path.Trim(),
                Tags = MediaLibrary.NormalizeTags(req.Tags), Enabled = req.Enabled,
            });
            await db.SaveChangesAsync();
            await catalog.ReloadAsync();   // and the scanner picks it up
            return Results.Ok();
        });

        g.MapPut("/libraries/{id:int}", async (int id, LibraryRequest req, AppDbContext db, LibraryCatalog catalog) =>
        {
            var lib = await db.Libraries.FirstOrDefaultAsync(l => l.Id == id);
            if (lib is null) return Results.NotFound();
            if (await Validate(req, db, id) is { } problem) return problem;
            // A new folder or kind means what was scanned from the old one no longer applies
            if (!string.Equals(lib.Path, req.Path.Trim(), StringComparison.Ordinal) || lib.Kind != req.Kind)
                await ForgetContents(db, id);
            lib.Name = req.Name.Trim();
            lib.Kind = req.Kind;
            lib.Path = req.Path.Trim();
            lib.Tags = MediaLibrary.NormalizeTags(req.Tags);
            lib.Enabled = req.Enabled;
            await db.SaveChangesAsync();
            await catalog.ReloadAsync();
            return Results.Ok();
        });

        g.MapDelete("/libraries/{id:int}", async (int id, AppDbContext db, LibraryCatalog catalog) =>
        {
            await ForgetContents(db, id);
            await db.Libraries.Where(l => l.Id == id).ExecuteDeleteAsync();
            await catalog.ReloadAsync();
            return Results.Ok();
        });

        // Before saving: can the service account see the folder, and are there videos (music, for Music) in it?
        g.MapPost("/libraries/check", async (PathCheckRequest req) =>
        {
            var path = req.Path?.Trim() ?? "";
            if (!System.IO.Path.IsPathFullyQualified(path))
                return Results.Ok(new { ok = false, message = "Give a full path, e.g. /mnt/media/Shows or \\\\server\\share\\Shows." });
            // A file server that's down can make this hang; give up after 15 s
            var check = Task.Run(() =>
            {
                if (!Directory.Exists(path)) return (false, "The folder doesn't exist, or this server's account can't see it.", 0, new List<string>());
                var sample = new List<string>();
                int count = 0;
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var f in Directory.EnumerateFiles(path, "*", opts))
                {
                    if (!MediaNameParser.Belongs(f, req.Kind)) continue;
                    if (sample.Count < 5) sample.Add(System.IO.Path.GetRelativePath(path, f).Replace('\\', '/'));
                    if (++count >= 1000) break;
                }
                var what = req.Kind == Options.LibraryKind.Music ? "music" : "video";
                return (true, count == 0 ? $"The folder is readable but has no {what} files." : $"Readable: {(count >= 1000 ? "1000+" : count)} {what} files.", count, sample);
            });
            if (await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(15))) != check)
                return Results.Ok(new { ok = false, message = "No answer within 15 s: is the file server reachable?" });
            try
            {
                var (ok, message, count, sample) = await check;
                return Results.Ok(new { ok, message, count, sample });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return Results.Ok(new { ok = false, message = "Can't read the folder: " + ex.Message });
            }
        });

        g.MapPost("/scan", (LibraryScanner scanner) => { scanner.RequestScan(); return Results.Accepted(); });
        g.MapGet("/scan", (LibraryScanner scanner) => new { scanner.IsScanning, scanner.LastScanUtc, scanner.LastResult, scanner.Progress });

        g.MapGet("/sessions", async (StreamSessionManager sessions, UserManager<AppUser> users) =>
        {
            var names = await users.Users.ToDictionaryAsync(u => u.Id, u => u.UserName);
            return sessions.All.Select(s => new
            {
                user = names.GetValueOrDefault(s.UserId), s.FileId, mode = s.Plan.Mode.ToString(), s.Plan.Reason,
                s.StartSeconds, s.CreatedUtc, s.LastAccessUtc,
            });
        });
    }

    private static async Task<IResult?> Validate(LibraryRequest req, AppDbContext db, int? id)
    {
        var name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100) return Results.BadRequest(new { error = "Give the library a name (up to 100 characters)." });
        if (await db.Libraries.AnyAsync(l => l.Name == name && l.Id != id))
            return Results.BadRequest(new { error = $"There's already a library called {name}." });
        if (!System.IO.Path.IsPathFullyQualified(req.Path?.Trim() ?? ""))
            return Results.BadRequest(new { error = "The folder must be a full path, e.g. /mnt/media/Shows or \\\\server\\share\\Shows." });
        if (!Enum.IsDefined(req.Kind)) return Results.BadRequest(new { error = "Unknown library kind." });
        return null;
    }

    /// <summary>Removes what was scanned from a library, and everyone's positions in it.</summary>
    private static async Task ForgetContents(AppDbContext db, int libraryId)
    {
        var files = db.MediaFiles.Where(f => f.LibraryId == libraryId).Select(f => f.Id);
        await db.Progress.Where(p => files.Contains(p.MediaFileId)).ExecuteDeleteAsync();
        await db.MediaFiles.Where(f => f.LibraryId == libraryId).ExecuteDeleteAsync();
        await db.Seasons.Where(s => db.Shows.Any(sh => sh.Id == s.ShowId && sh.LibraryId == libraryId)).ExecuteDeleteAsync();
        await db.Shows.Where(s => s.LibraryId == libraryId).ExecuteDeleteAsync();
        await db.Movies.Where(m => m.LibraryId == libraryId).ExecuteDeleteAsync();
    }
}
