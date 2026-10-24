using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaRiffle.Server.Options;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Media;

/// <summary>A subtitle track a file has that can be kept: PGS (drawn by the browser, libpgs) or text (WebVTT).</summary>
public sealed record SubtitleTrack(int Stream, bool Pgs)
{
    public static IReadOnlyList<SubtitleTrack> Of(ProbeResult probe) =>
        probe.Subtitles.Where(s => s.IsPgs || s.IsTextSubtitle).Select(s => new SubtitleTrack(s.Index, s.IsPgs)).ToList();
}

/// <summary>
/// A file's subtitle tracks, extracted once and kept in the data folder's subtitles/: PGS (Blu-ray) ones as .sup for the
/// player to draw (libpgs), text ones as WebVTT. Getting a track out of a file means reading all of it, as its packets
/// lie among the video's: minutes for a 40 GB disc rip, so every track of a file comes out of one read, ahead of time
/// (SubtitlePreparer) or the first time one is asked for. Until then a PGS track is handed out five minutes at a time
/// (<see cref="WindowAsync"/>), which ffmpeg reads from that point only.
/// </summary>
public sealed class SubtitleCache(Ffmpeg ffmpeg, IOptions<MediaRiffleOptions> options, ILogger<SubtitleCache> log)
{
    /// <summary>How much a PGS window holds, from where it's asked for.</summary>
    public const int WindowSeconds = 300;

    private readonly ConcurrentDictionary<string, Task> _extracting = new();
    private readonly SemaphoreSlim _windows = new(2, 2);

    private string Folder => Path.Combine(options.Value.ResolvedDataDirectory, "subtitles");
    /// <summary>What the cache is kept to: SubtitleCacheGB (a film's PGS track is usually 20–50 MB).</summary>
    public long CapBytes => (long)(Math.Max(0.1, options.Value.SubtitleCacheGB) * (1L << 30));

    /// <summary>
    /// Where a track is kept: per file (its path, hashed, and its last change, so a replaced file isn't served an old
    /// track) and stream.
    /// </summary>
    public string PathFor(string file, SubtitleTrack track)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file)))[..16];
        return Path.Combine(Folder, $"{key}-{File.GetLastWriteTimeUtc(file).Ticks}-{track.Stream}.{(track.Pgs ? "sup" : "vtt")}");
    }

    /// <summary>The kept track, or null (not extracted yet).</summary>
    public string? Cached(string file, SubtitleTrack track)
    {
        var path = PathFor(file, track);
        if (!File.Exists(path)) return null;
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { }   // trimmed last
        return path;
    }

    /// <summary>Whether every one of <paramref name="tracks"/> is kept.</summary>
    public bool Has(string file, IReadOnlyList<SubtitleTrack> tracks) => tracks.All(t => File.Exists(PathFor(file, t)));

    /// <summary>Starts extracting the file's tracks that aren't kept (one read of the file for all of them), unless that's under way.</summary>
    public void Start(string file, IReadOnlyList<SubtitleTrack> tracks) => _ = ExtractAsync(file, tracks);

    /// <summary>Extracts the file's tracks that aren't kept, in one read; joins one already under way for the file.</summary>
    public Task ExtractAsync(string file, IReadOnlyList<SubtitleTrack> tracks)
    {
        var missing = tracks.Where(t => !File.Exists(PathFor(file, t))).ToList();
        if (missing.Count == 0) return Task.CompletedTask;
        return _extracting.GetOrAdd(file, f => Task.Run(() => RunAsync(f, missing)));
    }

    /// <summary>
    /// One ffmpeg, an output per track: PGS copied as it is, text converted to WebVTT (with -copyts, as the stream's own
    /// WebVTT always was, so the player shifts both the same way). Each written beside its place and moved in when done.
    /// </summary>
    public static List<string> ExtractArguments(string file, IReadOnlyList<(SubtitleTrack Track, string Output)> outputs)
    {
        var a = new List<string> { "-hide_banner", "-v", "error", "-y", "-copyts", "-i", file };
        foreach (var (t, output) in outputs)
            a.AddRange(t.Pgs
                ? ["-map", $"0:{t.Stream.ToString(CultureInfo.InvariantCulture)}", "-c:s", "copy", "-f", "sup", output]
                : ["-map", $"0:{t.Stream.ToString(CultureInfo.InvariantCulture)}", "-c:s", "webvtt", "-f", "webvtt", output]);
        return a;
    }

    /// <summary>A PGS track's subtitles from <paramref name="from"/> (seconds into the file) for <see cref="WindowSeconds"/>.</summary>
    public async Task<byte[]> WindowAsync(string file, int stream, double from, CancellationToken ct)
    {
        await _windows.WaitAsync(ct);
        try { return await ffmpeg.RunAsync(ffmpeg.FfmpegPath, WindowArguments(file, stream, from), TimeSpan.FromMinutes(2), ct); }
        finally { _windows.Release(); }
    }

    /// <summary>
    /// A window: read from <paramref name="from"/> only (-ss before -i), the track copied as it is, its timestamps moved
    /// back onto the file's clock (-ss makes them count from there).
    /// </summary>
    public static List<string> WindowArguments(string file, int stream, double from) =>
    [
        "-hide_banner", "-v", "error",
        "-ss", from.ToString("0.###", CultureInfo.InvariantCulture), "-t", WindowSeconds.ToString(CultureInfo.InvariantCulture),
        "-i", file, "-map", $"0:{stream.ToString(CultureInfo.InvariantCulture)}", "-c:s", "copy",
        "-output_ts_offset", from.ToString("0.###", CultureInfo.InvariantCulture), "-f", "sup", "pipe:1",
    ];

    /// <summary>How much the cache holds now.</summary>
    public long Size()
    {
        try { return Directory.Exists(Folder) ? new DirectoryInfo(Folder).GetFiles().Sum(f => f.Length) : 0; }
        catch (IOException) { return 0; }
    }

    private async Task RunAsync(string file, List<SubtitleTrack> tracks)
    {
        var outputs = tracks.Select(t => (Track: t, Output: PathFor(file, t) + ".part")).ToList();
        try
        {
            Directory.CreateDirectory(Folder);
            var started = DateTime.UtcNow;
            // Long for a big file over a share; nothing waits on it but a player that already has a window
            await ffmpeg.RunAsync(ffmpeg.FfmpegPath, ExtractArguments(file, outputs), TimeSpan.FromHours(1));
            foreach (var (t, part) in outputs) File.Move(part, PathFor(file, t), overwrite: true);
            log.LogInformation("Subtitles of {File}: {Tracks} track(s) kept, in {Seconds:0} s", Path.GetFileName(file), tracks.Count,
                (DateTime.UtcNow - started).TotalSeconds);
            Trim();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Couldn't extract the subtitles of {File}: {Error}", Path.GetFileName(file), ex.Message);
            foreach (var (_, part) in outputs) try { File.Delete(part); } catch (IOException) { }
        }
        finally
        {
            _extracting.TryRemove(file, out _);   // a failure is tried again at the next ask
        }
    }

    /// <summary>Back under the cap, the tracks used longest ago first.</summary>
    private void Trim()
    {
        try
        {
            long total = 0;
            foreach (var f in new DirectoryInfo(Folder).GetFiles().Where(f => f.Extension is ".sup" or ".vtt").OrderByDescending(f => f.LastAccessTimeUtc))
            {
                total += f.Length;
                if (total > CapBytes) f.Delete();
            }
        }
        catch (IOException) { }
    }
}
