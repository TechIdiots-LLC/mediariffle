using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Options;
using MediaRiffle.Server.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Api;

public sealed record LoginRequest(string UserName, string Password, bool Remember);
public sealed record SetupRequest(string UserName, string Password);
public sealed record RegisterRequest(string Invite, string UserName, string Password);
public sealed record PreferencesRequest(string? Quality);

/// <summary>
/// Accounts. There's no open sign-up: the first visit creates the admin account, and after that people join
/// only with an invite link an admin made.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapGet("/state", async (HttpContext ctx, ClaimsPrincipal user, UserManager<AppUser> users, IOptions<MediaRiffleOptions> options) =>
        {
            // The account in use stays at the top of this device's list, with its stamp as it is now (it changes with
            // e.g. setting up two-step verification, which would otherwise make switching back ask for the password)
            if (user.Identity?.IsAuthenticated == true && await AccountSwitchEndpoints.IsKeptAsync(ctx) && await users.GetUserAsync(user) is { } me)
                await DeviceAccounts.RememberAsync(ctx, users, me);
            return new
            {
                version = Version,
                setupNeeded = !await users.Users.AnyAsync(),
                google = options.Value.Google.Enabled,
                user = user.Identity?.IsAuthenticated == true
                    ? new { name = user.Identity.Name, isAdmin = user.IsInRole(Roles.Admin) }
                    : null,
                // Other accounts signed in on this device, for Switch user
                deviceAccounts = DeviceAccounts.Read(ctx).Count,
            };
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

        // A kept sign-in ("keep me signed in") joins this device's accounts for Switch user, by the cookie's OnSigningIn
        g.MapPost("/login", async (LoginRequest req, SignInManager<AppUser> signIn) =>
        {
            var result = await signIn.PasswordSignInAsync(req.UserName.Trim(), req.Password, req.Remember, lockoutOnFailure: true);
            if (result.Succeeded) return Results.Ok();
            return result.IsLockedOut
                ? Results.Json(new { error = "Too many failed sign-ins; try again in 15 minutes." }, statusCode: 423)
                : Results.Json(new { error = "Wrong user name or password." }, statusCode: 401);
        }).RequireRateLimiting("auth");

        // Signs out the account in use and takes it off this device (?all=true: every account on it). Says how many
        // others are still signed in here, for the page to offer them.
        g.MapPost("/logout", async (bool? all, HttpContext ctx, ClaimsPrincipal me, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            if (all == true) DeviceAccounts.ForgetAll(ctx);
            else if (users.GetUserId(me) is { } id) DeviceAccounts.Forget(ctx, id);
            await signIn.SignOutAsync();
            return Results.Ok(new { others = all == true ? 0 : DeviceAccounts.Read(ctx).Count(a => a.UserId != users.GetUserId(me)) });
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

        // The user's own preference for Auto: auto (by the connection), high (best first), medium (≤720p), low (≤480p)
        g.MapPut("/preferences", async (PreferencesRequest req, ClaimsPrincipal principal, UserManager<AppUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            var q = (req.Quality ?? "auto").Trim().ToLowerInvariant();
            if (!Media.Quality.Preferences.Contains(q)) return Results.BadRequest(new { error = "Unknown quality preference." });
            user.PreferredQuality = q == "auto" ? null : q;
            await users.UpdateAsync(user);
            return Results.Ok(new { preferredQuality = q });
        }).RequireAuthorization();

        g.MapGet("/account", async (ClaimsPrincipal principal, UserManager<AppUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            var google = (await users.GetLoginsAsync(user)).FirstOrDefault(l => l.LoginProvider == GoogleDefaults.AuthenticationScheme);
            return Results.Ok(new
            {
                name = user.UserName, google = google?.ProviderDisplayName, pin = user.SwitchPinHash is not null,
                preferredQuality = user.PreferredQuality ?? "auto", maxQuality = user.MaxQuality,
            });
        }).RequireAuthorization();

        MapGoogle(g);
    }

    /// <summary>
    /// Google sign-in. These are page navigations, not fetches: each goes to Google and comes back through
    /// /signin-google to /api/auth/google/done, which sends the browser home with ?google=… saying what happened.
    /// Google's answer is only ever matched to an account that linked it; nobody is created or matched by e-mail.
    /// </summary>
    static void MapGoogle(RouteGroupBuilder g)
    {
        const string Provider = GoogleDefaults.AuthenticationScheme;
        // Who asked to link, carried inside Google's encrypted state: the sign-in cookie is SameSite=Strict, so the
        // browser doesn't send it on the way back from Google
        const string LinkUser = "mediariffle.link";

        g.MapGet("/google/signin", (IOptions<MediaRiffleOptions> options) =>
            options.Value.Google.Enabled
                ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/api/auth/google/done" }, [Provider])
                : Results.NotFound()).RequireRateLimiting("auth");

        // Reached by a link on the Account page; another site can't start it, since the Strict cookie isn't sent then
        g.MapGet("/google/link", (ClaimsPrincipal principal, UserManager<AppUser> users, IOptions<MediaRiffleOptions> options) =>
        {
            if (!options.Value.Google.Enabled) return Results.NotFound();
            var props = new AuthenticationProperties { RedirectUri = "/api/auth/google/done" };
            props.Items[LinkUser] = users.GetUserId(principal);
            // Lets someone with several Google accounts choose which one to link
            props.SetParameter("prompt", "select_account");
            return Results.Challenge(props, [Provider]);
        }).RequireAuthorization().RequireRateLimiting("auth");

        g.MapGet("/google/done", async (HttpContext ctx, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var result = await ctx.AuthenticateAsync(IdentityConstants.ExternalScheme);
            await ctx.SignOutAsync(IdentityConstants.ExternalScheme);
            var key = result.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!result.Succeeded || key is null) return Back("failed");
            var email = result.Principal!.FindFirstValue(ClaimTypes.Email) ?? "Google account";

            if (result.Properties?.Items.TryGetValue(LinkUser, out var userId) == true && userId is not null)
            {
                var user = await users.FindByIdAsync(userId);
                if (user is null) return Back("failed");
                var owner = await users.FindByLoginAsync(Provider, key);
                if (owner is not null) return Back(owner.Id == user.Id ? "linked" : "taken");
                // One Google account per user: linking another replaces it
                foreach (var old in (await users.GetLoginsAsync(user)).Where(l => l.LoginProvider == Provider))
                    await users.RemoveLoginAsync(user, Provider, old.ProviderKey);
                var added = await users.AddLoginAsync(user, new UserLoginInfo(Provider, key, email));
                if (!added.Succeeded) return Back("failed");
                return Back("linked");
            }

            var signedIn = await signIn.ExternalLoginSignInAsync(Provider, key, isPersistent: true, bypassTwoFactor: true);
            if (signedIn.Succeeded) return Results.Redirect("/");
            return Back(signedIn.IsLockedOut ? "locked" : "unknown");
        }).RequireRateLimiting("auth");

        g.MapPost("/google/unlink", async (ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            foreach (var old in (await users.GetLoginsAsync(user)).Where(l => l.LoginProvider == Provider))
                await users.RemoveLoginAsync(user, Provider, old.ProviderKey);
            // Removing a login changes the security stamp, which would sign this session out at its next check
            await signIn.RefreshSignInAsync(user);
            return Results.Ok();
        }).RequireAuthorization();

        static IResult Back(string outcome) => Results.Redirect($"/?google={outcome}");
    }

    /// <summary>The running version, e.g. 0.1.0 (from &lt;Version&gt; in the project file).</summary>
    public static readonly string Version =
        typeof(AuthEndpoints).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "dev";

    /// <summary>Invite tokens are stored only as their SHA-256, so the database alone can't be used to join.</summary>
    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));

    public static IResult Errors(IdentityResult r) =>
        Results.Json(new { error = string.Join(" ", r.Errors.Select(e => e.Description)) }, statusCode: 400);
}
