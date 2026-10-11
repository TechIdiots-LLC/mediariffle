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
        h["Cross-Origin-Opener-Policy"] = "same-origin-allow-popups";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
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
    /// </summary>
    public static IApplicationBuilder UseApiAntiforgery(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api") &&
            !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method))
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
