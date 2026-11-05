using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using MediaRiffle.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Security;

/// <summary>
/// Linked devices' tokens (see <see cref="LinkedDevice"/>): a TV or console app sends <c>Authorization: Bearer …</c>
/// instead of a sign-in cookie. A request carrying one is signed in by the token alone (the cookie scheme forwards it
/// here, see <see cref="SelectScheme"/>), and can only reach endpoints opened to devices with
/// <see cref="AllowDevices{TBuilder}"/>: browsing, playing and saving progress, never Admin or the account's settings.
/// A device is never an admin, whatever its user is.
/// </summary>
public static class DeviceTokens
{
    public const string Scheme = "Device";
    /// <summary>The <see cref="LinkedDevice"/> id, on a principal signed in by a device's token.</summary>
    public const string DeviceClaim = "mediariffle:device";
    /// <summary>A device unused this long is unlinked (its token stops working), as an old sign-in runs out.</summary>
    public static readonly TimeSpan UnusedLifetime = TimeSpan.FromDays(180);
    /// <summary>How stale Last used may get before a request updates it (not a database write on every request).</summary>
    internal static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(10);

    // A token sent without HTTPS is only accepted from the local network, where a console reaches MediaRiffle directly
    // (no TLS of its own): loopback, private and link-local addresses
    private static readonly IPNetwork[] LocalNetworks =
    [
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("::1/128"), IPNetwork.Parse("fc00::/7"), IPNetwork.Parse("fe80::/10"),
    ];

    /// <summary>The token in <c>Authorization: Bearer …</c>, or null.</summary>
    public static string? BearerToken(HttpRequest request)
    {
        var value = request.Headers.Authorization.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = value[7..].Trim();
        return token.Length > 0 ? token : null;
    }

    /// <summary>The sign-in cookie's ForwardDefaultSelector: a request with a device token is this scheme's.</summary>
    public static string? SelectScheme(HttpContext ctx) => BearerToken(ctx.Request) is null ? null : Scheme;

    public static bool IsLocal(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return LocalNetworks.Any(n => n.Contains(address));
    }

    /// <summary>
    /// Whether a device may send or get a token over this connection: HTTPS (directly or through a trusted proxy that
    /// says so), or from the local network.
    /// </summary>
    public static bool TransportAllowed(HttpContext ctx) => ctx.Request.IsHttps || IsLocal(ctx.Connection.RemoteIpAddress);

    /// <summary>A new device token: 256 random bits.</summary>
    public static string NewToken() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>The linked device a principal is signed in as, or null for a browser.</summary>
    public static int? DeviceId(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(DeviceClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>Opens endpoints to linked devices.</summary>
    public static TBuilder AllowDevices<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(DeviceAccess.Instance);

    /// <summary>Whether this request may go on: one with a device token only to endpoints opened to devices.</summary>
    public static bool Allowed(HttpContext ctx) =>
        BearerToken(ctx.Request) is null || ctx.GetEndpoint() is not { } endpoint || endpoint.Metadata.GetMetadata<DeviceAccess>() is not null;

    /// <summary>Refuses a request with a device token anywhere not opened to devices, before it's authorized.</summary>
    public static IApplicationBuilder UseDeviceGate(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (!Allowed(ctx))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "A linked device can't use this." });
            return;
        }
        await next();
    });
}

/// <summary>Marks an endpoint linked devices may use (<see cref="DeviceTokens.AllowDevices{TBuilder}"/>).</summary>
public sealed class DeviceAccess
{
    public static readonly DeviceAccess Instance = new();
}

/// <summary>
/// Signs in a request by its device token: the device must exist, have been used within
/// <see cref="DeviceTokens.UnusedLifetime"/>, and its user must exist and not be locked out.
/// </summary>
public sealed class DeviceTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    AppDbContext db, UserManager<AppUser> users) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (DeviceTokens.BearerToken(Request) is not { } token) return AuthenticateResult.NoResult();
        if (!DeviceTokens.TransportAllowed(Context))
            return AuthenticateResult.Fail("A device token needs HTTPS from outside the local network.");

        var hash = Api.AuthEndpoints.HashToken(token);
        var device = await db.LinkedDevices.FirstOrDefaultAsync(d => d.TokenHash == hash, Context.RequestAborted);
        if (device is null) return AuthenticateResult.Fail("Unknown device token.");
        var now = DateTime.UtcNow;
        if (now - device.LastUsedUtc > DeviceTokens.UnusedLifetime)
        {
            db.LinkedDevices.Remove(device);
            await db.SaveChangesAsync(Context.RequestAborted);
            return AuthenticateResult.Fail("This device was unused too long; link it again.");
        }
        var user = await users.FindByIdAsync(device.UserId);
        if (user is null || await users.IsLockedOutAsync(user)) return AuthenticateResult.Fail("The device's account can't sign in.");

        var address = Context.Connection.RemoteIpAddress?.ToString();
        if (now - device.LastUsedUtc > DeviceTokens.TouchEvery || device.LastAddress != address)
        {
            device.LastUsedUtc = now;
            device.LastAddress = address;
            await db.SaveChangesAsync(Context.RequestAborted);
        }

        // The user's id and name (what the endpoints read), and the device; no roles, so never Admin
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.UserName ?? string.Empty),
            new Claim(DeviceTokens.DeviceClaim, device.Id.ToString(CultureInfo.InvariantCulture)),
        ], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
