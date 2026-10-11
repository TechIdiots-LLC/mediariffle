using System.Globalization;
using MediaRiffle.Server.Options;

namespace MediaRiffle.Server.Media;

public enum PlaybackMode
{
    /// <summary>The browser plays the file as it is, with its own seeking (byte ranges).</summary>
    Direct,
    /// <summary>The video is copied into HLS segments and only the audio is converted if it has to be: cheap.</summary>
    Remux,
    /// <summary>The video is re-encoded to H.264 (scaled down, subtitles burned in when asked).</summary>
    Transcode,
}

/// <param name="Audio">The audio stream to play, or null for none.</param>
/// <param name="BurnSubtitle">An image subtitle stream to burn into the video, or null.</param>
public sealed record PlaybackPlan(PlaybackMode Mode, ProbeStream? Video, ProbeStream? Audio, ProbeStream? BurnSubtitle, string Reason);

/// <summary>Decides how a file plays in a browser, and writes the ffmpeg arguments for it.</summary>
public static class PlaybackPlanner
{
    private static readonly HashSet<string> Mp4Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov" };

    /// <summary>H.264 that every browser and Chromecast decodes: 8-bit 4:2:0, not High 10 / 4:2:2 / 4:4:4.</summary>
    public static bool IsWebH264(ProbeStream v) =>
        v.Codec == "h264" &&
        (v.PixelFormat is null or "yuv420p" or "yuvj420p") &&
        (v.Profile is null || !(v.Profile.Contains("10") || v.Profile.Contains("4:2:2") || v.Profile.Contains("4:4:4")));

    public static PlaybackPlan Decide(ProbeResult probe, string extension, int? audioIndex, int? burnSubtitleIndex)
    {
        var video = probe.Video;
        var audio = audioIndex is { } ai ? probe.Audio.FirstOrDefault(a => a.Index == ai) : probe.DefaultAudio;
        var burn = burnSubtitleIndex is { } si ? probe.Subtitles.FirstOrDefault(s => s.Index == si && !s.IsTextSubtitle) : null;

        if (video is null)
            return new(PlaybackMode.Transcode, null, audio, null, "no video stream");
        if (burn is not null)
            return new(PlaybackMode.Transcode, video, audio, burn, "burning in image subtitles");

        // A browser plays the first audio track of a file; any other one needs HLS
        bool firstAudio = audio is null || audio.Index == probe.Audio.First().Index;
        if (firstAudio && Mp4Extensions.Contains(extension) && probe.FormatName.Contains("mp4") &&
            IsWebH264(video) && (audio is null || audio.Codec is "aac" or "mp3"))
            return new(PlaybackMode.Direct, video, audio, null, "MP4 with H.264");
        if (firstAudio && extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) &&
            video.Codec is "vp8" or "vp9" or "av1" && (audio is null || audio.Codec is "opus" or "vorbis"))
            return new(PlaybackMode.Direct, video, audio, null, "WebM");

        if (IsWebH264(video))
            return new(PlaybackMode.Remux, video, audio, null, $"H.264 in {probe.FormatName}: video copied");
        return new(PlaybackMode.Transcode, video, audio, null, $"{video.Codec} video needs converting");
    }

    /// <summary>
    /// ffmpeg arguments that write an HLS event playlist (index.m3u8) and its MPEG-TS segments into
    /// <paramref name="outputDir"/>, starting <paramref name="startSeconds"/> into the file. Seeking further restarts
    /// with a new start. Segments are written to a temporary name and renamed, so a listed segment is complete.
    /// </summary>
    public static List<string> HlsArguments(PlaybackPlan plan, string input, double startSeconds, string outputDir, TranscodingOptions o)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        bool transcode = plan.Mode == PlaybackMode.Transcode;
        if (transcode && o.Encoder == VideoEncoder.Vaapi) a.AddRange(["-vaapi_device", o.VaapiDevice]);
        if (startSeconds > 0) a.AddRange(["-ss", Seconds(startSeconds)]);
        a.AddRange(["-i", input]);

        if (plan.Video is { } v)
        {
            if (!transcode)
            {
                a.AddRange(["-map", $"0:{v.Index}", "-c:v", "copy"]);
            }
            else
            {
                bool scale = v.Height > o.MaxHeight;
                var chain = o.Encoder switch
                {
                    // VA-API scales on the GPU, after the frames are uploaded to it
                    VideoEncoder.Vaapi => "format=nv12,hwupload" + (scale ? $",scale_vaapi=w=-2:h={o.MaxHeight}" : ""),
                    VideoEncoder.Qsv => (scale ? $"scale=-2:{o.MaxHeight}," : "") + "format=nv12",
                    _ => (scale ? $"scale=-2:{o.MaxHeight}," : "") + "format=yuv420p",
                };
                if (plan.BurnSubtitle is { } s)
                {
                    a.AddRange(["-filter_complex", $"[0:{v.Index}][0:{s.Index}]overlay,{chain}[v]", "-map", "[v]"]);
                }
                else
                {
                    a.AddRange(["-map", $"0:{v.Index}", "-vf", chain]);
                }
                a.AddRange(EncoderArguments(o));
                // A keyframe at every segment boundary, so each segment starts cleanly
                a.AddRange(["-force_key_frames", $"expr:gte(t,n_forced*{o.SegmentSeconds})"]);
            }
        }

        if (plan.Audio is { } au)
        {
            a.AddRange(["-map", $"0:{au.Index}"]);
            if (au.Codec == "aac" && au.Channels is > 0 and <= 2) a.AddRange(["-c:a", "copy"]);
            else a.AddRange(["-c:a", "aac", "-b:a", "192k", "-ac", "2"]);
        }

        a.AddRange([
            "-sn", "-dn", "-map_metadata", "-1", "-max_muxing_queue_size", "4096",
            "-f", "hls",
            "-hls_time", o.SegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_list_size", "0",
            "-hls_playlist_type", "event",
            "-hls_segment_type", "mpegts",
            "-hls_flags", "independent_segments+temp_file",
            "-hls_segment_filename", Path.Combine(outputDir, "seg%05d.ts"),
            Path.Combine(outputDir, "index.m3u8"),
        ]);
        return a;
    }

    private static IEnumerable<string> EncoderArguments(TranscodingOptions o)
    {
        var max = $"{o.MaxVideoKbps}k";
        var buf = $"{o.MaxVideoKbps * 2}k";
        return o.Encoder switch
        {
            VideoEncoder.Nvenc => ["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "23", "-maxrate", max, "-bufsize", buf, "-profile:v", "high"],
            VideoEncoder.Qsv => ["-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "23", "-maxrate", max, "-bufsize", buf, "-profile:v", "high"],
            VideoEncoder.Vaapi => ["-c:v", "h264_vaapi", "-rc_mode", "VBR", "-b:v", $"{o.MaxVideoKbps * 3 / 4}k", "-maxrate", max, "-bufsize", buf, "-profile:v", "high"],
            _ => ["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-maxrate", max, "-bufsize", buf, "-profile:v", "high", "-level:v", "4.1"],
        };
    }

    /// <summary>
    /// ffmpeg arguments that write one subtitle stream (or a side-car file) as WebVTT to standard output, on the
    /// file's own clock (-copyts: never moved to start at zero). <see cref="WebVtt.Shift"/> fits it to a stream.
    /// </summary>
    public static List<string> WebVttArguments(string input, int? streamIndex) =>
        ["-hide_banner", "-loglevel", "error", "-copyts", "-i", input,
         "-map", streamIndex is { } i ? $"0:{i}" : "0:s:0", "-c:s", "webvtt", "-f", "webvtt", "pipe:1"];

    /// <summary>ffmpeg arguments for a 480 px wide JPEG of the frame at <paramref name="atSeconds"/>.</summary>
    public static List<string> ThumbnailArguments(string input, double atSeconds, string output) =>
        ["-hide_banner", "-loglevel", "error", "-y", "-ss", Seconds(atSeconds), "-i", input,
         "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "4", output];

    private static string Seconds(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);
}
