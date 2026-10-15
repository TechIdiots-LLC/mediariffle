using System.Collections.Concurrent;
using MediaRiffle.Server.Options;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Media;

/// <summary>
/// Pictures for the library: art beside the media (poster.jpg, folder.jpg, …) when there is some, otherwise a
/// frame taken from the video. Frames are made on first request and kept in the image folder.
/// </summary>
public sealed class ImageService(IOptions<MediaRiffleOptions> options, Ffmpeg ffmpeg, ILogger<ImageService> log)
{
    private static readonly string[] ArtNames = ["poster", "folder", "cover", "show", "movie"];
    private static readonly string[] ArtExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    private readonly SemaphoreSlim _ffmpegSlots = new(2, 2);
    private readonly ConcurrentDictionary<string, Task<string?>> _making = new();

    /// <summary>Art in <paramref name="folder"/> (poster.jpg and the like), or null.</summary>
    public static string? FolderArt(string? folder)
    {
        if (folder is null || !Directory.Exists(folder)) return null;
        foreach (var name in ArtNames)
            foreach (var ext in ArtExtensions)
            {
                var path = Path.Combine(folder, name + ext);
                if (File.Exists(path)) return path;
            }
        return null;
    }

    /// <summary>Art named after the video (Movie.jpg / Movie-poster.jpg beside Movie.mkv), or null.</summary>
    public static string? FileArt(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath)!;
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        foreach (var suffix in new[] { "", "-poster", ".poster", "-thumb" })
            foreach (var ext in ArtExtensions)
            {
                var path = Path.Combine(dir, stem + suffix + ext);
                if (File.Exists(path)) return path;
            }
        return null;
    }

    /// <summary>A frame from the video, made once and cached; null when ffmpeg can't make one.</summary>
    public Task<string?> FrameAsync(int fileId, string videoPath, double durationSeconds)
    {
        var output = Path.Combine(options.Value.ImageDirectory, $"frame{Version}-{fileId}.jpg");
        if (File.Exists(output)) return Task.FromResult<string?>(output);
        return _making.GetOrAdd(output, _ => MakeAsync());

        async Task<string?> MakeAsync()
        {
            await _ffmpegSlots.WaitAsync();
            try
            {
                var at = FrameTime(durationSeconds);
                var temp = output + ".tmp.jpg";
                await ffmpeg.RunAsync(ffmpeg.FfmpegPath, PlaybackPlanner.ThumbnailArguments(videoPath, at, temp), TimeSpan.FromSeconds(60));
                File.Move(temp, output, overwrite: true);
                return output;
            }
            catch (Exception ex)
            {
                log.LogDebug("No frame for file {Id}: {Error}", fileId, ex.Message);
                return null;
            }
            finally
            {
                _ffmpegSlots.Release();
                _making.TryRemove(output, out _);
            }
        }
    }

    /// <summary>
    /// The cover art inside a music file (an attached picture), as a JPEG at most 800 wide, made once and kept (named by
    /// the file's modified time, so retagging gives new art); null when there's none or ffmpeg can't read it.
    /// </summary>
    public Task<string?> CoverAsync(int fileId, DateTime modifiedUtc, string audioPath)
    {
        var output = Path.Combine(options.Value.ImageDirectory, $"cover-{fileId}-{modifiedUtc.Ticks}.jpg");
        if (File.Exists(output)) return Task.FromResult<string?>(output);
        return _making.GetOrAdd(output, _ => MakeAsync());

        async Task<string?> MakeAsync()
        {
            await _ffmpegSlots.WaitAsync();
            try
            {
                var temp = output + ".tmp.jpg";
                await ffmpeg.RunAsync(ffmpeg.FfmpegPath, CoverArguments(audioPath, temp), TimeSpan.FromSeconds(30));
                File.Move(temp, output, overwrite: true);
                return output;
            }
            catch (Exception ex)
            {
                log.LogDebug("No cover art in file {Id}: {Error}", fileId, ex.Message);
                return null;
            }
            finally
            {
                _ffmpegSlots.Release();
                _making.TryRemove(output, out _);
            }
        }
    }

    /// <summary>ffmpeg arguments writing a file's attached picture as a JPEG, scaled down to at most 800 wide.</summary>
    public static List<string> CoverArguments(string input, string output) =>
        ["-hide_banner", "-loglevel", "error", "-y", "-i", input,
         "-map", "0:v:0", "-frames:v", "1", "-vf", "scale='min(800,iw)':-2", "-q:v", "3", output];

    /// <summary>Drops cached frames, e.g. after a file changed.</summary>
    public void Forget(int fileId)
    {
        try { File.Delete(Path.Combine(options.Value.ImageDirectory, $"frame{Version}-{fileId}.jpg")); } catch { }
    }

    /// <summary>
    /// Bumped when frames are made differently: cached ones are made again, and the ?v= in picture URLs tells
    /// browsers to fetch them anew. 2: frames from minutes in, through the thumbnail filter (version 1 took early,
    /// often black, frames, and at 0:00 for files whose length wasn't known).
    /// </summary>
    public const int Version = 2;

    /// <summary>
    /// Where to take a frame: past the opening (black screens, logos, titles), but not late enough to spoil anything.
    /// 10% in for anything over ten minutes, never before 3:00; a third in for shorter clips; 1:00 when the length
    /// isn't known (ffmpeg stops at the end if the file is shorter).
    /// </summary>
    public static double FrameTime(double durationSeconds) =>
        durationSeconds <= 0 ? 60
        : durationSeconds > 600 ? Math.Min(Math.Max(180, durationSeconds * 0.1), durationSeconds * 0.4)
        : durationSeconds / 3;

    /// <summary>Removes frames made by earlier versions, once at start.</summary>
    public void RemoveOldFrames()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(options.Value.ImageDirectory, "frame*.jpg"))
                if (!Path.GetFileName(f).StartsWith($"frame{Version}-", StringComparison.Ordinal)) File.Delete(f);
        }
        catch (Exception ex) { log.LogDebug("Couldn't remove old frames: {Error}", ex.Message); }
    }
}
