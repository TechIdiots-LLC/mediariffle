using System.IO.Compression;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Library;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

/// <summary>
/// Games libraries: the games by system and by library, one game's details, and the game itself for the browser's
/// emulator (a disc game with its tracks or discs, zipped as it goes out).
/// </summary>
public static class GameEndpoints
{
    public static void MapGameEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // The systems there are games for, in the table's order, with how many each (a game's versions counted once)
        // and whether a browser can play them
        g.MapGet("/games/systems", async (LibraryCatalog catalog, AppDbContext db) =>
        {
            var enabled = EnabledIds(catalog);
            var rows = await db.Games.Where(x => enabled.Contains(x.LibraryId)).Select(x => new { x.Id, x.System, x.Title }).ToListAsync();
            var counts = rows.GroupBy(x => x.System)
                .ToDictionary(x => x.Key, x => x.Select(r => GroupKey(r.Id, r.Title)).Distinct().Count());
            return GameSystems.All.Where(s => counts.ContainsKey(s.Id))
                .Select(s => new { s.Id, s.Name, games = counts[s.Id], playable = s.Core is not null });
        });

        g.MapGet("/games", async (string? system, LibraryCatalog catalog, AppDbContext db) =>
        {
            var enabled = EnabledIds(catalog);
            var q = db.Games.Where(x => enabled.Contains(x.LibraryId));
            if (!string.IsNullOrWhiteSpace(system)) q = q.Where(x => x.System == system);
            return await Tiles(q);
        });

        g.MapGet("/libraries/{id:int}/games", async (int id, LibraryCatalog catalog, AppDbContext db) =>
            catalog.Get(id) is not { Enabled: true, Kind: Options.LibraryKind.Games } ? Results.NotFound()
                : Results.Ok(await Tiles(db.Games.Where(x => x.LibraryId == id))));

        // With what the play page needs: the emulator core, the file's address, the BIOS, and whether it can start
        // (the emulator and core in place under Admin → Games, and a BIOS the system can't do without)
        g.MapGet("/games/{id:int}", async (int id, LibraryCatalog catalog, AppDbContext db, LibraryFiles files, Games.EmulatorFiles emulator,
            System.Security.Claims.ClaimsPrincipal me, Microsoft.AspNetCore.Identity.UserManager<AppUser> users) =>
        {
            var game = await db.Games.FirstOrDefaultAsync(x => x.Id == id);
            if (game is null || catalog.Get(game.LibraryId) is not { Enabled: true } lib) return Results.NotFound();
            var system = GameSystems.Get(game.System);
            var file = game.RelativePath[(game.RelativePath.LastIndexOf('/') + 1)..];
            var path = files.Resolve(game.LibraryId, game.RelativePath);
            // The emulator names the game after the end of its address, and some cores go by the extension
            // A disc game's zip is made as it goes out, so it has no size beforehand, which is what the emulator's cache
            // in the browser goes by: its name carries when the game last changed instead
            var sent = path is not null && GameParts(path).Count > 0
                ? $"{Path.GetFileNameWithoutExtension(file)}-{game.ModifiedUtc.Ticks:x}.zip" : file;
            var installed = emulator.Installed is not null;
            var saved = await GamePlayEndpoints.SaveFor(db, users.GetUserId(me)!, game);
            var coreReady = system?.Core is { } core && installed && emulator.Cores().Contains(core);

            // Its versions in the enabled libraries, best first, each told apart in words
            var key = GroupKey(game.Id, game.Title);
            var enabled = EnabledIds(catalog);
            var same = await db.Games.Where(x => x.System == game.System && enabled.Contains(x.LibraryId))
                .Select(x => new { x.Id, x.Title, x.RelativePath, x.LibraryId }).ToListAsync();
            var versions = GameVersions.Best(same.Where(x => x.Id == game.Id || GroupKey(x.Id, x.Title) == key), x => x.RelativePath);
            var labels = GameVersions.Labels(versions.Select(x => x.RelativePath[(x.RelativePath.LastIndexOf('/') + 1)..]).ToList());
            var libraries = versions.Select(x => x.LibraryId).Distinct().Count();

            return Results.Ok(new
            {
                game.Id, game.Title, game.Size, file,
                // Without its tags (the file name, shown too, has them)
                name = GameVersions.Name(game.Title),
                versions = versions.Select((x, i) => new
                {
                    x.Id,
                    label = libraries > 1 ? $"{labels[i]} ({catalog.Get(x.LibraryId)?.Name})" : labels[i],
                }).ToList(),
                library = new { lib.Id, lib.Name },
                system = new { id = game.System, name = system?.Name ?? game.System, core = system?.Core, bios = system?.Bios },
                image = ImageUrl(game),
                fileUrl = $"/api/games/{game.Id}/file/{Uri.EscapeDataString(sent)}",
                biosUrl = system is null ? null : EmulatorEndpoints.BiosUrl(emulator, system.Id),
                // The user's controller mapping for the system, null for EmulatorJS's own
                controls = await GamePlayEndpoints.ControlsFor(db, users.GetUserId(me)!, game.System),
                // The user's in-game save, which the play page loads, and the key it keeps its note of it under
                save = saved.Save,
                saveKey = saved.Key,
                // Why it can't start here, if it can't
                notReady = system?.Core is null ? $"No emulator for the {system?.Name ?? game.System} runs in a browser."
                    : !installed ? "The emulator isn't set up yet: an admin adds it under Admin → Games."
                    : !coreReady ? $"The {system.Name} emulator ({system.Core}) isn't added yet: an admin adds it under Admin → Games."
                    : system.Bios is not null && EmulatorEndpoints.BiosUrl(emulator, system.Id) is null
                        ? $"The {system.Name} needs its BIOS ({system.Bios}): an admin adds it under Admin → Games."
                    : null,
            });
        });

        // The game, for the emulator. A disc game's .cue or .m3u goes out as a zip with the tracks and discs it names,
        // since the emulator takes one file. The name after /file/ is only for the emulator (see above). HEAD too: the
        // emulator asks for the size first, to see whether the copy it keeps in the browser is the same.
        string[] getOrHead = [HttpMethods.Get, HttpMethods.Head];
        g.MapMethods("/games/{id:int}/file/{name}", getOrHead, (int id, string name, AppDbContext db, LibraryFiles files, HttpContext ctx) =>
            SendGame(id, db, files, ctx));
        g.MapMethods("/games/{id:int}/file", getOrHead, (int id, AppDbContext db, LibraryFiles files, HttpContext ctx) => SendGame(id, db, files, ctx));

        // A picture beside the game first, then its box art kept from libretro-thumbnails, looked up now when the
        // background pass hasn't got to it yet (so tiles fill in as they're shown)
        g.MapGet("/images/game/{id:int}", async (int id, AppDbContext db, LibraryFiles files, Games.GameArt art, HttpContext ctx) =>
        {
            var game = await db.Games.FirstOrDefaultAsync(x => x.Id == id);
            if (game is null) return Results.NotFound();
            var path = game.ImagePath is null ? null : files.Resolve(game.LibraryId, game.ImagePath);
            if (path is null || !File.Exists(path))
            {
                path = game.ArtName is null ? null : art.CachedFile(game.System, game.ArtName);
                if (path is null && (game.ArtName is not null || Games.GameArt.MayHaveArt(game.System, game.ArtCheckedUtc)))
                {
                    try { path = await art.LookUpAsync(id, ctx.RequestAborted).WaitAsync(TimeSpan.FromSeconds(20), ctx.RequestAborted); }
                    catch (TimeoutException) { }
                }
            }
            if (path is null)
            {
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.NotFound();
            }
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            var type = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
            return Results.File(path, type);
        });
    }

    private static async Task<IResult> SendGame(int id, AppDbContext db, LibraryFiles files, HttpContext ctx)
    {
        var game = await db.Games.FirstOrDefaultAsync(x => x.Id == id);
        var path = game is null ? null : files.Resolve(game.LibraryId, game.RelativePath);
        if (path is null || !File.Exists(path)) return Results.NotFound();
        ctx.Response.Headers.CacheControl = "private, max-age=3600";
        var parts = GameParts(path);
        if (parts.Count == 0)
            return Results.File(path, "application/octet-stream", Path.GetFileName(path), enableRangeProcessing: true);
        if (HttpMethods.IsHead(ctx.Request.Method))
        {
            ctx.Response.ContentType = "application/zip";
            return Results.Empty;
        }

        // Stored rather than compressed: disc images hardly shrink, and the emulator unpacks it sooner
        return Results.Stream(async body =>
        {
            var output = new HoldSyncWrites(body);
            await using (var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, ctx.RequestAborted))
            {
                var dir = Path.GetDirectoryName(path)!;
                foreach (var file in parts.Prepend(path))
                {
                    var entry = zip.CreateEntry(Path.GetRelativePath(dir, file).Replace('\\', '/'), CompressionLevel.NoCompression);
                    await using var to = await entry.OpenAsync(ctx.RequestAborted);
                    await using var from = File.OpenRead(file);
                    await from.CopyToAsync(to, ctx.RequestAborted);
                }
            }
            await output.FlushAsync(ctx.RequestAborted);
        }, "application/zip", Path.GetFileNameWithoutExtension(path) + ".zip");
    }

    /// <summary>
    /// Games as tiles, by system (in the table's order) and then name (without tags): a game's versions among them as
    /// one tile, opening the best of them, with the picture of the best that has one.
    /// </summary>
    public static async Task<List<object>> Tiles(IQueryable<Game> games)
    {
        var rows = await games.Select(x => new { x.Id, x.Title, x.System, x.RelativePath, x.ImagePath, x.ArtName, x.ArtCheckedUtc }).ToListAsync();
        var order = GameSystems.All.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        return rows.GroupBy(x => (x.System, key: GroupKey(x.Id, x.Title)))
            .Select(g =>
            {
                var best = GameVersions.Best(g, x => x.RelativePath);
                var first = best[0];
                var pictured = best.FirstOrDefault(x => x.ImagePath is not null || x.ArtName is not null)
                    ?? best.FirstOrDefault(x => Games.GameArt.MayHaveArt(x.System, x.ArtCheckedUtc));
                return new
                {
                    first.Id, Title = GameVersions.Name(first.Title), first.System,
                    versions = best.Count, image = pictured is null ? null : $"/api/images/game/{pictured.Id}",
                };
            })
            .OrderBy(x => order.GetValueOrDefault(x.System, int.MaxValue)).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => (object)new
            {
                x.Id, x.Title, system = x.System,
                systemName = GameSystems.Get(x.System)?.Name ?? x.System,
                playable = GameSystems.Get(x.System)?.Core is not null,
                x.versions, x.image,
            }).ToList();
    }

    // The versions of a game share it (a name of tags alone is a game of its own)
    private static string GroupKey(int id, string title) => GameVersions.Key(title) is { Length: > 0 } key ? key : "#" + id;

    /// <summary>The address of a game's picture (box art), or null when it has none.</summary>
    public static string? ImageUrl(Game game) =>
        HasPicture(game.ImagePath, game.ArtName, game.System, game.ArtCheckedUtc) ? $"/api/images/game/{game.Id}" : null;

    // One beside it, box art kept, or box art not looked for yet (a game known to have none gets no address, so its
    // tile asks for nothing)
    private static bool HasPicture(string? imagePath, string? artName, string system, DateTime? artChecked) =>
        imagePath is not null || artName is not null || Games.GameArt.MayHaveArt(system, artChecked);

    /// <summary>The files a disc game's .cue or .m3u names (an .m3u's discs with their own tracks), in its folder.</summary>
    private static List<string> GameParts(string listPath, int depth = 0)
    {
        var dir = Path.GetDirectoryName(listPath)!;
        var found = new List<string>();
        foreach (var part in GameSystems.PartsOf(listPath))
        {
            // Only inside the game's folder: a list naming ../ or another place can't reach out of it
            var full = LibraryFiles.Resolve(dir, part);
            if (full is null || !File.Exists(full) || found.Contains(full)) continue;
            found.Add(full);
            if (depth == 0) found.AddRange(GameParts(full, 1).Where(p => !found.Contains(p)));
        }
        return found;
    }

    private static HashSet<int> EnabledIds(LibraryCatalog catalog) => catalog.Enabled.Select(l => l.Id).ToHashSet();

    /// <summary>
    /// The response body as ZipArchive writes it: the server allows no synchronous writes, and ZipArchive still makes
    /// a few small ones even when written asynchronously (each entry's data descriptor). Those are held and sent ahead
    /// of the next asynchronous write, or by FlushAsync at the end.
    /// </summary>
    private sealed class HoldSyncWrites(Stream inner) : Stream
    {
        private readonly MemoryStream held = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => held.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => held.Write(buffer);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await ReleaseAsync(ct);
            await inner.WriteAsync(buffer, ct);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async Task FlushAsync(CancellationToken ct)
        {
            await ReleaseAsync(ct);
            await inner.FlushAsync(ct);
        }

        private async ValueTask ReleaseAsync(CancellationToken ct)
        {
            if (held.Length == 0) return;
            held.Position = 0;
            await held.CopyToAsync(inner, ct);
            held.SetLength(0);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
