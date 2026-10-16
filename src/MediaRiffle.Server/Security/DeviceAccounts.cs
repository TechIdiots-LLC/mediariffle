using System.Text.Json;
using MediaRiffle.Server.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace MediaRiffle.Server.Security;

/// <summary>One account signed in on this device: who, their security stamp then, and when it was last used.</summary>
public sealed record DeviceAccount(string UserId, string Stamp, long LastUsed);

/// <summary>
/// The accounts signed in on this device, to switch between without a password (as with Google or Netflix): a cookie
/// holding each one's user id and security stamp, encrypted with the app's keys. An account drops off after
/// <see cref="Lifetime"/> unused (as a "keep me signed in" sign-in does), or once its stamp changes elsewhere (a new
/// password, removed sign-ins). Only sign-ins kept ("keep me signed in") are remembered.
/// </summary>
public static class DeviceAccounts
{
    public const string CookieName = "mediariffle.accounts";
    public const int Max = 8;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);
    private const string Purpose = "MediaRiffle.DeviceAccounts";

    private static IDataProtector Protector(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    /// <summary>The accounts on this device, most recently used first (expired ones left out).</summary>
    public static List<DeviceAccount> Read(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrEmpty(value)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<DeviceAccount>>(Protector(ctx).Unprotect(value)) ?? [];
            var oldest = DateTimeOffset.UtcNow.Add(-Lifetime).ToUnixTimeSeconds();
            return list.Where(a => a.LastUsed >= oldest).OrderByDescending(a => a.LastUsed).Take(Max).ToList();
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        {
            return [];   // altered, or from other keys
        }
    }

    public static void Write(HttpContext ctx, List<DeviceAccount> list)
    {
        if (list.Count == 0) { ctx.Response.Cookies.Delete(CookieName, Options(ctx)); return; }
        var value = Protector(ctx).Protect(JsonSerializer.Serialize(list.Take(Max).ToList()));
        ctx.Response.Cookies.Append(CookieName, value, Options(ctx));
    }

    // Lax, not Strict like the sign-in cookie: it must come along on the way back from Google (a top-level GET from
    // another site), or signing in with Google would start the list afresh. Switching is a POST with the antiforgery
    // token, so another site can't do it.
    private static CookieOptions Options(HttpContext ctx) => new()
    {
        HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps, Path = "/",
        Expires = DateTimeOffset.UtcNow.Add(Lifetime), IsEssential = true,
    };

    /// <summary>Puts the user first on this device's list, with their stamp as it is now.</summary>
    public static async Task RememberAsync(HttpContext ctx, UserManager<AppUser> users, AppUser user) =>
        Put(ctx, user.Id, await users.GetSecurityStampAsync(user) ?? "");

    private static void Put(HttpContext ctx, string userId, string stamp)
    {
        var list = Read(ctx).Where(a => a.UserId != userId).ToList();
        list.Insert(0, new DeviceAccount(userId, stamp, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        Write(ctx, list);
    }

    /// <summary>
    /// The sign-in cookie's OnSigningIn: every time an account's cookie is issued (signing in, Google, switching, and
    /// signing in again after the stamp changes, as two-step verification changes do) this device's list follows: a kept
    /// sign-in is remembered with the stamp it carries, one that isn't kept leaves the list.
    /// </summary>
    public static Task OnSigningIn(Microsoft.AspNetCore.Authentication.Cookies.CookieSigningInContext ctx)
    {
        var principal = ctx.Principal;
        var userId = principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return Task.CompletedTask;
        var stampType = ctx.HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>()
            .Value.ClaimsIdentity.SecurityStampClaimType;
        if (ctx.Properties.IsPersistent && principal!.FindFirst(stampType)?.Value is { } stamp) Put(ctx.HttpContext, userId, stamp);
        else Forget(ctx.HttpContext, userId);
        return Task.CompletedTask;
    }

    public static void Forget(HttpContext ctx, string userId)
    {
        var list = Read(ctx);
        if (list.RemoveAll(a => a.UserId == userId) > 0) Write(ctx, list);
    }

    public static void ForgetAll(HttpContext ctx) => Write(ctx, []);

    /// <summary>
    /// The user an entry is for, while it's still good: the account exists, isn't locked out, and its stamp is the one
    /// remembered (a changed password or "sign out everywhere" changes it). Null otherwise.
    /// </summary>
    public static async Task<AppUser?> ValidAsync(UserManager<AppUser> users, DeviceAccount a)
    {
        var user = await users.FindByIdAsync(a.UserId);
        if (user is null || await users.IsLockedOutAsync(user)) return null;
        return await users.GetSecurityStampAsync(user) == a.Stamp ? user : null;
    }
}
