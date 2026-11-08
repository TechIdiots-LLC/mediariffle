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

        // The game page, when a game starts: last played now, and back on Continue playing
        g.MapPost("/{id:int}/played", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db, LibraryCatalog catalog) =>
        {
            if (await FindGame(id, db, catalog) is not { } game) return Results.NotFound();
            var play = await PlayOf(db, users.GetUserId(me)!, game);
            play.LastPlayedUtc = DateTime.UtcNow;
            play.LeftContinue = false;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // Off Continue playing (its saves and states stay)
        g.MapDelete("/{id:int}/continue", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db, LibraryCatalog catalog) =>
        {
            if (await FindGame(id, db, catalog) is not { } game) return Results.NotFound();
            if (await FindPlay(db, users.GetUserId(me)!, game) is { } play) { play.LeftContinue = true; await db.SaveChangesAsync(); }
            return Results.Ok();
        });

        // The games the user played last, most recent first, with the screenshot of their latest state where there's one
        g.MapGet("/continue", async (ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db, LibraryCatalog catalog) =>
        {
            var userId = users.GetUserId(me)!;
            var enabled = catalog.Enabled.Select(l => l.Id).ToList();
            var plays = await db.GamePlays.AsNoTracking().Where(p => p.UserId == userId && !p.LeftContinue)
                .OrderByDescending(p => p.LastPlayedUtc).Take(40).ToListAsync();
            var tiles = new List<object>();
            foreach (var p in plays)
            {
                // The game by its system and file name, wherever it is now
                var name = p.FileName;
                var game = await db.Games.AsNoTracking()
                    .Where(x => enabled.Contains(x.LibraryId) && x.System == p.System && (x.RelativePath == name || x.RelativePath.EndsWith("/" + name)))
                    .OrderBy(x => x.Id).FirstOrDefaultAsync();
                if (game is null) continue;
                var state = await db.GameStates.AsNoTracking().Where(s => s.PlayId == p.Id && s.PictureType != null)
                    .OrderByDescending(s => s.CreatedUtc).FirstOrDefaultAsync();
                tiles.Add(new
                {
                    game.Id, title = GameVersions.Name(game.Title), system = game.System,
                    systemName = GameSystems.Get(game.System)?.Name ?? game.System,
                    image = state is null ? GameEndpoints.ImageUrl(game) : $"/api/games/states/{state.Id}/picture",
                    lastPlayedUtc = Utc(p.LastPlayedUtc),
                });
                if (tiles.Count == 20) break;
            }
            return tiles;
        });

        // The user's save states of a game, newest first
        g.MapGet("/{id:int}/states", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db, LibraryCatalog catalog) =>
        {
            if (await FindGame(id, db, catalog) is not { } game) return Results.NotFound();
            var play = await FindPlay(db, users.GetUserId(me)!, game, tracked: false);
            if (play is null) return Results.Ok(Array.Empty<object>());
            var states = await db.GameStates.AsNoTracking().Where(s => s.PlayId == play.Id).OrderByDescending(s => s.CreatedUtc).ToListAsync();
            return Results.Ok(states.Select(StateInfo));
        });

        // A new save state, as the emulator made it (its screenshot follows, below). The oldest go past StatesPerGame.
        g.MapPost("/{id:int}/states", async (int id, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db, LibraryCatalog catalog,
            Games.GameSaves saves, HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = Games.GameSaves.MaxState;
            if (await FindGame(id, db, catalog) is not { } game) return Results.NotFound();
            var data = await ReadBody(ctx, ct);
            if (data is null) return Results.BadRequest(new { error = "That save state is larger than MediaRiffle keeps." });
            if (data.Length == 0) return Results.BadRequest(new { error = "The save state is empty." });
            var userId = users.GetUserId(me)!;
            if (await saves.OverLimitAsync(db, userId, data.Length) is { } error) return Results.BadRequest(new { error });

            var play = await PlayOf(db, userId, game);
            play.LastPlayedUtc = DateTime.UtcNow;
            var state = new GameSaveState { PlayId = play.Id, CreatedUtc = DateTime.UtcNow, Size = data.Length };
            db.GameStates.Add(state);
            await db.SaveChangesAsync(ct);
            try { await Games.GameSaves.WriteAsync(saves.StatePath(userId, play.Id, state.Id), data, ct); }
            catch
            {
                db.GameStates.Remove(state);
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }

            if (saves.StatesPerGame > 0)
            {
                var old = await db.GameStates.Where(s => s.PlayId == play.Id).OrderByDescending(s => s.CreatedUtc).Skip(saves.StatesPerGame).ToListAsync(ct);
                foreach (var s in old) saves.DeleteState(userId, play.Id, s.Id);
                db.GameStates.RemoveRange(old);
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(StateInfo(state));
        });

        // A state's screenshot (PNG, JPEG or WebP, as the emulator took it)
        g.MapPut("/states/{sid:int}/picture", async (int sid, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db,
            Games.GameSaves saves, HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = Games.GameSaves.MaxPicture;
            var userId = users.GetUserId(me)!;
            if (await FindState(db, userId, sid) is not { } found) return Results.NotFound();
            var type = ctx.Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
            if (type is not ("image/png" or "image/jpeg" or "image/webp")) return Results.BadRequest(new { error = "The screenshot is a PNG, JPEG or WebP." });
            var data = await ReadBody(ctx, ct);
            if (data is null || data.Length == 0) return Results.BadRequest(new { error = "That screenshot is too large." });
            await Games.GameSaves.WriteAsync(saves.PicturePath(userId, found.PlayId, sid), data, ct);
            found.State.PictureType = type;
            found.State.PictureSize = data.Length;
            await db.SaveChangesAsync(ct);
            return Results.Ok(StateInfo(found.State));
        });

        g.MapGet("/states/{sid:int}", async (int sid, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db, Games.GameSaves saves, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "private, max-age=86400";   // a state never changes; a new one is a new id
            var userId = users.GetUserId(me)!;
            if (await FindState(db, userId, sid) is not { } found) return Results.NotFound();
            var path = saves.StatePath(userId, found.PlayId, sid);
            return File.Exists(path) ? Results.File(path, "application/octet-stream") : Results.NotFound();
        });

        g.MapGet("/states/{sid:int}/picture", async (int sid, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db, Games.GameSaves saves, HttpContext ctx) =>
        {
            var userId = users.GetUserId(me)!;
            if (await FindState(db, userId, sid) is not { State.PictureType: { } type } found) return Results.NotFound();
            var path = saves.PicturePath(userId, found.PlayId, sid);
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            return File.Exists(path) ? Results.File(path, type) : Results.NotFound();
        });

        g.MapDelete("/states/{sid:int}", async (int sid, ClaimsPrincipal me, UserManager<AppUser> users, AppDbContext db, Games.GameSaves saves) =>
        {
            var userId = users.GetUserId(me)!;
            if (await FindState(db, userId, sid) is not { } found) return Results.NotFound();
            saves.DeleteState(userId, found.PlayId, sid);
            db.GameStates.Remove(found.State);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // Admin → Games: how much space each user's saves and states take
        var admin = app.MapGroup("/api/admin/gamesaves").RequireAuthorization("Admin").AddEndpointFilter(Security.Mfa.RequireForAdmin);
        admin.MapGet("", async (AppDbContext db, Games.GameSaves saves) =>
        {
            var plays = await db.GamePlays.AsNoTracking().GroupBy(p => p.UserId)
                .Select(x => new { x.Key, saves = x.Count(p => p.SaveHash != null), saveBytes = x.Sum(p => p.SaveSize) }).ToListAsync();
            var states = await db.GameStates.AsNoTracking().Join(db.GamePlays, s => s.PlayId, p => p.Id, (s, p) => new { p.UserId, bytes = s.Size + s.PictureSize })
                .GroupBy(x => x.UserId).Select(x => new { x.Key, count = x.Count(), bytes = x.Sum(y => y.bytes) }).ToListAsync();
            var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);
            return new
            {
                limitMB = saves.Limit / 1024 / 1024, statesPerGame = saves.StatesPerGame,
                users = names.Select(n =>
                {
                    var p = plays.FirstOrDefault(x => x.Key == n.Key);
                    var s = states.FirstOrDefault(x => x.Key == n.Key);
                    return new { name = n.Value, saves = p?.saves ?? 0, states = s?.count ?? 0, bytes = (p?.saveBytes ?? 0) + (s?.bytes ?? 0) };
                }).Where(u => u.saves > 0 || u.states > 0).OrderByDescending(u => u.bytes),
            };
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

    static object SaveInfo(GamePlay play) => new { hash = play.SaveHash, size = play.SaveSize, updatedUtc = play.SaveUtc is { } t ? Utc(t) : (DateTime?)null };

    static object StateInfo(GameSaveState s) => new
    {
        s.Id, createdUtc = Utc(s.CreatedUtc), s.Size,
        picture = s.PictureType is null ? null : $"/api/games/states/{s.Id}/picture",
    };

    // SQLite gives times back without their kind
    static DateTime Utc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc);

    // The user's play of a game, made when there's none yet
    static async Task<GamePlay> PlayOf(AppDbContext db, string userId, Game game)
    {
        if (await FindPlay(db, userId, game) is { } play) return play;
        db.GamePlays.Add(play = new GamePlay { UserId = userId, System = game.System, FileName = FileNameOf(game), LastPlayedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return play;
    }

    // One of the user's states (none of anyone else's), with the play it's of
    static async Task<(GameSaveState State, int PlayId)?> FindState(AppDbContext db, string userId, int stateId)
    {
        var state = await db.GameStates.FirstOrDefaultAsync(s => s.Id == stateId && db.GamePlays.Any(p => p.Id == s.PlayId && p.UserId == userId));
        return state is null ? null : (state, state.PlayId);
    }

    // The request's body, or null when it's larger than the endpoint takes
    static async Task<byte[]?> ReadBody(HttpContext ctx, CancellationToken ct)
    {
        try
        {
            using var body = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(body, ct);
            return body.ToArray();
        }
        catch (BadHttpRequestException) { return null; }
    }

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
