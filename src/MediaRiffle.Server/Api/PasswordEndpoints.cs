using System.Security.Claims;
using System.Text;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace MediaRiffle.Server.Api;

public sealed record ChangePasswordRequest(string Current, string New);
public sealed record ResetPasswordRequest(string UserId, string Token, string Password, bool Remember = true);

/// <summary>
/// Passwords: a user changes their own (Profile → Password), and an admin makes a one-time reset link for someone who
/// forgot theirs (Admin → Users), which that person opens to choose a new one, so the admin never knows it. Either
/// way the account's other sign-ins end (its security stamp changes); a reset also lifts a lockout.
/// </summary>
public static class PasswordEndpoints
{
    /// <summary>How long an admin's reset link works (Identity's reset tokens; one use, as using it changes the stamp).</summary>
    public static readonly TimeSpan ResetLinkLifetime = TimeSpan.FromHours(24);

    public static void MapPasswordEndpoints(this WebApplication app)
    {
        // The user's own: the current password first (a wrong one counts toward the lockout, as at sign-in)
        app.MapPost("/api/auth/password", async (ChangePasswordRequest req, HttpContext ctx, ClaimsPrincipal me, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = await users.GetUserAsync(me);
            if (user is null) return Results.Unauthorized();
            if (await users.IsLockedOutAsync(user))
                return Results.Json(new { error = "Too many wrong tries; try again in 15 minutes." }, statusCode: 423);
            if (!await users.CheckPasswordAsync(user, req.Current ?? ""))
            {
                await users.AccessFailedAsync(user);
                return Results.Json(new { error = "Your current password isn't right." }, statusCode: 400);
            }
            await users.ResetAccessFailedCountAsync(user);
            var changed = await users.ChangePasswordAsync(user, req.Current!, req.New ?? "");
            if (!changed.Succeeded) return AuthEndpoints.Errors(changed);
            // The stamp changed, which signs out every session: this one stays (keeping its two-step check)
            await Mfa.SignInAgainAsync(ctx, signIn, user);
            return Results.Ok();
        }).RequireAuthorization().RequireRateLimiting("auth");

        // The person with an admin's reset link: a new password, then signed in with it
        app.MapPost("/api/auth/reset-password", async (ResetPasswordRequest req, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = await users.FindByIdAsync(req.UserId ?? "");
            string token;
            try { token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(req.Token ?? "")); }
            catch (FormatException) { token = ""; }
            if (user is null || token.Length == 0) return Expired();
            var reset = await users.ResetPasswordAsync(user, token, req.Password ?? "");
            if (!reset.Succeeded)
                return reset.Errors.Any(e => e.Code == "InvalidToken") ? Expired() : AuthEndpoints.Errors(reset);
            // Someone locked out by wrong guesses gets back in with the new password
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            await signIn.SignInAsync(user, isPersistent: req.Remember);
            return Results.Ok(new { name = user.UserName });
        }).RequireRateLimiting("auth");
    }

    /// <summary>The link an admin sends: the user and a reset token, in the page's address (#/reset/…).</summary>
    public static async Task<string> ResetLinkAsync(UserManager<AppUser> users, AppUser user, HttpRequest request)
    {
        var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GeneratePasswordResetTokenAsync(user)));
        return $"{request.Scheme}://{request.Host}/#/reset/{Uri.EscapeDataString(user.Id)}/{token}";
    }

    private static IResult Expired() =>
        Results.Json(new { error = "This link has been used or has run out (they last 24 hours). Ask an admin for a new one." }, statusCode: 400);
}
