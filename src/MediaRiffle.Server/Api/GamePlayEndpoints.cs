using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

/// <summary>
/// What a user keeps from playing games, on the server so it follows them from device to device: their controller
/// mapping for each system.
/// </summary>
public static class GamePlayEndpoints
{
    public sealed record ControlsRequest(string? Layout, JsonElement? Mapping);

    static readonly string[] Layouts = ["letter", "position", "custom"];

    public static void MapGamePlayEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/games").RequireAuthorization();

        // A system's controller mapping: "letter" (EmulatorJS's own), "position", or "custom" with the emulator's
        // controls as they were set in its Controls menu
        g.MapPut("/controls/{system}", async (string system, ControlsRequest req, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            if (GameSystems.Get(system) is null) return Results.NotFound();
            var layout = req.Layout?.Trim().ToLowerInvariant();
            if (layout is null || !Layouts.Contains(layout)) return Results.BadRequest(new { error = "The layout is letter, position or custom." });
            string? mapping = null;
            if (layout == "custom")
            {
                if (req.Mapping is not { ValueKind: JsonValueKind.Object } m) return Results.BadRequest(new { error = "A custom layout needs its mapping." });
                mapping = m.GetRawText();
                if (mapping.Length > 64 * 1024) return Results.BadRequest(new { error = "That mapping is too large." });
            }
            var userId = users.GetUserId(me)!;
            var row = await db.GameControls.FindAsync(userId, system);
            if (row is null) db.GameControls.Add(row = new GameControls { UserId = userId, System = system });
            row.Layout = layout;
            row.Mapping = mapping;
            row.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(Controls(row));
        });

        // Back to EmulatorJS's own mapping
        g.MapDelete("/controls/{system}", async (string system, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db) =>
        {
            var userId = users.GetUserId(me)!;
            await db.GameControls.Where(c => c.UserId == userId && c.System == system).ExecuteDeleteAsync();
            return Results.Ok();
        });
    }

    /// <summary>A user's mapping for a system, as the game page takes it; null when they haven't set one.</summary>
    public static async Task<object?> ControlsFor(AppDbContext db, string userId, string system) =>
        Controls(await db.GameControls.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == userId && c.System == system));

    static object? Controls(GameControls? row) => row is null ? null : new
    {
        layout = row.Layout,
        mapping = row.Mapping is null ? null : JsonNode.Parse(row.Mapping),
    };
}
