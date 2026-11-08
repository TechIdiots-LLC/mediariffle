using MediaRiffle.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaRiffle.Server.Library;

public sealed partial class LibraryScanner
{
    /// <summary>
    /// A Games library: every file a system's game, by its extension or the folders it's in (GameSystems.Classify).
    /// A disc game's .cue or .m3u is the game, and the tracks and discs it names go with it. Nothing is probed; a zip
    /// whose folders don't say is opened to see which system's games are in it.
    /// </summary>
    private async Task<(int Added, int Updated, int Removed, int Failed)> ScanGamesAsync(LibraryInfo lib, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var root = Path.GetFullPath(lib.Path);
        var known = await db.Games.Where(g => g.LibraryId == lib.Id).ToDictionaryAsync(g => g.RelativePath, StringComparer.Ordinal, ct);

        // Every file, for finding pictures without asking the disk again
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var infos = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        Progress = $"{lib.Name}: looking for games…";
        foreach (var path in Directory.EnumerateFiles(root, "*", enumeration))
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            files.Add(rel);
            infos[rel] = new FileInfo(path);
        }

        var found = new Dictionary<string, GameSystem>(StringComparer.Ordinal);
        var parts = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // a .cue's tracks, an .m3u's discs
        var programs = GameSystems.ProgramFolders(infos.Keys);
        foreach (var (rel, info) in infos)
        {
            ct.ThrowIfCancellationRequested();
            if (GameSystems.IsUnder(rel, programs)) continue;
            var system = GameSystems.Classify(rel);
            if (system is null && rel.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                system = known.TryGetValue(rel, out var was) && was.Size == info.Length && was.ModifiedUtc == info.LastWriteTimeUtc
                    ? GameSystems.Get(was.System)
                    : GameSystems.FromZip(info.FullName);
            if (system is null) continue;
            found[rel] = system;
            var slash = rel.LastIndexOf('/');
            var dir = slash < 0 ? "" : rel[..(slash + 1)];
            var named = GameSystems.PartsOf(info.FullName)
                .Select(p => files.TryGetValue(dir + p, out var actual) ? actual : null)
                .OfType<string>().ToList();
            if (named.Count > 0) parts[rel] = named;
        }
        // A track or disc a list names isn't a game of its own (an .m3u's discs are .cues, whose tracks are theirs)
        var inLists = parts.Values.SelectMany(p => p).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var repacked = GameSystems.Repacked(found.Keys);
        var games = found.Where(kv => !inLists.Contains(kv.Key) && !repacked.Contains(kv.Key)).ToList();

        long SizeOf(string rel, int depth = 0) => infos[rel].Length +
            (depth < 2 && parts.TryGetValue(rel, out var p) ? p.Sum(x => SizeOf(x, depth + 1)) : 0);
        DateTime ChangedOf(string rel, int depth = 0)
        {
            var t = infos[rel].LastWriteTimeUtc;
            if (depth < 2 && parts.TryGetValue(rel, out var p))
                foreach (var x in p) { var c = ChangedOf(x, depth + 1); if (c > t) t = c; }
            return t;
        }

        int added = 0, updated = 0;
        Progress = $"{lib.Name}: {games.Count} games";
        foreach (var (rel, system) in games)
        {
            var size = SizeOf(rel);
            var changed = ChangedOf(rel);
            var title = GameSystems.Title(rel[(rel.LastIndexOf('/') + 1)..]);
            var picture = GameSystems.FindPicture(rel, files);
            if (known.TryGetValue(rel, out var game))
            {
                if (game.Size == size && game.ModifiedUtc == changed && game.System == system.Id && game.Title == title && game.ImagePath == picture)
                    continue;
                updated++;
            }
            else
            {
                game = new Game { LibraryId = lib.Id, RelativePath = rel };
                db.Games.Add(game);
                added++;
            }
            game.System = system.Id;
            game.Title = title;
            game.ImagePath = picture;
            game.Size = size;
            game.ModifiedUtc = changed;
        }

        var onDisk = games.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var gone = known.Values.Where(g => !onDisk.Contains(g.RelativePath)).ToList();
        db.Games.RemoveRange(gone);
        await db.SaveChangesAsync(ct);
        return (added, updated, gone.Count, 0);
    }
}
