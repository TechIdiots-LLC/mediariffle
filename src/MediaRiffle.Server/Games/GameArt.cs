using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Media;
using MediaRiffle.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Games;

/// <summary>
/// Box art for games without a picture beside them, from the libretro-thumbnails project (thumbnails.libretro.com):
/// each system's list of names is read (and read again after 30 days), a game is matched to one by its name (letters
/// and digits only, tags and "The" aside, its region preferred), and the picture is fetched once, made a 600-pixel
/// JPEG and kept in the data folder for good, so the games there are keep their art if the service goes. Runs in the
/// background after scans, and right away for a game whose tile is shown before its turn.
/// </summary>
public sealed partial class GameArt(IServiceScopeFactory scopes, IOptions<MediaRiffleOptions> options, Ffmpeg ffmpeg,
    Library.LibraryScanner scanner, ILogger<GameArt> log) : BackgroundService
{
    private const string Site = "https://thumbnails.libretro.com/";
    private static readonly TimeSpan IndexAge = TimeSpan.FromDays(30);

    /// <summary>The libretro-thumbnails folders for each system (none for arcade: its art is named by title, its games by romset).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Folders = new Dictionary<string, string[]>
    {
        ["nes"] = ["Nintendo - Nintendo Entertainment System"],
        ["snes"] = ["Nintendo - Super Nintendo Entertainment System"],
        ["segaMD"] = ["Sega - Mega Drive - Genesis"],
        ["segaMS"] = ["Sega - Master System - Mark III"],
        ["segaGG"] = ["Sega - Game Gear"],
        ["segaCD"] = ["Sega - Mega-CD - Sega CD"],
        ["sega32x"] = ["Sega - 32X"],
        ["gb"] = ["Nintendo - Game Boy"],
        ["gbc"] = ["Nintendo - Game Boy Color"],
        ["gba"] = ["Nintendo - Game Boy Advance"],
        ["n64"] = ["Nintendo - Nintendo 64"],
        ["nds"] = ["Nintendo - Nintendo DS"],
        ["psx"] = ["Sony - PlayStation"],
        ["psp"] = ["Sony - PlayStation Portable"],
        ["pce"] = ["NEC - PC Engine - TurboGrafx 16"],
        ["pcecd"] = ["NEC - PC Engine CD - TurboGrafx-CD"],
        ["ngp"] = ["SNK - Neo Geo Pocket", "SNK - Neo Geo Pocket Color"],
        ["ws"] = ["Bandai - WonderSwan", "Bandai - WonderSwan Color"],
        ["lynx"] = ["Atari - Lynx"],
        ["vb"] = ["Nintendo - Virtual Boy"],
        ["atari2600"] = ["Atari - 2600"],
        ["atari5200"] = ["Atari - 5200"],
        ["atari7800"] = ["Atari - 7800"],
        ["jaguar"] = ["Atari - Jaguar"],
        ["coleco"] = ["Coleco - ColecoVision"],
        ["segaSaturn"] = ["Sega - Saturn"],
        ["3ds"] = ["Nintendo - Nintendo 3DS"],
        ["ps2"] = ["Sony - PlayStation 2"],
        ["gamecube"] = ["Nintendo - GameCube"],
        ["wii"] = ["Nintendo - Wii"],
        ["dreamcast"] = ["Sega - Dreamcast"],
    };

    /// <summary>Whether box art is fetched at all (MediaRiffle:Games:BoxArt); set at start.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Whether a game without a picture of its own may still get one: not looked for yet, and its system has art.</summary>
    public static bool MayHaveArt(string system, DateTime? checkedUtc) => Enabled && checkedUtc is null && Folders.ContainsKey(system);

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders = { { "User-Agent", $"MediaRiffle/{Api.AuthEndpoints.Version}" } },
    };

    private readonly SemaphoreSlim _slots = new(4, 4);
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Dictionary<string, List<(string Folder, string Name)>>> _indexes = new();
    private readonly ConcurrentDictionary<int, Task<string?>> _looking = new();
    private readonly ConcurrentDictionary<string, DateTime> _listFailed = new();
    private readonly ConcurrentDictionary<string, Task<string?>> _fetching = new();
    private readonly Channel _wake = new();

    private string Root => options.Value.GameArtDirectory;

    /// <summary>What the background pass is doing; null when idle.</summary>
    public string? Progress { get; private set; }
    public string? LastResult { get; private set; }

    /// <summary>Asks for a pass soon (after a scan).</summary>
    public void Wake() => _wake.Signal();

    // ── Matching ──

    /// <summary>
    /// A name as compared: brackets and their tags, DS sets' "0001 " numbers, "The" and accents dropped, then letters
    /// and digits only, so "7th Saga, The (U) [!]", "the_7th_saga" and libretro's "7th Saga, The (USA)" are one.
    /// </summary>
    public static string Key(string name)
    {
        var s = name.Replace('_', ' ');
        s = NumberedSet().Replace(s, "");
        s = Brackets().Replace(s, " ");
        s = TheWord().Replace(s.ToLowerInvariant(), " ");
        var plain = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (char.IsAsciiLetterOrDigit(c)) plain.Append(c);
            else if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) plain.Append(c);
        return plain.ToString();
    }

    [GeneratedRegex(@"^\s*(\d{4}|x{4})\s+", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedSet();

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"(^|[\s,])the(?=\s|$)")]
    private static partial Regex TheWord();

    /// <summary>The region a file name says (GoodTools' (U), (E), (J), (JU)…, or No-Intro's words): usa, europe, japan, world, or null.</summary>
    public static string? Region(string name)
    {
        foreach (Match m in ParenGroup().Matches(name))
        {
            var tag = m.Groups[1].Value.Trim().ToLowerInvariant();
            if (tag is "u" or "us" or "usa" or "ue" or "ju" or "uj" || tag.Contains("usa")) return "usa";
            if (tag is "w" or "world" || tag.Contains("world")) return "world";
            if (tag is "e" or "eu" or "eur" or "europe" or "uk" or "f" or "g" or "s" or "i" || tag.Contains("europe")) return "europe";
            if (tag is "j" or "jp" or "jap" or "japan" || tag.Contains("japan")) return "japan";
        }
        return null;
    }

    [GeneratedRegex(@"\(([^)]*)\)")]
    private static partial Regex ParenGroup();

    private static readonly string[] Preferred = ["usa", "world", "europe", "japan"];
    private static readonly string[] Unwanted = ["beta", "proto", "demo", "sample", "kiosk", "pirate", "unl"];

    /// <summary>
    /// Of the names with the game's key, the one to use: the game's own region first, else USA, World, Europe, Japan;
    /// a release over a beta, demo or prototype; the fewest extra tags.
    /// </summary>
    public static string? Pick(string game, IEnumerable<string> candidates)
    {
        var want = Region(game);
        return candidates
            .OrderBy(c => Unwanted.Any(u => c.Contains("(" + u, StringComparison.OrdinalIgnoreCase)) ? 1 : 0)
            .ThenBy(c =>
            {
                var r = Region(c);
                if (r is not null && r == want) return 0;
                var i = r is null ? -1 : Array.IndexOf(Preferred, r);
                return i < 0 ? 9 : i + 1;
            })
            .ThenBy(c => ParenGroup().Matches(c).Count)
            .ThenBy(c => c.Length)
            .FirstOrDefault();
    }

    // ── Files ──

    /// <summary>The art kept for a system under a libretro name, or null when it isn't here.</summary>
    public string? CachedFile(string system, string name)
    {
        var path = Path.Combine(Root, system, FileName(name));
        return File.Exists(path) ? path : null;
    }

    private static string FileName(string name)
    {
        var safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return safe + ".jpg";
    }

    /// <summary>A system's libretro names, by key; read again from the site after 30 days (kept as it was when that fails).</summary>
    private async Task<Dictionary<string, List<(string Folder, string Name)>>?> IndexAsync(string system, CancellationToken ct)
    {
        if (!Folders.TryGetValue(system, out var folders)) return null;
        var file = Path.Combine(Root, "index", system + ".txt");
        if (_indexes.TryGetValue(system, out var cached) && File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < IndexAge)
            return cached;
        await _indexLock.WaitAsync(ct);
        try
        {
            var fresh = File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < IndexAge;
            // After the site failed to answer, not asked again for an hour (the old list, if any, does meanwhile)
            var resting = _listFailed.TryGetValue(system, out var failed) && DateTime.UtcNow - failed < TimeSpan.FromHours(1);
            if (resting && !File.Exists(file)) return null;
            if (!fresh && !resting)
            {
                try
                {
                    var lines = new List<string>();
                    foreach (var folder in folders)
                    {
                        var html = await Http.GetStringAsync(Site + Uri.EscapeDataString(folder) + "/Named_Boxarts/", ct);
                        foreach (Match m in ListedPng().Matches(html))
                            lines.Add(folder + "\t" + Uri.UnescapeDataString(m.Groups[1].Value)[..^4]);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllLinesAsync(file, lines, ct);
                    _indexes.TryRemove(system, out _);
                    // The games of this system that had no match get another look against the new list
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    await db.Games.Where(x => x.System == system && x.ArtName == null && x.ArtCheckedUtc != null)
                        .ExecuteUpdateAsync(u => u.SetProperty(x => x.ArtCheckedUtc, (DateTime?)null), ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    log.LogWarning("Couldn't read the box art list for {System}: {Error}", system, ex.Message);
                    _listFailed[system] = DateTime.UtcNow;
                    if (!File.Exists(file)) return null;
                }
            }
            if (_indexes.TryGetValue(system, out cached)) return cached;
            var index = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
            foreach (var line in await File.ReadAllLinesAsync(file, ct))
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                var name = line[(tab + 1)..];
                var key = Key(name);
                if (key.Length == 0) continue;
                if (!index.TryGetValue(key, out var list)) index[key] = list = [];
                list.Add((line[..tab], name));
            }
            return _indexes[system] = index;
        }
        finally { _indexLock.Release(); }
    }

    [GeneratedRegex("href=\"([^\"?/][^\"]*\\.png)\"")]
    private static partial Regex ListedPng();

    /// <summary>
    /// Fetches a libretro picture once and keeps it as a JPEG of at most 512 pixels; the path, or null. Games that
    /// share one picture (a game's [a1] and [b1] dumps) share the download.
    /// </summary>
    private Task<string?> FetchAsync(string system, string folder, string name, CancellationToken ct)
    {
        var output = Path.Combine(Root, system, FileName(name));
        if (File.Exists(output)) return Task.FromResult<string?>(output);
        return _fetching.GetOrAdd(output, path => DownloadAsync(path, folder, name, ct).ContinueWith(t =>
        {
            _fetching.TryRemove(output, out _);
            return t;
        }, TaskScheduler.Default).Unwrap());
    }

    private async Task<string?> DownloadAsync(string output, string folder, string name, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temp = output + ".png.part";
        try
        {
            var url = $"{Site}{Uri.EscapeDataString(folder)}/Named_Boxarts/{Uri.EscapeDataString(name)}.png";
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode) return null;
                await using var to = File.Create(temp);
                await (await response.Content.ReadAsStreamAsync(ct)).CopyToAsync(to, ct);
            }
            try
            {
                // One picture (-update 1: a "%" in a name isn't a frame-number pattern), JPEG named outright as the
                // temporary name's extension doesn't say
                await ffmpeg.RunAsync(ffmpeg.FfmpegPath, ["-hide_banner", "-loglevel", "error", "-y", "-i", temp,
                    "-vf", "scale='min(512,iw)':'min(512,ih)':force_original_aspect_ratio=decrease", "-frames:v", "1",
                    "-c:v", "mjpeg", "-q:v", "4", "-f", "image2", "-update", "1", output + ".part"],
                    TimeSpan.FromSeconds(30), ct);
                File.Move(output + ".part", output, overwrite: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                // Without ffmpeg the picture is kept as it came (a PNG under the .jpg name: browsers go by its content)
                log.LogDebug("Box art kept unscaled: {Error}", ex.Message);
                File.Move(temp, output, overwrite: true);
            }
            return output;
        }
        finally
        {
            File.Delete(temp);
            File.Delete(output + ".part");
        }
    }

    /// <summary>
    /// Looks for one game's art now (a tile shown before the background pass got to it): its kept picture, or null.
    /// Records what was found, or that nothing was.
    /// </summary>
    public Task<string?> LookUpAsync(int gameId, CancellationToken ct) =>
        _looking.GetOrAdd(gameId, id => LookUpOneAsync(id).ContinueWith(t =>
        {
            _looking.TryRemove(id, out _);
            return t.IsCompletedSuccessfully ? t.Result : null;
        }, TaskScheduler.Default)).WaitAsync(ct);

    private async Task<string?> LookUpOneAsync(int gameId)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var game = await db.Games.FirstOrDefaultAsync(x => x.Id == gameId);
        if (game is null) return null;
        if (game.ArtName is not null) return CachedFile(game.System, game.ArtName) ?? await RefetchAsync(game);
        if (!MayHaveArt(game.System, game.ArtCheckedUtc)) return null;
        var path = await MatchAsync(game, CancellationToken.None);
        await db.SaveChangesAsync();
        return path;
    }

    // Kept art that's gone from the folder (deleted by hand): fetched again under the name it was found under
    private async Task<string?> RefetchAsync(Game game)
    {
        if (!Enabled || !Folders.TryGetValue(game.System, out var folders)) return null;
        var index = await IndexAsync(game.System, CancellationToken.None);
        var folder = index?.Values.SelectMany(l => l).FirstOrDefault(x => x.Name == game.ArtName).Folder ?? folders[0];
        await _slots.WaitAsync();
        try { return await FetchAsync(game.System, folder, game.ArtName!, CancellationToken.None); }
        finally { _slots.Release(); }
    }

    /// <summary>Matches a game and fetches its art; sets ArtName and ArtCheckedUtc (not saved). The kept picture, or null.</summary>
    private async Task<string?> MatchAsync(Game game, CancellationToken ct)
    {
        var index = await IndexAsync(game.System, ct);
        if (index is null) return null;   // the list couldn't be read: looked for again next time
        var file = game.RelativePath[(game.RelativePath.LastIndexOf('/') + 1)..];
        string? path = null;
        if (index.TryGetValue(Key(Path.GetFileNameWithoutExtension(file)), out var candidates)
            && Pick(file, candidates.Select(c => c.Name)) is { } name)
        {
            await _slots.WaitAsync(ct);
            try { path = await FetchAsync(game.System, candidates.First(c => c.Name == name).Folder, name, ct); }
            // The site didn't answer, or the file couldn't be written: looked for again next time
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogDebug("Box art for {Game} not fetched: {Error}", game.Title, ex.Message);
                return null;
            }
            finally { _slots.Release(); }
            game.ArtName = path is null ? null : name;
        }
        game.ArtCheckedUtc = DateTime.UtcNow;
        return path;
    }

    // ── Background ──

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        scanner.Scanned += (_, _) => Wake();   // new games: their art
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PassAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Box art pass failed"); LastResult = "Failed: " + ex.Message; }
            finally { Progress = null; }
            await _wake.WaitAsync(TimeSpan.FromHours(6), stoppingToken);
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var systems = Folders.Keys.ToList();
        var waiting = await db.Games.Where(x => x.ImagePath == null && x.ArtCheckedUtc == null && systems.Contains(x.System))
            .OrderBy(x => x.System).ThenBy(x => x.Title).Select(x => x.Id).ToListAsync(ct);
        if (waiting.Count == 0) return;
        int done = 0, found = 0;
        foreach (var batch in waiting.Chunk(40))
        {
            var games = await db.Games.Where(x => batch.Contains(x.Id)).ToListAsync(ct);
            await Parallel.ForEachAsync(games, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (game, token) =>
            {
                if (game.ArtCheckedUtc is not null || game.ImagePath is not null) return;   // a tile got to it first
                if (await MatchAsync(game, token) is not null) Interlocked.Increment(ref found);
                Progress = $"Box art: {Interlocked.Increment(ref done)} of {waiting.Count} games looked up, {found} found";
            });
            await db.SaveChangesAsync(ct);
        }
        LastResult = $"Box art: {found} of {waiting.Count} games found ({DateTime.Now:g})";
        log.LogInformation("{Result}", LastResult);
    }

    /// <summary>How many games have art kept, how many have none to be found, and how many are waiting.</summary>
    public async Task<(int Found, int NotFound, int Waiting)> CountsAsync(AppDbContext db)
    {
        var systems = Folders.Keys.ToList();
        var games = db.Games.Where(x => x.ImagePath == null && systems.Contains(x.System));
        return (await games.CountAsync(x => x.ArtName != null), await games.CountAsync(x => x.ArtName == null && x.ArtCheckedUtc != null),
            await games.CountAsync(x => x.ArtCheckedUtc == null));
    }

    /// <summary>A wake-up signal that a waiter can also time out of.</summary>
    private sealed class Channel
    {
        private readonly SemaphoreSlim _signal = new(0, 1);
        public void Signal() { try { _signal.Release(); } catch (SemaphoreFullException) { } }
        public async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await _signal.WaitAsync(timeout, ct);
    }
}
