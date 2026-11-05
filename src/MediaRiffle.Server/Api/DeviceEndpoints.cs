using System.Security.Claims;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

public sealed record DeviceCodeRequest(string? Name);
public sealed record DeviceTokenRequest(string? DeviceCode);
public sealed record DeviceLinkRequest(string? Code);

/// <summary>
/// Linked devices (<see cref="DevicePairings"/>, <see cref="DeviceTokens"/>): the device's side, which asks for a code
/// and then for its token; the user's side in the browser, which enters the code and lists or unlinks devices; and what
/// a device asks about itself once linked. docs/devices.md describes it for whoever writes a device's app.
/// </summary>
public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        // The device before it has a token: not a browser, so no cookie and no antiforgery token. Like the token
        // itself, only over HTTPS or on the local network.
        var device = app.MapGroup("/api/devices").AllowDevices().DisableAntiforgery();

        device.MapPost("/code", (DeviceCodeRequest req, DevicePairings pairings, HttpContext ctx) =>
        {
            if (!DeviceTokens.TransportAllowed(ctx)) return NeedsHttps();
            var p = pairings.Start(req.Name, ctx.Connection.RemoteIpAddress?.ToString());
            if (p is null) return Results.Json(new { error = "Too many devices are linking right now; try again in a few minutes." }, statusCode: 503);
            return Results.Ok(new
            {
                deviceCode = p.DeviceCode,
                userCode = DevicePairings.Display(p.UserCode),
                link = "/link",
                expiresIn = (int)DevicePairings.Lifetime.TotalSeconds,
                interval = DevicePairings.PollSeconds,
            });
        }).RequireRateLimiting("auth");

        // Asked every few seconds: 202 until someone enters the code, then the token (once), or 410 once it's run out
        device.MapPost("/token", async (DeviceTokenRequest req, DevicePairings pairings, UserManager<AppUser> users, AppDbContext db, HttpContext ctx) =>
        {
            if (!DeviceTokens.TransportAllowed(ctx)) return NeedsHttps();
            var (state, userId, name) = pairings.Poll(req.DeviceCode);
            if (state == PairingState.Waiting) return Results.Json(new { status = "waiting" }, statusCode: StatusCodes.Status202Accepted);
            var user = state == PairingState.Linked ? await users.FindByIdAsync(userId!) : null;
            if (user is null) return Results.Json(new { error = "This code has run out; start again on the device." }, statusCode: StatusCodes.Status410Gone);
            var token = DeviceTokens.NewToken();
            db.LinkedDevices.Add(new LinkedDevice
            {
                UserId = user.Id, Name = name!, TokenHash = AuthEndpoints.HashToken(token),
                LastAddress = ctx.Connection.RemoteIpAddress?.ToString(),
            });
            await db.SaveChangesAsync();
            return Results.Ok(new { token, user = user.UserName, device = name });
        }).RequireRateLimiting("devices");

        // The device, with its token: who it's signed in as, and unlinking itself (its Sign out)
        var self = app.MapGroup("/api/device").RequireAuthorization().AllowDevices();

        self.MapGet("", async (ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            if (DeviceTokens.DeviceId(me) is not { } id) return Results.NotFound();
            var user = await users.GetUserAsync(me);
            var linked = await db.LinkedDevices.FindAsync(id);
            if (user is null || linked is null) return Results.Unauthorized();
            return Results.Ok(new { user = user.UserName, device = linked.Name, maxQuality = user.MaxQuality });
        });

        self.MapDelete("", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            if (DeviceTokens.DeviceId(me) is not { } id) return Results.NotFound();
            await db.LinkedDevices.Where(d => d.Id == id).ExecuteDeleteAsync();
            return Results.Ok();
        });

        // The user, in the browser (never a device): what a code is for, entering it, and their linked devices
        var mine = app.MapGroup("/api/devices").RequireAuthorization();

        mine.MapGet("/code/{code}", (string code, DevicePairings pairings) =>
            pairings.Find(code) is { } p
                ? Results.Ok(new { code = DevicePairings.Display(p.UserCode), name = p.Name, address = p.Address })
                : NotValid()).RequireRateLimiting("auth");

        mine.MapPost("/link", (DeviceLinkRequest req, ClaimsPrincipal me, UserManager<AppUser> users, DevicePairings pairings) =>
            users.GetUserId(me) is { } userId && pairings.Approve(req.Code, userId) ? Results.Ok() : NotValid()).RequireRateLimiting("auth");

        mine.MapGet("", async (ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var userId = users.GetUserId(me)!;
            // Ones unused too long can't sign in any more anyway
            var oldest = DateTime.UtcNow - DeviceTokens.UnusedLifetime;
            await db.LinkedDevices.Where(d => d.UserId == userId && d.LastUsedUtc < oldest).ExecuteDeleteAsync();
            var list = await db.LinkedDevices.Where(d => d.UserId == userId).OrderByDescending(d => d.LastUsedUtc).ToListAsync();
            // SQLite gives the times back without their kind; say UTC, so the page shows them in local time
            return Results.Ok(list.Select(d => new
            {
                d.Id, d.Name, d.LastAddress,
                createdUtc = DateTime.SpecifyKind(d.CreatedUtc, DateTimeKind.Utc),
                lastUsedUtc = DateTime.SpecifyKind(d.LastUsedUtc, DateTimeKind.Utc),
            }));
        });

        mine.MapDelete("/{id:int}", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var userId = users.GetUserId(me)!;
            await db.LinkedDevices.Where(d => d.Id == id && d.UserId == userId).ExecuteDeleteAsync();
            return Results.Ok();
        });

        // The short address a device shows ("go to …/link"), to the page where the code is entered
        app.MapGet("/link", () => Results.Redirect("/#/link"));
    }

    /// <summary>Unlinks all of a user's devices: a new password should sign out everything signed in with the old one.</summary>
    public static Task UnlinkAllAsync(AppDbContext db, string userId) =>
        db.LinkedDevices.Where(d => d.UserId == userId).ExecuteDeleteAsync();

    private static IResult NotValid() =>
        Results.Json(new { error = "That code isn't valid. Check it, or start again on the device (codes last 10 minutes)." }, statusCode: 404);

    private static IResult NeedsHttps() =>
        Results.Json(new { error = "Linking a device needs HTTPS from outside the local network." }, statusCode: 403);
}
