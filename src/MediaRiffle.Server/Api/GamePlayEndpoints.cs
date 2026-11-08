using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

/// <summary>
/// What a user keeps from playing games, on the server so it follows them from device to device: their controller
/// mapping for each system, and each game's in-game save (the game page keeps it and the one in its browser alike).
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

        // The user's in-game save for a game (by its system and file name, so it stays when the game is moved)
        g.MapGet("/{id:int}/save", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db, LibraryCatalog catalog,
            Games.GameSaves saves, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (await FindGame(id, db, catalog) is not { } game) return Results.NotFound();
            var userId = users.GetUserId(me)!;
            var play = await FindPlay(db, userId, game);
            var path = play?.SaveHash is null ? null : saves.SavePath(userId, play.Id);
            return path is not null && File.Exists(path) ? Results.File(path, "application/octet-stream") : Results.NotFound();
        });

        // A new save, as the emulator wrote it
        g.MapPut("/{id:int}/save", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db, LibraryCatalog catalog,
            Games.GameSaves saves, HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = Games.GameSaves.MaxSave;
            if (await FindGame(id, db, catalog) is not { } game) return Results.NotFound();
            byte[] data;
            try
            {
                using var body = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(body, ct);
                data = body.ToArray();
            }
            catch (BadHttpRequestException) { return Results.BadRequest(new { error = "That save is larger than a game save can be." }); }
            if (data.Length == 0) return Results.BadRequest(new { error = "The save is empty." });

            var userId = users.GetUserId(me)!;
            var play = await FindPlay(db, userId, game);
            if (await saves.OverLimitAsync(db, userId, data.Length - (play?.SaveSize ?? 0)) is { } error)
                return Results.BadRequest(new { error });
            var now = DateTime.UtcNow;
            if (play is null)
            {
                db.GamePlays.Add(play = new GamePlay { UserId = userId, System = game.System, FileName = FileNameOf(game), LastPlayedUtc = now });
                await db.SaveChangesAsync(ct);
            }
            await Games.GameSaves.WriteAsync(saves.SavePath(userId, play.Id), data, ct);
            play.SaveSize = data.Length;
            play.SaveUtc = now;
            play.SaveHash = Games.GameSaves.Hash(data);
            play.LastPlayedUtc = now;
            await db.SaveChangesAsync(ct);
            return Results.Ok(SaveInfo(play));
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

    /// <summary>
    /// The user's save for a game as the game page takes it (its hash, size and time; null when there's none), and the
    /// key the page keeps its note of the last save it synced under (the user's, for this game, without saying who).
    /// </summary>
    public static async Task<(object? Save, string Key)> SaveFor(AppDbContext db, string userId, Game game)
    {
        var play = await FindPlay(db, userId, game, tracked: false);
        var key = Games.GameSaves.Hash(System.Text.Encoding.UTF8.GetBytes($"{userId}|{game.System}|{FileNameOf(game).ToLowerInvariant()}"));
        return (play?.SaveHash is null ? null : SaveInfo(play), key);
    }

    static object SaveInfo(GamePlay play) => new { hash = play.SaveHash, size = play.SaveSize, updatedUtc = play.SaveUtc };

    static async Task<Game?> FindGame(int id, AppDbContext db, LibraryCatalog catalog)
    {
        var game = await db.Games.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return game is not null && catalog.Get(game.LibraryId) is { Enabled: true } ? game : null;
    }

    static string FileNameOf(Game game) => game.RelativePath[(game.RelativePath.LastIndexOf('/') + 1)..];

    static Task<GamePlay?> FindPlay(AppDbContext db, string userId, Game game, bool tracked = true)
    {
        var name = FileNameOf(game);
        var plays = tracked ? db.GamePlays : db.GamePlays.AsNoTracking();
        return plays.FirstOrDefaultAsync(p => p.UserId == userId && p.System == game.System && p.FileName == name);
    }

    static object? Controls(GameControls? row) => row is null ? null : new
    {
        layout = row.Layout,
        mapping = row.Mapping is null ? null : JsonNode.Parse(row.Mapping),
    };
}
