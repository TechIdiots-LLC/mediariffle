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

/// <summary>Admin only: invites, users, library scans and the streams running now.</summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization("Admin");

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
            return (await users.Users.OrderBy(u => u.UserName).ToListAsync())
                .Select(u => new { u.Id, u.UserName, u.CreatedUtc, isAdmin = admins.Contains(u.Id), lockedOut = u.LockoutEnd > DateTimeOffset.UtcNow, u.MaxQuality });
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

        g.MapPost("/users/{id}/unlock", async (string id, UserManager<AppUser> users) =>
        {
            var user = await users.FindByIdAsync(id);
            if (user is null) return Results.NotFound();
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            return Results.Ok();
        });

        g.MapPost("/scan", (LibraryScanner scanner) => { scanner.RequestScan(); return Results.Accepted(); });
        g.MapGet("/scan", (LibraryScanner scanner) => new { scanner.IsScanning, scanner.LastScanUtc, scanner.LastResult });

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
}
