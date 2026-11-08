using MediaRiffle.Server.Data;
using MediaRiffle.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Games;

/// <summary>
/// Where users' game saves are kept: the data folder's gamesaves/, a folder per user and per game they've played
/// (GamePlay), and how much space each user's take against MediaRiffle:Games:SpacePerUserMB.
/// </summary>
public sealed class GameSaves(IOptions<MediaRiffleOptions> options)
{
    /// <summary>The most a single save may be (a memory card is 128 KB, the largest cartridge saves a few MB).</summary>
    public const long MaxSave = 32L * 1024 * 1024;

    string Root => options.Value.GameSaveDirectory;

    /// <summary>The space each user may take, in bytes; 0 for no limit.</summary>
    public long Limit => options.Value.Games.SpacePerUserMB * 1024L * 1024;

    public string Folder(string userId, int playId) => Path.Combine(Root, SafeName(userId), playId.ToString());

    public string SavePath(string userId, int playId) => Path.Combine(Folder(userId, playId), "save");

    /// <summary>The most a save state may be (a PSP's run to 40 MB or so).</summary>
    public const long MaxState = 256L * 1024 * 1024;

    /// <summary>The most a state's screenshot may be.</summary>
    public const long MaxPicture = 8L * 1024 * 1024;

    /// <summary>How many states a user keeps of each game; 0 for all.</summary>
    public int StatesPerGame => options.Value.Games.StatesPerGame;

    public string StatePath(string userId, int playId, int stateId) => Path.Combine(Folder(userId, playId), $"state-{stateId}");

    public string PicturePath(string userId, int playId, int stateId) => Path.Combine(Folder(userId, playId), $"state-{stateId}.pic");

    /// <summary>A state's files.</summary>
    public void DeleteState(string userId, int playId, int stateId)
    {
        File.Delete(StatePath(userId, playId, stateId));
        File.Delete(PicturePath(userId, playId, stateId));
    }

    /// <summary>A removed user's saves.</summary>
    public void DeleteUser(string userId)
    {
        var dir = Path.Combine(Root, SafeName(userId));
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (IOException) { }
    }

    /// <summary>How much a user's saves and states take, in bytes.</summary>
    public static async Task<long> UsedAsync(AppDbContext db, string userId)
    {
        var saves = await db.GamePlays.Where(p => p.UserId == userId).SumAsync(p => (long?)p.SaveSize) ?? 0;
        var states = await db.GameStates.Where(s => db.GamePlays.Any(p => p.Id == s.PlayId && p.UserId == userId))
            .SumAsync(s => (long?)(s.Size + s.PictureSize)) ?? 0;
        return saves + states;
    }

    /// <summary>Why a user can't keep <paramref name="adding"/> bytes more, or null when they can.</summary>
    public async Task<string?> OverLimitAsync(AppDbContext db, string userId, long adding)
    {
        if (Limit <= 0 || adding <= 0) return null;
        var used = await UsedAsync(db, userId);
        return used + adding <= Limit ? null
            : $"Your game saves and states would take more than the {options.Value.Games.SpacePerUserMB} MB you have " +
              $"({used / 1024 / 1024} MB used). Delete some save states on the game's page to make room.";
    }

    /// <summary>Writes a file whole or not at all (a half-written save is worse than the one before).</summary>
    public static async Task WriteAsync(string path, byte[] data, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".new";
        await File.WriteAllBytesAsync(temp, data, ct);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// FNV-1a, 32-bit, as hex: what the game page compares its save with the server's by (the page may be on plain
    /// HTTP, where browsers have no SHA-256), and keys its notes by.
    /// </summary>
    public static string Hash(ReadOnlySpan<byte> data)
    {
        uint h = 2166136261;
        foreach (var b in data) h = unchecked((h ^ b) * 16777619);
        return h.ToString("x8");
    }

    static string SafeName(string id) => string.Concat(id.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_'));
}
