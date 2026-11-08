using System.IO.Compression;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Games;
using MediaRiffle.Server.Library;
using MediaRiffle.Server.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Api;

public sealed record EmulatorDownloadRequest(List<string>? Cores);

/// <summary>
/// Admin → Games: the browser emulator (EmulatorJS and its cores, downloaded from npm or uploaded) and the BIOS files
/// some systems need; and, for players, a system's BIOS.
/// </summary>
public static class EmulatorEndpoints
{
    public static void MapEmulatorEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin/emulator").RequireAuthorization("Admin").AddEndpointFilter(Mfa.RequireForAdmin);

        // Each core with the systems it plays and how many games there are for them, and each system's BIOS files
        admin.MapGet("", async (EmulatorFiles emulator, GameArt art, LibraryCatalog catalog, AppDbContext db) =>
        {
            var enabled = catalog.Enabled.Select(l => l.Id).ToHashSet();
            var counts = await db.Games.Where(x => enabled.Contains(x.LibraryId)).GroupBy(x => x.System)
                .Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
            var have = emulator.Cores();
            var (found, notFound, waiting) = await art.CountsAsync(db);
            return new
            {
                // Box art from libretro-thumbnails (games with a picture beside them aren't counted)
                art = new { enabled = GameArt.Enabled, found, notFound, waiting, art.Progress, art.LastResult },
                version = EmulatorFiles.Version,
                installed = emulator.Installed,
                progress = emulator.Progress,
                lastResult = emulator.LastResult,
                cores = GameSystems.All.Where(s => s.Core is not null).GroupBy(s => s.Core!).Select(g => new
                {
                    core = g.Key,
                    systems = g.Select(s => s.Name),
                    games = g.Sum(s => counts.GetValueOrDefault(s.Id)),
                    installed = have.Contains(g.Key),
                }),
                // Systems with a BIOS to add (needed, or better with), and any that have one added
                bios = GameSystems.All.Where(s => s.Core is not null && (s.Bios is not null || s.OptionalBios is not null || emulator.Bios(s.Id).Count > 0))
                    .Select(s => new
                    {
                        system = s.Id, s.Name, needed = s.Bios, optional = s.OptionalBios,
                        files = emulator.Bios(s.Id), games = counts.GetValueOrDefault(s.Id),
                    })
                    .OrderByDescending(b => b.games > 0),
            };
        });

        // No cores named: those for the systems there are games for
        admin.MapPost("/download", async (EmulatorDownloadRequest req, EmulatorFiles emulator, LibraryCatalog catalog, AppDbContext db) =>
        {
            var known = GameSystems.All.Select(s => s.Core).OfType<string>().ToHashSet();
            List<string> cores;
            if (req.Cores is { Count: > 0 }) cores = req.Cores.Where(known.Contains).Distinct().ToList();
            else
            {
                var enabled = catalog.Enabled.Select(l => l.Id).ToHashSet();
                var systems = await db.Games.Where(x => enabled.Contains(x.LibraryId)).Select(x => x.System).Distinct().ToListAsync();
                cores = systems.Select(GameSystems.Get).Select(s => s?.Core).OfType<string>().Distinct().ToList();
            }
            return emulator.StartDownload(cores) ? Results.Accepted() : Results.Conflict(new { error = "A download is running already." });
        });

        // An npm package (.tgz) of EmulatorJS or a core, for a server that can't reach npm itself
        admin.MapPost("/upload", async (HttpContext ctx, EmulatorFiles emulator, CancellationToken ct) =>
        {
            ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = 200L * 1024 * 1024;
            try { return Results.Ok(new { result = await emulator.AddUploadAsync(ctx.Request.Body, ct) }); }
            // What's wrong with it (PackageException), or one running already (InvalidOperation); else it wasn't a gzip,
            // a tar or a package.json at all
            catch (Exception ex) when (ex is PackageException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or System.Text.Json.JsonException or EndOfStreamException)
            {
                return Results.BadRequest(new { error = "That isn't an npm package (.tgz)." });
            }
        });

        admin.MapPost("/bios/{system}", async (string system, string? name, HttpContext ctx, EmulatorFiles emulator, CancellationToken ct) =>
        {
            ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = 64L * 1024 * 1024;
            try
            {
                return await emulator.AddBiosAsync(system, name ?? "", ctx.Request.Body, ct) is { } error
                    ? Results.BadRequest(new { error }) : Results.Ok(emulator.Bios(system));
            }
            catch (PackageException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        admin.MapDelete("/bios/{system}/{name}", (string system, string name, EmulatorFiles emulator) =>
            emulator.RemoveBios(system, name) ? Results.Ok(emulator.Bios(system)) : Results.NotFound());

        // A system's BIOS for the emulator: the file itself, or a zip of them when there are several (the emulator
        // unpacks it). The last part of the address is the name the emulator gives it, so it ends in that name.
        var g = app.MapGroup("/api/games").RequireAuthorization();
        g.MapMethods("/bios/{system}/{name}", [HttpMethods.Get, HttpMethods.Head], (string system, string name, EmulatorFiles emulator, HttpContext ctx) =>
        {
            var files = emulator.Bios(system);
            if (files.Count == 0 || emulator.BiosFolder(system) is not { } dir) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            if (files.Count == 1) return Results.File(Path.Combine(dir, files[0]), "application/octet-stream", files[0]);
            if (HttpMethods.IsHead(ctx.Request.Method)) { ctx.Response.ContentType = "application/zip"; return Results.Empty; }
            return Results.Stream(async body =>
            {
                using var buffer = new MemoryStream();
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                    foreach (var f in files) zip.CreateEntryFromFile(Path.Combine(dir, f), f, CompressionLevel.NoCompression);
                buffer.Position = 0;
                await buffer.CopyToAsync(body, ctx.RequestAborted);
            }, "application/zip", "bios.zip");
        });
    }

    /// <summary>
    /// The address of a system's BIOS for the emulator, or null when none was added. Several go as a zip, named for
    /// when they last changed: the emulator's cache in the browser knows a file by its name and size, and the zip has
    /// no size beforehand.
    /// </summary>
    public static string? BiosUrl(EmulatorFiles emulator, string system)
    {
        var files = emulator.Bios(system);
        if (files.Count == 0 || emulator.BiosFolder(system) is not { } dir) return null;
        var start = $"/api/games/bios/{Uri.EscapeDataString(system)}/";
        if (files.Count == 1) return start + Uri.EscapeDataString(files[0]);
        var changed = files.Max(f => File.GetLastWriteTimeUtc(Path.Combine(dir, f)).Ticks);
        return start + $"bios-{changed:x}-{files.Count}.zip";
    }
}
