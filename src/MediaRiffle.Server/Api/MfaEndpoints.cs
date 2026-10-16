using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Options;
using MediaRiffle.Server.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using QRCoder;

namespace MediaRiffle.Server.Api;

public sealed record PasswordConfirmRequest(string Password);
public sealed record CodeRequest(string Code);
public sealed record PasskeyRequest(string Credential, string? Name);

/// <summary>
/// Two-step verification: passkeys (Face ID, Touch ID, Windows Hello, a phone or a security key) and an authenticator
/// app's 6-digit codes, either or both, with one-time recovery codes. Admin asks for one (see <see cref="Mfa"/>).
/// Setting up the first one wants the password confirmed just before; changing them afterwards wants a recent check.
/// </summary>
public static class MfaEndpoints
{
    private const string Issuer = "MediaRiffle";
    private const int RecoveryCodeCount = 10;

    public static void MapMfaEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/auth/mfa").RequireAuthorization();

        // What's set up, and whether this session has verified recently (Admin opens) or may set one up
        g.MapGet("", async (ClaimsPrincipal me, UserManager<AppUser> users, IOptions<MediaRiffleOptions> options) =>
        {
            var user = await users.GetUserAsync(me);
            if (user is null) return Results.Unauthorized();
            var passkeys = await users.GetPasskeysAsync(user);
            var o = options.Value.AdminMfa;
            return Results.Ok(new
            {
                required = o.Required,
                authenticator = user.AuthenticatorEnabled,
                passkeys = passkeys.OrderBy(p => p.CreatedAt).Select(p => new
                {
                    id = WebEncoders.Base64UrlEncode(p.CredentialId), name = p.Name ?? "Passkey", created = p.CreatedAt,
                    synced = p.IsBackedUp,
                }),
                recoveryCodesLeft = await users.CountRecoveryCodesAsync(user),
                setUp = user.AuthenticatorEnabled || passkeys.Count > 0,
                verified = Mfa.IsFresh(me, TimeSpan.FromHours(Math.Max(1, o.Hours))),
                passwordConfirmed = Mfa.PasswordConfirmed(me),
                // Recent enough to add or remove a method (see Mfa.ChangeLifetime)
                verifiedForChanges = Mfa.IsFresh(me, Mfa.ChangeLifetime),
            });
        });

        // Before setting up the first one: the password again, so a session left open can't add its own
        g.MapPost("/password", async (PasswordConfirmRequest req, HttpContext ctx, ClaimsPrincipal me, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = await users.GetUserAsync(me);
            if (user is null) return Results.Unauthorized();
            if (await users.IsLockedOutAsync(user)) return LockedOut();
            if (!await users.CheckPasswordAsync(user, req.Password ?? ""))
            {
                await users.AccessFailedAsync(user);
                return Results.Json(new { error = "That isn't your password." }, statusCode: 400);
            }
            await users.ResetAccessFailedCountAsync(user);
            await Mfa.SignInAgainAsync(ctx, signIn, user, Mfa.PasswordClaim);
            return Results.Ok();
        }).RequireRateLimiting("auth");

        // ── Authenticator app ──

        // A new secret for the app, as a QR code and as text; it counts once a code from it is entered (below)
        g.MapPost("/authenticator/start", async (HttpContext ctx, ClaimsPrincipal me, UserManager<AppUser> users, SignInManager<AppUser> signIn, IOptions<MediaRiffleOptions> options) =>
        {
            var (user, refused) = await MayChangeAsync(me, users, options);
            if (refused is not null) return refused;
            if (user!.AuthenticatorEnabled) return Results.Conflict(new { error = "An authenticator app is already set up; turn it off first to set up another." });
            await users.ResetAuthenticatorKeyAsync(user);
            await Mfa.SignInAgainAsync(ctx, signIn, user);
            var key = (await users.GetAuthenticatorKeyAsync(user))!;
            var uri = AuthenticatorUri(user.UserName!, key);
            return Results.Ok(new { key = FormatKey(key), uri, qr = QrSvgDataUri(uri) });
        });

        g.MapPost("/authenticator/enable", async (CodeRequest req, HttpContext ctx, ClaimsPrincipal me, UserManager<AppUser> users, SignInManager<AppUser> signIn, IOptions<MediaRiffleOptions> options) =>
        {
            var (user, refused) = await MayChangeAsync(me, users, options);
            if (refused is not null) return refused;
            if (await users.IsLockedOutAsync(user!)) return LockedOut();
            if (!await users.VerifyTwoFactorTokenAsync(user!, users.Options.Tokens.AuthenticatorTokenProvider, Digits(req.Code)))
            {
                await users.AccessFailedAsync(user!);
                return Results.Json(new { error = "That code isn't right. Check the phone's clock, and use the newest code." }, statusCode: 400);
            }
            var first = !await Mfa.IsSetUpAsync(users, user!);
            user!.AuthenticatorEnabled = true;
            await users.UpdateAsync(user);
            var codes = first ? await users.GenerateNewTwoFactorRecoveryCodesAsync(user!, RecoveryCodeCount) : null;
            await users.ResetAccessFailedCountAsync(user!);
            await Mfa.SignInAgainAsync(ctx, signIn, user!, Mfa.VerifiedClaim);
            return Results.Ok(new { recoveryCodes = codes });
        }).RequireRateLimiting("auth");

        g.MapDelete("/authenticator", async (HttpContext ctx, ClaimsPrincipal me, UserManager<AppUser> users, SignInManager<AppUser> signIn, IOptions<MediaRiffleOptions> options) =>
        {
            var (user, refused) = await MayChangeAsync(me, users, options, removing: true);
            if (refused is not null) return refused;
            user!.AuthenticatorEnabled = false;
            await users.UpdateAsync(user);
            // The old secret stops working, wherever it was copied to
            await users.ResetAuthenticatorKeyAsync(user!);
            await ForgetRecoveryCodesIfNothingLeftAsync(users, user!);
            await Mfa.SignInAgainAsync(ctx, signIn, user!);
            return Results.Ok();
        });

        // ── Passkeys ──

        g.MapPost("/passkeys/options", async (ClaimsPrincipal me, UserManager<AppUser> users, SignInManager<AppUser> signIn, IOptions<MediaRiffleOptions> options) =>
        {
            var (user, refused) = await MayChangeAsync(me, users, options);
            if (refused is not null) return refused;
            var json = await signIn.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
            {
                Id = user!.Id, Name = user.UserName!, DisplayName = user.UserName!,
            });
            return Results.Content(json, "application/json");
        });

        g.MapPost("/passkeys", async (PasskeyRequest req, HttpContext ctx, ClaimsPrincipal me, UserManager<AppUser> users, SignInManager<AppUser> signIn, IOptions<MediaRiffleOptions> options) =>
        {
            var (user, refused) = await MayChangeAsync(me, users, options);
            if (refused is not null) return refused;
            var result = await signIn.PerformPasskeyAttestationAsync(req.Credential ?? "");
            if (!result.Succeeded)
                return Results.Json(new { error = "The passkey wasn't accepted: " + result.Failure.Message }, statusCode: 400);
            if (result.UserEntity.Id != user!.Id) return Results.Json(new { error = "That passkey is for someone else." }, statusCode: 400);
            var first = !await Mfa.IsSetUpAsync(users, user);
            var passkey = result.Passkey;
            passkey.Name = Name(req.Name);
            var added = await users.AddOrUpdatePasskeyAsync(user, passkey);
            if (!added.Succeeded) return AuthEndpoints.Errors(added);
            var codes = first ? await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount) : null;
            await Mfa.SignInAgainAsync(ctx, signIn, user, Mfa.VerifiedClaim);
            return Results.Ok(new { recoveryCodes = codes });
        });

        g.MapDelete("/passkeys/{id}", async (string id, HttpContext ctx, ClaimsPrincipal me, UserManager<AppUser> users, SignInManager<AppUser> signIn, IOptions<MediaRiffleOptions> options) =>
        {
            var (user, refused) = await MayChangeAsync(me, users, options, removing: true);
            if (refused is not null) return refused;
            byte[] credentialId;
            try { credentialId = WebEncoders.Base64UrlDecode(id); } catch (FormatException) { return Results.NotFound(); }
            var removed = await users.RemovePasskeyAsync(user!, credentialId);
            if (!removed.Succeeded) return AuthEndpoints.Errors(removed);
            await ForgetRecoveryCodesIfNothingLeftAsync(users, user!);
            await Mfa.SignInAgainAsync(ctx, signIn, user!);
            return Results.Ok();
        });

        // ── Checking it's you ──

        g.MapPost("/verify/passkey/options", async (ClaimsPrincipal me, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = await users.GetUserAsync(me);
            if (user is null) return Results.Unauthorized();
            return Results.Content(await signIn.MakePasskeyRequestOptionsAsync(user), "application/json");
        });

        g.MapPost("/verify/passkey", async (PasskeyRequest req, HttpContext ctx, ClaimsPrincipal me, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = await users.GetUserAsync(me);
            if (user is null) return Results.Unauthorized();
            if (await users.IsLockedOutAsync(user)) return LockedOut();
            var result = await signIn.PerformPasskeyAssertionAsync(req.Credential ?? "");
            if (!result.Succeeded || result.User.Id != user.Id)
            {
                await users.AccessFailedAsync(user);
                return Results.Json(new { error = result.Succeeded ? "That passkey is for someone else." : "The passkey wasn't accepted: " + result.Failure.Message }, statusCode: 400);
            }
            // Its use count goes up with each use (a cloned key would fall behind)
            await users.AddOrUpdatePasskeyAsync(user, result.Passkey);
            await users.ResetAccessFailedCountAsync(user);
            await Mfa.SignInAgainAsync(ctx, signIn, user, Mfa.VerifiedClaim);
            return Results.Ok();
        }).RequireRateLimiting("auth");

        // An authenticator app's code, or one of the recovery codes (each works once)
        g.MapPost("/verify/code", async (CodeRequest req, HttpContext ctx, ClaimsPrincipal me, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = await users.GetUserAsync(me);
            if (user is null) return Results.Unauthorized();
            if (!await Mfa.IsSetUpAsync(users, user)) return Results.Json(new { error = "Set up a passkey or an authenticator app first." }, statusCode: 400);
            if (await users.IsLockedOutAsync(user)) return LockedOut();
            var code = (req.Code ?? "").Trim();
            int? recoveryLeft = null;
            var ok = user.AuthenticatorEnabled && Digits(code).Length == 6 &&
                     await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, Digits(code));
            if (!ok && code.Length >= 8 && (await users.RedeemTwoFactorRecoveryCodeAsync(user, code.Replace(" ", ""))).Succeeded)
            {
                ok = true;
                recoveryLeft = await users.CountRecoveryCodesAsync(user);
            }
            if (!ok)
            {
                await users.AccessFailedAsync(user);
                return Results.Json(new { error = "That code isn't right." }, statusCode: 400);
            }
            await users.ResetAccessFailedCountAsync(user);
            await Mfa.SignInAgainAsync(ctx, signIn, user, Mfa.VerifiedClaim);
            return Results.Ok(new { recoveryCodesLeft = recoveryLeft });
        }).RequireRateLimiting("auth");

        // New recovery codes (the old ones stop working)
        g.MapPost("/recovery-codes", async (ClaimsPrincipal me, UserManager<AppUser> users, IOptions<MediaRiffleOptions> options) =>
        {
            var (user, refused) = await MayChangeAsync(me, users, options, removing: true);
            if (refused is not null) return refused;
            if (!await Mfa.IsSetUpAsync(users, user!)) return Results.BadRequest(new { error = "Set up a passkey or an authenticator app first." });
            return Results.Ok(new { recoveryCodes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user!, RecoveryCodeCount) });
        });
    }

    /// <summary>
    /// The user, if they may change how they verify: with something set up, after a check with it in the last
    /// <see cref="Mfa.ChangeLifetime"/> (not just the one that keeps Admin open); with nothing yet, after
    /// confirming their password (or a check) — else a 403 saying which (<c>mfa</c> = "verify" or "password").
    /// <paramref name="removing"/>: taking something away (or new recovery codes) always wants a recent check.
    /// </summary>
    private static async Task<(AppUser? User, IResult? Refused)> MayChangeAsync(ClaimsPrincipal me, UserManager<AppUser> users,
        IOptions<MediaRiffleOptions> options, bool removing = false)
    {
        var user = await users.GetUserAsync(me);
        if (user is null) return (null, Results.Unauthorized());
        // Adding or removing a method wants a check with one already set up, just now, not the one that keeps Admin open
        if (Mfa.IsFresh(me, Mfa.ChangeLifetime)) return (user, null);
        if (!removing && !await Mfa.IsSetUpAsync(users, user) && Mfa.PasswordConfirmed(me)) return (user, null);
        var setUp = await Mfa.IsSetUpAsync(users, user);
        return (null, Results.Json(new
        {
            error = setUp ? "Confirm it's you first." : "Confirm your password first.",
            mfa = setUp ? "verify" : "password",
        }, statusCode: StatusCodes.Status403Forbidden));
    }

    // With no passkey and no authenticator left, recovery codes have nothing to stand in for
    private static async Task ForgetRecoveryCodesIfNothingLeftAsync(UserManager<AppUser> users, AppUser user)
    {
        if (!await Mfa.IsSetUpAsync(users, user)) await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
    }

    private static IResult LockedOut() =>
        Results.Json(new { error = "Too many wrong tries; try again in 15 minutes." }, statusCode: 423);

    private static string Digits(string? code) => new((code ?? "").Where(char.IsAsciiDigit).ToArray());

    private static string Name(string? name)
    {
        var n = (name ?? "").Trim();
        return n.Length == 0 ? "Passkey" : n.Length > 60 ? n[..60] : n;
    }

    /// <summary>The otpauth:// link an authenticator app reads (from the QR code, or tapped on the phone itself).</summary>
    public static string AuthenticatorUri(string userName, string key) =>
        $"otpauth://totp/{UrlEncoder.Default.Encode(Issuer)}:{UrlEncoder.Default.Encode(userName)}?secret={key}&issuer={UrlEncoder.Default.Encode(Issuer)}&digits=6";

    /// <summary>The secret in groups of four, lower case, for typing in by hand.</summary>
    public static string FormatKey(string key)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4) sb.Append(sb.Length > 0 ? " " : "").Append(key.AsSpan(i, Math.Min(4, key.Length - i)));
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>A QR code of the link, as an SVG data: URI (the page's Content Security Policy allows data: images).</summary>
    public static string QrSvgDataUri(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(6);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }
}
