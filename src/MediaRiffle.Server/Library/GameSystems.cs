using System.Text.RegularExpressions;

namespace MediaRiffle.Server.Library;

/// <summary>
/// A console or computer whose games a Games library holds. <see cref="Id"/> is EmulatorJS's name for it (EJS_core), and
/// <see cref="Core"/> the EmulatorJS core that plays it in the browser (null when no browser emulator runs it: its
/// games are listed all the same, so that opening one can say so).
/// </summary>
public sealed record GameSystem(
    string Id, string Name,
    // Its own extensions: a file with one of these is its game, wherever it is
    string[] Extensions,
    // Ones other systems or other files use too (.zip, .bin, .cue): its game only in a folder named for it
    string[] Shared,
    // Folder names for it, in lower case letters and digits only ("Sega Genesis" is segagenesis, sega and genesis)
    string[] Folders,
    string? Core,
    // A BIOS file the core can't do without (null: none, or it has its own)
    string? Bios = null);

/// <summary>
/// Which system a file in a Games library is for, from its extension and the folders it's in: the same rules as 360
/// Riffle's emulator lists, so one games folder serves both.
/// </summary>
public static partial class GameSystems
{
    public static readonly IReadOnlyList<GameSystem> All =
    [
        new("nes", "NES", [".nes", ".fds", ".unf", ".unif"], [".zip", ".7z"], ["nes", "famicom", "nintendoentertainmentsystem", "famicomdisksystem", "fds"], "fceumm"),
        new("snes", "SNES", [".sfc", ".smc", ".fig", ".swc", ".gd3", ".gd7", ".dx2", ".bsx"], [".zip", ".7z"], ["snes", "superfamicom", "supernintendo", "supernes", "sfc"], "snes9x"),
        new("segaMD", "Genesis", [".gen", ".smd", ".mdx", ".68k", ".sgd"], [".zip", ".7z", ".bin", ".md"], ["genesis", "megadrive", "segagenesis", "segamegadrive", "md"], "genesis_plus_gx"),
        new("segaMS", "Master System", [".sms", ".sg", ".bms"], [".zip", ".7z"], ["mastersystem", "segamastersystem", "sms", "sg1000"], "genesis_plus_gx"),
        new("segaGG", "Game Gear", [".gg"], [".zip", ".7z"], ["gamegear", "segagamegear", "gg"], "genesis_plus_gx"),
        new("segaCD", "Sega CD", [], [".cue", ".chd", ".iso", ".m3u"], ["segacd", "megacd"], "genesis_plus_gx", "bios_CD_U.bin"),
        new("sega32x", "32X", [".32x"], [".zip", ".7z"], ["32x", "sega32x"], "picodrive"),
        new("gb", "Game Boy", [".gb"], [".zip", ".7z", ".dmg"], ["gb", "gameboy"], "gambatte"),
        new("gbc", "Game Boy Color", [".gbc"], [".zip", ".7z"], ["gbc", "gameboycolor"], "gambatte"),
        new("gba", "Game Boy Advance", [".gba"], [".zip", ".7z"], ["gba", "gameboyadvance"], "mgba"),
        new("n64", "Nintendo 64", [".z64", ".n64", ".v64"], [".zip", ".7z"], ["n64", "nintendo64"], "mupen64plus_next"),
        new("nds", "Nintendo DS", [".nds"], [".zip", ".7z"], ["ds", "nds", "nintendods"], "melonds"),
        // .pbp is a PS1 game converted for the PSP, or a PSP program's EBOOT: its folder says which
        new("psx", "PlayStation", [".cbn"], [".cue", ".bin", ".img", ".iso", ".chd", ".m3u", ".mdf", ".pbp", ".zip", ".7z"], ["psx", "ps1", "psone", "playstation", "playstation1", "sonyplaystation"], "pcsx_rearmed"),
        new("psp", "PSP", [".cso"], [".iso", ".pbp", ".zip", ".7z"], ["psp", "playstationportable"], "ppsspp"),
        new("pce", "PC Engine", [".pce"], [".zip", ".7z"], ["pce", "pcengine", "turbografx", "turbografx16", "tg16"], "mednafen_pce"),
        new("pcecd", "PC Engine CD", [], [".cue", ".chd", ".m3u"], ["pcecd", "pcenginecd", "turbografxcd", "tgcd"], "mednafen_pce", "syscard3.pce"),
        new("ngp", "Neo Geo Pocket", [".ngp", ".ngc", ".ngpc", ".npc"], [".zip", ".7z"], ["ngp", "ngpc", "neogeopocket", "neogeopocketcolor"], "mednafen_ngp"),
        new("ws", "WonderSwan", [".ws", ".wsc"], [".zip", ".7z"], ["wonderswan", "wonderswancolor", "ws", "wsc"], "mednafen_wswan"),
        new("lynx", "Atari Lynx", [".lnx", ".lyx"], [".zip", ".7z"], ["lynx", "atarilynx"], "handy", "lynxboot.img"),
        new("vb", "Virtual Boy", [".vboy"], [".zip", ".7z", ".vb"], ["virtualboy", "vb"], "beetle_vb"),
        new("atari2600", "Atari 2600", [".a26"], [".zip", ".7z", ".bin"], ["atari2600", "2600", "vcs"], "stella2014"),
        new("atari5200", "Atari 5200", [".a52"], [".zip", ".7z", ".bin"], ["atari5200", "5200"], "a5200", "5200.rom"),
        new("atari7800", "Atari 7800", [".a78"], [".zip", ".7z", ".bin"], ["atari7800", "7800"], "prosystem"),
        new("jaguar", "Atari Jaguar", [".j64", ".jag"], [".zip", ".7z"], ["jaguar", "atarijaguar"], "virtualjaguar"),
        // .col is also other programs' data (GTA's collision files), so only in a ColecoVision folder
        new("coleco", "ColecoVision", [], [".col", ".zip", ".7z"], ["coleco", "colecovision"], "gearcoleco", "colecovision.rom"),
        new("segaSaturn", "Saturn", [], [".cue", ".chd", ".iso", ".m3u"], ["saturn", "segasaturn"], "yabause"),
        new("arcade", "Arcade", [], [".zip", ".7z"], ["arcade", "fbneo", "finalburnneo", "mame", "neogeo", "cps1", "cps2"], "fbneo"),

        // No browser emulator for these: listed, but not playable here
        new("3ds", "Nintendo 3DS", [".3ds", ".cia"], [".zip"], ["3ds", "nintendo3ds"], null),
        new("ps2", "PlayStation 2", [], [".iso", ".bin", ".cue", ".chd"], ["ps2", "playstation2"], null),
        new("gamecube", "GameCube", [".gcm", ".rvz"], [".iso"], ["gamecube", "nintendogamecube", "gc", "ngc"], null),
        new("wii", "Wii", [".wbfs"], [".iso"], ["wii", "nintendowii"], null),
        new("dreamcast", "Dreamcast", [".cdi", ".gdi"], [".chd"], ["dreamcast", "segadreamcast", "dc"], null),
    ];

    private static readonly Dictionary<string, GameSystem> ById = All.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

    public static GameSystem? Get(string? id) => id is not null && ById.TryGetValue(id, out var s) ? s : null;

    // Folders whose files aren't games even with a game's extension: BIOS dumps, emulators kept next to the games,
    // pictures, manuals and other emulators' saves
    private static readonly HashSet<string> NotGames = ["bios", "firmware", "emulator", "emulators", "emulateur", "emulateurs",
        "boxart", "boxarts", "covers", "snaps", "screenshots", "manuals", "saves", "states"];

    /// <summary>
    /// The system a file is a game for, from its path relative to the library, or null when it isn't one. Its own
    /// extension decides first; a shared one (.zip, .bin, .cue…) only counts in a folder named for a system: the
    /// nearest such folder, by its whole name, then two words of it, then one ("PlayStation 2" is that before it's
    /// "PlayStation").
    /// </summary>
    public static GameSystem? Classify(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var folders = parts[..^1];
        if (folders.Any(f => Words(f).Any(NotGames.Contains))) return null;

        var ext = Path.GetExtension(parts[^1]).ToLowerInvariant();
        if (ext.Length == 0) return null;
        if (All.FirstOrDefault(s => s.Extensions.Contains(ext)) is { } own) return own;
        if (!All.Any(s => s.Shared.Contains(ext))) return null;

        for (var i = folders.Length - 1; i >= 0; i--)
        {
            var words = Words(folders[i]);
            if (words.Count == 0) continue;
            var names = new List<string> { string.Concat(words) };
            for (var j = 0; j + 1 < words.Count; j++) names.Add(words[j] + words[j + 1]);
            names.AddRange(words);
            foreach (var name in names)
                if (All.FirstOrDefault(s => s.Shared.Contains(ext) && s.Folders.Contains(name)) is { } bySystem) return bySystem;
        }
        return null;
    }

    /// <summary>The words of a folder's name, in lower case: "SEGA_GENESIS" is sega, genesis.</summary>
    private static List<string> Words(string folder) =>
        WordRun().Matches(folder).Select(m => m.Value.ToLowerInvariant()).ToList();

    [GeneratedRegex("[A-Za-z0-9]+")]
    private static partial Regex WordRun();

    /// <summary>
    /// A game's name from its file name, without the extension and the "[!]" (a verified good dump) GoodTools sets
    /// put on most; the region and the other tags ([a1], [h1]…) stay, as they tell versions apart.
    /// </summary>
    public static string Title(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var title = Spaces().Replace(name.Replace("[!]", " "), " ").Trim();
        return title.Length > 0 ? title : name;
    }

    private static bool IsArchive(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);

    private static string Stem(string path) => path[..^Path.GetExtension(path).Length];

    /// <summary>
    /// Archives with the same game beside them unpacked ("Game.zip" next to "Game.nds"): the game is listed once, as
    /// the unpacked file.
    /// </summary>
    public static HashSet<string> Repacked(IEnumerable<string> games)
    {
        var list = games.ToList();
        var unpacked = list.Where(g => !IsArchive(g)).Select(Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return list.Where(g => IsArchive(g) && unpacked.Contains(Stem(g))).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Folders a program is installed in (an emulator kept next to the games, with its own cheats and resources):
    /// those holding an .exe or a .dll but no file that's a game by its own extension. Ending in "/"; nothing under
    /// them is a game.
    /// </summary>
    public static HashSet<string> ProgramFolders(IEnumerable<string> files)
    {
        var programs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var withGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var slash = file.LastIndexOf('/');
            var dir = slash < 0 ? "" : file[..(slash + 1)];
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".exe" or ".dll") programs.Add(dir);
            else if (All.Any(s => s.Extensions.Contains(ext))) withGames.Add(dir);
        }
        programs.ExceptWith(withGames);
        programs.Remove("");   // the library's own folder is never skipped as a whole
        return programs;
    }

    /// <summary>Whether a file is in one of <paramref name="folders"/> (as from <see cref="ProgramFolders"/>), or below one.</summary>
    public static bool IsUnder(string file, HashSet<string> folders)
    {
        if (folders.Count == 0) return false;
        for (var i = file.IndexOf('/'); i >= 0; i = file.IndexOf('/', i + 1))
            if (folders.Contains(file[..(i + 1)])) return true;
        return false;
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    /// <summary>
    /// The files a disc game's list names: a .cue's tracks ("FILE "Game (Track 1).bin" BINARY") or an .m3u's discs, as
    /// paths relative to the list's folder. They're played through the list, so they aren't games of their own.
    /// </summary>
    public static IEnumerable<string> PartsOf(string listPath)
    {
        var ext = Path.GetExtension(listPath).ToLowerInvariant();
        if (ext is not (".cue" or ".m3u")) yield break;
        string[] lines;
        try
        {
            // A list is a few lines; anything big isn't one
            if (new FileInfo(listPath).Length > 64 * 1024) yield break;
            lines = File.ReadAllLines(listPath);
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (ext == ".m3u")
            {
                if (line.Length > 0 && !line.StartsWith('#')) yield return line.Replace('\\', '/');
            }
            else if (CueFile().Match(line) is { Success: true } m)
                yield return m.Groups["name"].Value.Replace('\\', '/');
        }
    }

    [GeneratedRegex("^FILE\\s+(?:\"(?<name>[^\"]+)\"|(?<name>\\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex CueFile();

    private static readonly string[] PictureExtensions = [".png", ".jpg", ".jpeg", ".webp"];
    private static readonly string[] PictureFolders = ["", "boxart", "boxarts", "Named_Boxarts", "covers", "images", "media/images", "media/box2dfront"];

    /// <summary>
    /// A picture for the game: one named like it beside it, or in a boxart / covers / images folder there (also
    /// libretro-thumbnails' Named_Boxarts and EmulationStation's media folders). <paramref name="files"/> is every file
    /// in the library (relative, forward slashes), so nothing more is read from the disk. Null when there's none.
    /// </summary>
    public static string? FindPicture(string relativePath, HashSet<string> files)
    {
        var slash = relativePath.LastIndexOf('/');
        var dir = slash < 0 ? "" : relativePath[..(slash + 1)];
        var name = Path.GetFileNameWithoutExtension(relativePath);
        foreach (var folder in PictureFolders)
            foreach (var ext in PictureExtensions)
            {
                var candidate = dir + (folder.Length > 0 ? folder + "/" : "") + name + ext;
                if (files.TryGetValue(candidate, out var actual)) return actual;
            }
        return null;
    }

    /// <summary>
    /// The system of a zip whose folders don't say: the one whose own extension its games have, when they're all one
    /// system's. Null when they aren't, or it can't be read.
    /// </summary>
    public static GameSystem? FromZip(string path)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var systems = zip.Entries.Where(e => e.Length > 0)
                .Select(e => Path.GetExtension(e.Name).ToLowerInvariant())
                .Select(ext => All.FirstOrDefault(s => s.Extensions.Contains(ext)))
                .Where(s => s is not null).Distinct().ToList();
            return systems.Count == 1 ? systems[0] : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }
}
