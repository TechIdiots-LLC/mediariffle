using Microsoft.AspNetCore.Antiforgery;

namespace MediaRiffle.Server.Security;

public static class Roles
{
    public const string Admin = "Admin";
}

public static class SecurityMiddleware
{
    /// <summary>
    /// Headers for every response: no framing, no sniffing, no referrer to other sites, and a Content Security
    /// Policy allowing only this site's own scripts and styles, plus Google's Cast sender for the Cast button.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "same-origin";
        h["X-Frame-Options"] = "DENY";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        if (ctx.Request.Path.Equals("/game.html", StringComparison.OrdinalIgnoreCase))
        {
            // The game page runs the emulator: WebAssembly, the core's script handed over as a blob (Emscripten's
            // code, which evaluates strings: 'unsafe-eval'), workers, and the style attributes EmulatorJS sets on its
            // controls; still only this site's files (a core missing here isn't fetched from EmulatorJS's CDN), and
            // nothing on the page comes from the library but the game's name, set as text. Cross-origin isolated, for
            // the cores that need threads (SharedArrayBuffer), which the Cast button's Google script would break, so
            // it has none.
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Cross-Origin-Embedder-Policy"] = "require-corp";
            h["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self' blob: 'unsafe-eval' 'wasm-unsafe-eval'; " +
                "style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data: blob:; " +
                "media-src 'self' blob:; " +
                "connect-src 'self' blob: data:; " +
                "worker-src 'self' blob:; " +
                "font-src 'self' data:; " +
                "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
            await next();
            return;
        }
        h["Cross-Origin-Opener-Policy"] = "same-origin-allow-popups";
        h["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self' https://www.gstatic.com; " +
            "style-src 'self'; " +
            "img-src 'self' data: blob:; " +
            "media-src 'self' blob:; " +
            "connect-src 'self'; " +
            "worker-src 'self' blob:; " +
            "frame-src https://www.gstatic.com; " +
            "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
        await next();
    });

    /// <summary>
    /// Every request that changes something under /api must carry the antiforgery token (X-CSRF-TOKEN), on top
    /// of the SameSite=Strict cookies, so another site can't act as a signed-in user. /stream is read-only.
    /// Not a linked device's: its token is sent by its app, never by a browser by itself, and signs it in without the
    /// cookie (DeviceTokens); nor endpoints marked DisableAntiforgery (a device asking for its code).
    /// </summary>
    public static IApplicationBuilder UseApiAntiforgery(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api") &&
            !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method) &&
            DeviceTokens.BearerToken(ctx.Request) is null &&
            ctx.GetEndpoint()?.Metadata.GetMetadata<IAntiforgeryMetadata>()?.RequiresValidation != false)
        {
            var antiforgery = ctx.RequestServices.GetRequiredService<IAntiforgery>();
            try
            {
                await antiforgery.ValidateRequestAsync(ctx);
            }
            catch (AntiforgeryValidationException)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { error = "Missing or invalid antiforgery token; reload the page." });
                return;
            }
        }
        await next();
    });
}
