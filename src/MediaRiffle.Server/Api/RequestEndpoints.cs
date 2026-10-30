using System.Security.Claims;
using MediaRiffle.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

/// <summary>
/// Requests for things to add to the library. Anyone signed in asks and sees their own; an admin sees everyone's and
/// ticks them off once they're in (or can't be had). A request is removed by whoever asked, or an admin.
/// </summary>
public static class RequestEndpoints
{
    private const int MaxOpenPerUser = 100;

    public sealed record RequestBody(string? Title, int? Year, RequestKind Kind, string? Notes, string? Link);
    public sealed record DoneBody(bool Done);

    public static void MapRequestEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/requests").RequireAuthorization();

        // Open ones first (oldest first, the order to work through), then done ones, newest first
        g.MapGet("", async (ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var userId = users.GetUserId(me)!;
            var admin = me.IsInRole(Security.Roles.Admin);
            var rows = await db.Requests.Where(r => admin || r.UserId == userId)
                .Select(r => new
                {
                    r.Id, r.Title, r.Year, r.Kind, r.Notes, r.Link, r.CreatedUtc, r.DoneUtc,
                    Mine = r.UserId == userId,
                    By = db.Users.Where(u => u.Id == r.UserId).Select(u => u.UserName).FirstOrDefault(),
                }).ToListAsync();
            return rows.OrderBy(r => r.DoneUtc != null).ThenBy(r => r.DoneUtc == null ? r.CreatedUtc : DateTime.MaxValue)
                .ThenByDescending(r => r.DoneUtc);
        });

        g.MapPost("", async (RequestBody req, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var userId = users.GetUserId(me)!;
            if (Validate(req) is { } problem) return Results.BadRequest(new { error = problem });
            if (await db.Requests.CountAsync(r => r.UserId == userId && r.DoneUtc == null) >= MaxOpenPerUser)
                return Results.BadRequest(new { error = $"That's {MaxOpenPerUser} requests waiting already; wait for some to be done." });
            var request = new MediaRequest
            {
                UserId = userId, Title = Clean(req.Title)!, Year = req.Year, Kind = req.Kind,
                Notes = Clean(req.Notes), Link = Clean(req.Link),
            };
            db.Requests.Add(request);
            await db.SaveChangesAsync();
            return Results.Ok(new { request.Id });
        });

        g.MapDelete("/{id:int}", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var userId = users.GetUserId(me)!;
            var admin = me.IsInRole(Security.Roles.Admin);
            var removed = await db.Requests.Where(r => r.Id == id && (admin || r.UserId == userId)).ExecuteDeleteAsync();
            return removed == 0 ? Results.NotFound() : Results.Ok();
        });

        // Ticking off is the admin's
        g.MapPut("/{id:int}/done", async (int id, DoneBody body, AppDbContext db) =>
        {
            var changed = await db.Requests.Where(r => r.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.DoneUtc, body.Done ? DateTime.UtcNow : (DateTime?)null));
            return changed == 0 ? Results.NotFound() : Results.Ok(new { done = body.Done });
        }).RequireAuthorization("Admin");
    }

    /// <summary>What's wrong with a request, in words for the person asking; null when it's fine.</summary>
    public static string? Validate(RequestBody req)
    {
        var title = Clean(req.Title);
        if (title is null) return "Give the title.";
        if (title.Length > MediaRequest.MaxTitle) return $"The title can be at most {MediaRequest.MaxTitle} characters.";
        if (req.Year is { } y && (y < 1850 || y > DateTime.UtcNow.Year + 5)) return "That year doesn't look right.";
        if (!Enum.IsDefined(req.Kind)) return "Say what kind of thing it is.";
        if (Clean(req.Notes) is { Length: > MediaRequest.MaxNotes }) return $"The notes can be at most {MediaRequest.MaxNotes} characters.";
        if (Clean(req.Link) is { } link)
        {
            if (link.Length > MediaRequest.MaxLink) return "That link is too long.";
            // Shown as a link on the admin's page: only web addresses, never javascript: and the like
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return "The link has to be a web address starting with http:// or https://.";
        }
        return null;
    }

    /// <summary>Trimmed, control characters dropped (new lines kept in notes); null when nothing is left.</summary>
    private static string? Clean(string? raw)
    {
        var text = new string((raw ?? "").Where(c => !char.IsControl(c) || c == '\n').ToArray()).Trim();
        return text.Length == 0 ? null : text;
    }
}
