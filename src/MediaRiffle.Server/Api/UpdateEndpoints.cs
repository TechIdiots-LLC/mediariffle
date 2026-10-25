using MediaRiffle.Server.Options;
using MediaRiffle.Server.Security;
using MediaRiffle.Server.Updates;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Api;

public sealed record UpdateRequest(string Version);

/// <summary>Admin → Updates: this version, newer releases and their notes, and updating to one (see UpdateService).</summary>
public static class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin/updates").RequireAuthorization("Admin").AddEndpointFilter(Mfa.RequireForAdmin);

        g.MapGet("", (UpdateService updates, IOptions<MediaRiffleOptions> options) => Results.Ok(Report(updates, options.Value)));

        g.MapPost("/check", async (UpdateService updates, IOptions<MediaRiffleOptions> options, CancellationToken ct) =>
        {
            await updates.CheckAsync(ct);
            return Results.Ok(Report(updates, options.Value));
        });

        g.MapPost("/install", (UpdateRequest req, UpdateService updates) =>
            updates.Request(req.Version ?? "") is { } error ? Results.BadRequest(new { error }) : Results.Ok());
    }

    private static object Report(UpdateService updates, MediaRiffleOptions options) => new
    {
        current = AuthEndpoints.Version,
        source = options.Updates.Source.ToString(),
        page = Releases.Page(options.Updates.Source),
        whyNot = updates.WhyNot(),
        checkedUtc = updates.CheckedUtc,
        error = updates.Error,
        pending = UpdateService.Pending,
        status = UpdateService.Status(),
        releases = updates.Newer.Select(r =>
        {
            var (file, ready) = UpdateService.Readiness(r);
            return new { r.Version, r.Name, r.Notes, r.Url, r.PublishedUtc, r.Prerelease, file, ready };
        }),
    };
}
