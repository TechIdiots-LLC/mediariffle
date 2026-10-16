using System.Globalization;
using System.Security.Claims;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Security;

/// <summary>
/// Two-step verification for Admin, kept as marks in the sign-in cookie: when the user last verified with a passkey or
/// a code (<see cref="VerifiedClaim"/>), and when they last confirmed their password to set one up the first time
/// (<see cref="PasswordClaim"/>). Admin wants a recent verification; changing how one verifies wants one too, or, with
/// nothing set up yet, a just-confirmed password.
/// </summary>
public static class Mfa
{
    public const string VerifiedClaim = "mediariffle:mfa";
    public const string PasswordClaim = "mediariffle:password";
    /// <summary>How long a confirmed password lets someone set up their first passkey or authenticator.</summary>
    public static readonly TimeSpan PasswordLifetime = TimeSpan.FromMinutes(15);
    /// <summary>
    /// How recent a check must be to change how one verifies (add or remove a passkey or the authenticator, new recovery
    /// codes): a check with something already set up, just before, not the hours-old one that keeps Admin open.
    /// </summary>
    public static readonly TimeSpan ChangeLifetime = TimeSpan.FromMinutes(10);

    private static readonly string[] Marks = [VerifiedClaim, PasswordClaim];

    /// <summary>The user verified within <paramref name="lifetime"/>.</summary>
    public static bool IsFresh(ClaimsPrincipal user, TimeSpan lifetime, DateTimeOffset? now = null) => Within(user, VerifiedClaim, lifetime, now);

    /// <summary>The user confirmed their password within <see cref="PasswordLifetime"/>.</summary>
    public static bool PasswordConfirmed(ClaimsPrincipal user, DateTimeOffset? now = null) => Within(user, PasswordClaim, PasswordLifetime, now);

    private static bool Within(ClaimsPrincipal user, string type, TimeSpan lifetime, DateTimeOffset? now) =>
        long.TryParse(user.FindFirstValue(type), NumberStyles.Integer, CultureInfo.InvariantCulture, out var at) &&
        (now ?? DateTimeOffset.UtcNow) - DateTimeOffset.FromUnixTimeSeconds(at) is var age && age >= TimeSpan.Zero && age < lifetime;

    /// <summary>Whether the user has a passkey or an authenticator app set up.</summary>
    public static async Task<bool> IsSetUpAsync(UserManager<AppUser> users, AppUser user) =>
        user.AuthenticatorEnabled || (await users.GetPasskeysAsync(user)).Count > 0;

    /// <summary>
    /// Signs the user in again, keeping their marks and adding <paramref name="mark"/> (now), and keeping the cookie as
    /// lasting as it was. Needed after anything that changes the security stamp (it would sign them out otherwise).
    /// </summary>
    public static async Task SignInAgainAsync(HttpContext ctx, SignInManager<AppUser> signIn, AppUser user, string? mark = null)
    {
        var current = await ctx.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var claims = (current.Principal?.Claims ?? []).Where(c => Marks.Contains(c.Type) && c.Type != mark).Select(c => new Claim(c.Type, c.Value)).ToList();
        if (mark is not null) claims.Add(new Claim(mark, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        var props = current.Properties ?? new AuthenticationProperties();
        await signIn.SignInWithClaimsAsync(user, props, claims);
    }

    /// <summary>The marks survive the minutely check of the cookie against the database, which rebuilds its claims.</summary>
    public static Task KeepMarks(SecurityStampRefreshingPrincipalContext ctx)
    {
        if (ctx.NewPrincipal?.Identity is ClaimsIdentity identity && ctx.CurrentPrincipal is { } current)
            foreach (var c in current.Claims.Where(c => Marks.Contains(c.Type)))
                identity.AddClaim(new Claim(c.Type, c.Value));
        return Task.CompletedTask;
    }

    /// <summary>
    /// For the Admin endpoints: a recent verification, or 403 with <c>mfa</c> = "setup" (nothing set up yet, so set
    /// one up) or "verify" (check with a passkey or a code). Off when <see cref="AdminMfaOptions.Required"/> is false.
    /// </summary>
    public static async ValueTask<object?> RequireForAdmin(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var options = http.RequestServices.GetRequiredService<IOptions<MediaRiffleOptions>>().Value.AdminMfa;
        if (!options.Required || IsFresh(http.User, TimeSpan.FromHours(Math.Max(1, options.Hours)))) return await next(ctx);
        var users = http.RequestServices.GetRequiredService<UserManager<AppUser>>();
        var user = await users.GetUserAsync(http.User);
        if (user is null) return Results.Unauthorized();
        var setUp = await IsSetUpAsync(users, user);
        return Results.Json(new
        {
            error = setUp ? "Confirm it's you to open Admin." : "Admin needs two-step verification; set it up first.",
            mfa = setUp ? "verify" : "setup",
        }, statusCode: StatusCodes.Status403Forbidden);
    }
}
