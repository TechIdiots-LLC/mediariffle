using System.Security.Claims;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace MediaRiffle.Server.Api;

public sealed record SwitchRequest(string UserId, string? Pin);
public sealed record ForgetRequest(string UserId);
public sealed record PinRequest(string Password, string? Pin);

/// <summary>
/// Switch user: the accounts signed in on this device (<see cref="DeviceAccounts"/>), to change between without a
/// password, and an optional PIN an account's owner can set so others on a shared device can't switch into it. Admin
/// still asks its own two-step check after a switch, as the check belongs to the session.
/// </summary>
public static class AccountSwitchEndpoints
{
    private static readonly PasswordHasher<AppUser> PinHasher = new();

    public static void MapAccountSwitchEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/auth/accounts");

        // This device's accounts, most recently used first. Open to anyone on the device (the sign-in page shows them):
        // names only, and only of accounts signed in here. "expired" ones need their password again.
        g.MapGet("", async (HttpContext ctx, ClaimsPrincipal me, UserManager<AppUser> users) =>
        {
            var currentId = users.GetUserId(me);
            var list = new List<object>();
            foreach (var a in DeviceAccounts.Read(ctx))
            {
                var user = await users.FindByIdAsync(a.UserId);
                if (user is null) continue;
                var valid = await DeviceAccounts.ValidAsync(users, a) is not null;
                list.Add(new
                {
                    id = user.Id, name = user.UserName, current = user.Id == currentId,
                    avatar = Avatars.Of(user),
                    pin = user.SwitchPinHash is not null, expired = !valid && user.Id != currentId,
                    admin = await users.IsInRoleAsync(user, Roles.Admin),
                });
            }
            return list;
        });

        g.MapPost("/switch", async (SwitchRequest req, HttpContext ctx, ClaimsPrincipal me, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var entry = DeviceAccounts.Read(ctx).FirstOrDefault(a => a.UserId == req.UserId);
            if (entry is null) return Results.Json(new { error = "That account isn't signed in on this device." }, statusCode: 404);
            var target = await users.FindByIdAsync(req.UserId);
            if (target is null) { DeviceAccounts.Forget(ctx, req.UserId); return Results.NotFound(); }
            if (await users.IsLockedOutAsync(target))
                return Results.Json(new { error = "That account is locked for a while after too many wrong tries." }, statusCode: 423);
            if (await DeviceAccounts.ValidAsync(users, entry) is null)
                return Results.Json(new { error = $"Sign in to {target.UserName} again.", expired = true, name = target.UserName }, statusCode: 409);

            var current = await users.GetUserAsync(me);
            if (current?.Id == target.Id) return Results.Ok();
            if (target.SwitchPinHash is { } hash)
            {
                if (string.IsNullOrEmpty(req.Pin)) return Results.Json(new { error = "Enter the PIN.", pin = true }, statusCode: 401);
                if (PinHasher.VerifyHashedPassword(target, hash, req.Pin) == PasswordVerificationResult.Failed)
                {
                    await users.AccessFailedAsync(target);
                    return Results.Json(new { error = "That PIN isn't right.", pin = true }, statusCode: 401);
                }
                await users.ResetAccessFailedCountAsync(target);
            }
            await signIn.SignInAsync(target, isPersistent: true);
            return Results.Ok();
        }).RequireRateLimiting("auth");

        // Takes an account off this device's list (signing it out here, if it's the one in use)
        g.MapPost("/forget", async (ForgetRequest req, HttpContext ctx, ClaimsPrincipal me, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            DeviceAccounts.Forget(ctx, req.UserId);
            if (users.GetUserId(me) == req.UserId) await signIn.SignOutAsync();
            return Results.Ok();
        });

        // Sets, changes or (Pin null) removes this account's switch PIN; wants the password, so whoever switched in can't
        app.MapPost("/api/auth/pin", async (PinRequest req, ClaimsPrincipal me, UserManager<AppUser> users) =>
        {
            var user = await users.GetUserAsync(me);
            if (user is null) return Results.Unauthorized();
            if (await users.IsLockedOutAsync(user))
                return Results.Json(new { error = "Too many wrong tries; try again in 15 minutes." }, statusCode: 423);
            if (!await users.CheckPasswordAsync(user, req.Password ?? ""))
            {
                await users.AccessFailedAsync(user);
                return Results.Json(new { error = "That isn't your password." }, statusCode: 400);
            }
            await users.ResetAccessFailedCountAsync(user);
            var pin = req.Pin?.Trim();
            if (!string.IsNullOrEmpty(pin) && (pin.Length is < 4 or > 8 || !pin.All(char.IsAsciiDigit)))
                return Results.BadRequest(new { error = "A PIN is 4 to 8 digits." });
            user.SwitchPinHash = string.IsNullOrEmpty(pin) ? null : PinHasher.HashPassword(user, pin);
            await users.UpdateAsync(user);
            return Results.Ok(new { pin = user.SwitchPinHash is not null });
        }).RequireAuthorization().RequireRateLimiting("auth");
    }

    /// <summary>The current sign-in was kept ("keep me signed in"): only those are remembered on the device.</summary>
    public static async Task<bool> IsKeptAsync(HttpContext ctx) =>
        (await ctx.AuthenticateAsync(IdentityConstants.ApplicationScheme)).Properties?.IsPersistent == true;
}
