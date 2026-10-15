using System.Collections.Concurrent;
using MediaRiffle.Server.Options;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Media;

/// <summary>What audio this browser plays besides MP3, AAC and WAV, which all do.</summary>
public sealed record AudioCaps(bool Flac, bool Ogg, bool Opus)
{
    public static readonly AudioCaps Basic = new(false, false, false);
}

/// <summary>
/// Music for the player. A file the browser plays is sent as it is; any other (WMA, ALAC, APE, …, or FLAC/Ogg where
/// the browser lacks them) is converted once to AAC and kept, so it seeks like a file. The kept files are dropped,
/// oldest used first, past <see cref="MaxCacheBytes"/>.
/// </summary>
public sealed class AudioService(IOptions<MediaRiffleOptions> options, Ffmpeg ffmpeg, ILogger<AudioService> log)
{
    public const long MaxCacheBytes = 2L * 1024 * 1024 * 1024;
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly ConcurrentDictionary<string, Task<string>> _making = new();

    /// <summary>Whether the browser plays the file as it is, from its container and first audio stream.</summary>
    public static bool PlaysAsIs(ProbeResult probe, string extension, AudioCaps caps)
    {
        if (probe.Video is not null) return false;
        var codec = probe.DefaultAudio?.Codec;
        var format = probe.FormatName;
        var ext = extension.ToLowerInvariant();
        return codec switch
        {
            "mp3" => ext == ".mp3",
            "aac" => (ext is ".m4a" or ".m4b" or ".mp4" && format.Contains("mp4")) || (ext == ".aac" && format == "aac"),
            "flac" => caps.Flac && ext == ".flac",
            "vorbis" => caps.Ogg && ext is ".ogg" or ".oga",
            "opus" => caps.Opus && ext is ".ogg" or ".oga" or ".opus",
            "pcm_s16le" or "pcm_s24le" or "pcm_u8" => ext == ".wav",
            _ => false,
        };
    }

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".aac" => "audio/aac",
        ".flac" => "audio/flac",
        ".ogg" or ".oga" or ".opus" => "audio/ogg",
        ".wav" => "audio/wav",
        _ => "audio/mp4",
    };

    /// <summary>The file converted to AAC (.m4a): made on first request, then kept.</summary>
    public Task<string> ConvertedAsync(int fileId, DateTime modifiedUtc, string path)
    {
        var output = Path.Combine(options.Value.AudioDirectory, $"{fileId}-{modifiedUtc.Ticks}.m4a");
        if (File.Exists(output))
        {
            // Last used, for dropping the oldest
            try { File.SetLastWriteTimeUtc(output, DateTime.UtcNow); } catch (IOException) { }
            return Task.FromResult(output);
        }
        return _making.GetOrAdd(output, _ => MakeAsync());

        async Task<string> MakeAsync()
        {
            await _slots.WaitAsync();
            try
            {
                Directory.CreateDirectory(options.Value.AudioDirectory);
                var temp = output + ".tmp.m4a";
                await ffmpeg.RunAsync(ffmpeg.FfmpegPath, Arguments(path, temp), TimeSpan.FromMinutes(20));
                File.Move(temp, output, overwrite: true);
                Trim();
                return output;
            }
            finally
            {
                _slots.Release();
                _making.TryRemove(output, out _);
            }
        }
    }

    /// <summary>ffmpeg arguments converting a music file's first audio stream to 256 kbit/s stereo AAC, ready to seek.</summary>
    public static List<string> Arguments(string input, string output) =>
    [
        "-nostdin", "-y", "-v", "error", "-i", input,
        "-map", "0:a:0", "-vn", "-sn", "-dn", "-map_metadata", "-1",
        "-c:a", "aac", "-b:a", "256k", "-ac", "2",
        "-movflags", "+faststart", "-f", "mp4", output,
    ];

    private void Trim()
    {
        try
        {
            var files = new DirectoryInfo(options.Value.AudioDirectory).GetFiles("*.m4a")
                .Where(f => !f.Name.EndsWith(".tmp.m4a")).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > MaxCacheBytes) f.Delete();
            }
        }
        catch (IOException ex) { log.LogWarning(ex, "Couldn't trim the converted music folder"); }
    }
}
