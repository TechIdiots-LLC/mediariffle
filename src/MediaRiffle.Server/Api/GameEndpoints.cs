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

        // The systems there are games for, in the table's order, with how many each and whether a browser can play them
        g.MapGet("/games/systems", async (LibraryCatalog catalog, AppDbContext db) =>
        {
            var enabled = EnabledIds(catalog);
            var counts = await db.Games.Where(x => enabled.Contains(x.LibraryId)).GroupBy(x => x.System)
                .Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
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

        g.MapGet("/games/{id:int}", async (int id, LibraryCatalog catalog, AppDbContext db) =>
        {
            var game = await db.Games.FirstOrDefaultAsync(x => x.Id == id);
            if (game is null || catalog.Get(game.LibraryId) is not { Enabled: true } lib) return Results.NotFound();
            var system = GameSystems.Get(game.System);
            return Results.Ok(new
            {
                game.Id, game.Title, game.Size,
                file = game.RelativePath[(game.RelativePath.LastIndexOf('/') + 1)..],
                library = new { lib.Id, lib.Name },
                system = new { id = game.System, name = system?.Name ?? game.System, core = system?.Core, bios = system?.Bios },
                image = ImageUrl(game),
            });
        });

        // The game, for the emulator. A disc game's .cue or .m3u goes out as a zip with the tracks and discs it names,
        // since the emulator takes one file
        g.MapGet("/games/{id:int}/file", async (int id, AppDbContext db, LibraryFiles files, HttpContext ctx) =>
        {
            var game = await db.Games.FirstOrDefaultAsync(x => x.Id == id);
            var path = game is null ? null : files.Resolve(game.LibraryId, game.RelativePath);
            if (path is null || !File.Exists(path)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            var parts = GameParts(path);
            if (parts.Count == 0)
                return Results.File(path, "application/octet-stream", Path.GetFileName(path), enableRangeProcessing: true);

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
        });

        g.MapGet("/images/game/{id:int}", async (int id, AppDbContext db, LibraryFiles files, HttpContext ctx) =>
        {
            var game = await db.Games.FirstOrDefaultAsync(x => x.Id == id);
            var path = game?.ImagePath is null ? null : files.Resolve(game.LibraryId, game.ImagePath);
            if (path is null || !File.Exists(path)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            var type = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
            return Results.File(path, type);
        });
    }

    /// <summary>Games as tiles, by system (in the table's order) and then name.</summary>
    public static async Task<List<object>> Tiles(IQueryable<Game> games)
    {
        var rows = await games.Select(x => new { x.Id, x.Title, x.System, x.ImagePath }).ToListAsync();
        var order = GameSystems.All.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        return rows.OrderBy(x => order.GetValueOrDefault(x.System, int.MaxValue)).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => (object)new
            {
                x.Id, x.Title, system = x.System,
                systemName = GameSystems.Get(x.System)?.Name ?? x.System,
                playable = GameSystems.Get(x.System)?.Core is not null,
                image = x.ImagePath is null ? null : $"/api/images/game/{x.Id}",
            }).ToList();
    }

    private static string? ImageUrl(Game game) => game.ImagePath is null ? null : $"/api/images/game/{game.Id}";

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
