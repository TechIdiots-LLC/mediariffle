using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

public sealed record LoginRequest(string UserName, string Password, bool Remember);
public sealed record SetupRequest(string UserName, string Password);
public sealed record RegisterRequest(string Invite, string UserName, string Password);

/// <summary>
/// Accounts. There's no open sign-up: the first visit creates the admin account, and after that people join
/// only with an invite link an admin made.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapGet("/state", async (ClaimsPrincipal user, UserManager<AppUser> users) => new
        {
            setupNeeded = !await users.Users.AnyAsync(),
            user = user.Identity?.IsAuthenticated == true
                ? new { name = user.Identity.Name, isAdmin = user.IsInRole(Roles.Admin) }
                : null,
        });

        // The antiforgery token for the signed-in (or anonymous) user; fetched again after signing in or out
        g.MapGet("/csrf", (HttpContext ctx, IAntiforgery af) =>
        {
            var tokens = af.GetAndStoreTokens(ctx);
            return Results.Ok(new { token = tokens.RequestToken });
        });

        g.MapPost("/setup", async (SetupRequest req, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            if (await users.Users.AnyAsync()) return Results.Conflict(new { error = "Setup is already done." });
            var user = new AppUser { UserName = req.UserName.Trim() };
            var created = await users.CreateAsync(user, req.Password);
            if (!created.Succeeded) return Errors(created);
            await users.AddToRoleAsync(user, Roles.Admin);
            await signIn.SignInAsync(user, isPersistent: true);
            return Results.Ok();
        }).RequireRateLimiting("auth");

        g.MapPost("/login", async (LoginRequest req, SignInManager<AppUser> signIn) =>
        {
            var result = await signIn.PasswordSignInAsync(req.UserName.Trim(), req.Password, req.Remember, lockoutOnFailure: true);
            if (result.Succeeded) return Results.Ok();
            return result.IsLockedOut
                ? Results.Json(new { error = "Too many failed sign-ins; try again in 15 minutes." }, statusCode: 423)
                : Results.Json(new { error = "Wrong user name or password." }, statusCode: 401);
        }).RequireRateLimiting("auth");

        g.MapPost("/logout", async (SignInManager<AppUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.Ok();
        });

        g.MapPost("/register", async (RegisterRequest req, AppDbContext db, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var hash = HashToken(req.Invite);
            var invite = await db.Invites.FirstOrDefaultAsync(i => i.TokenHash == hash);
            if (invite is null || invite.UsedUtc is not null || invite.ExpiresUtc < DateTime.UtcNow)
                return Results.Json(new { error = "This invite link isn't valid any more; ask for a new one." }, statusCode: 400);

            var user = new AppUser { UserName = req.UserName.Trim() };
            var created = await users.CreateAsync(user, req.Password);
            if (!created.Succeeded) return Errors(created);
            if (invite.IsAdmin) await users.AddToRoleAsync(user, Roles.Admin);
            invite.UsedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await signIn.SignInAsync(user, isPersistent: true);
            return Results.Ok();
        }).RequireRateLimiting("auth");
    }

    /// <summary>Invite tokens are stored only as their SHA-256, so the database alone can't be used to join.</summary>
    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));

    public static IResult Errors(IdentityResult r) =>
        Results.Json(new { error = string.Join(" ", r.Errors.Select(e => e.Description)) }, statusCode: 400);
}
