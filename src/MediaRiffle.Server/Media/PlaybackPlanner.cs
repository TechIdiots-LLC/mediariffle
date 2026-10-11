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

public enum SegmentFormat
{
    /// <summary>MPEG-TS segments: H.264, played everywhere.</summary>
    Ts,
    /// <summary>Fragmented MP4 segments (init.mp4 + .m4s): needed for HEVC in browsers.</summary>
    Fmp4,
}

/// <param name="Audio">The audio stream to play, or null for none.</param>
/// <param name="BurnSubtitle">An image subtitle stream to burn into the video, or null.</param>
/// <param name="Quality">The quality this plays at: "original", or a <see cref="Quality.Ladder"/> id.</param>
/// <param name="TargetHeight">A transcode's height, at most.</param>
/// <param name="TargetVideoKbps">A transcode's peak video bitrate.</param>
/// <param name="StreamKbps">About how much the stream needs (the file's bitrate when it isn't transcoded), for Auto.</param>
public sealed record PlaybackPlan(
    PlaybackMode Mode, ProbeStream? Video, ProbeStream? Audio, ProbeStream? BurnSubtitle, string Reason,
    string Quality = Media.Quality.Original, int TargetHeight = 1080, int TargetVideoKbps = 8000, int AudioKbps = 192,
    SegmentFormat Segments = SegmentFormat.Ts, int StreamKbps = 0);

/// <summary>Decides how a file plays in a browser, and writes the ffmpeg arguments for it.</summary>
public static class PlaybackPlanner
{
    private static readonly HashSet<string> Mp4Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov" };

    /// <summary>H.264 that every browser and Chromecast decodes: 8-bit 4:2:0, not High 10 / 4:2:2 / 4:4:4.</summary>
    public static bool IsWebH264(ProbeStream v) =>
        v.Codec == "h264" &&
        (v.PixelFormat is null or "yuv420p" or "yuvj420p") &&
        (v.Profile is null || !(v.Profile.Contains("10") || v.Profile.Contains("4:2:2") || v.Profile.Contains("4:4:4")));

    /// <summary>HEVC (x265) this device said it decodes: Main (8-bit) or Main 10, 4:2:0 only.</summary>
    public static bool IsPlayableHevc(ProbeStream v, ClientCaps caps)
    {
        if (v.Codec != "hevc") return false;
        if (v.PixelFormat is "yuv420p" or "yuvj420p" or null) return caps.Hevc;
        if (v.PixelFormat is "yuv420p10le" or "yuv420p10be") return caps.Hevc10;
        return false;   // 4:2:2, 4:4:4, 12-bit: no browser
    }

    /// <summary>
    /// How to play a file at a quality. "original" (or a level the file already fits in) sends the file's own
    /// video when this device can decode it: the file as it is, or copied into HLS. Otherwise it's transcoded to
    /// H.264 at the level (or at the server's maximum, for "original").
    /// </summary>
    public static PlaybackPlan Decide(ProbeResult probe, string extension, int? audioIndex, int? burnSubtitleIndex,
        string? quality = null, ClientCaps? caps = null, TranscodingOptions? options = null)
    {
        caps ??= ClientCaps.H264Only;
        options ??= new TranscodingOptions();
        var video = probe.Video;
        var audio = audioIndex is { } ai ? probe.Audio.FirstOrDefault(a => a.Index == ai) : probe.DefaultAudio;
        var burn = burnSubtitleIndex is { } si ? probe.Subtitles.FirstOrDefault(s => s.Index == si && !s.IsTextSubtitle) : null;

        var level = Quality.Level(quality);
        var qualityId = level?.Id ?? Quality.Original;
        int height = Math.Min(level?.Height ?? options.MaxHeight, options.MaxHeight);
        int videoKbps = Math.Min(level?.VideoKbps ?? options.MaxVideoKbps, options.MaxVideoKbps);
        int audioKbps = level?.AudioKbps ?? 192;

        PlaybackPlan Transcode(string reason) => new(PlaybackMode.Transcode, video, audio, burn, reason,
            qualityId, height, videoKbps, audioKbps, SegmentFormat.Ts, videoKbps + audioKbps);
        PlaybackPlan Pass(PlaybackMode mode, string reason, SegmentFormat seg) => new(mode, video, audio, null, reason,
            qualityId, height, videoKbps, audioKbps, seg, probe.BitRateKbps);

        if (video is null) return Transcode("no video stream");
        if (burn is not null) return Transcode("burning in image subtitles");

        // The file's own video only when it already fits the picked level (or Original was picked)
        bool fits = level is null || (video.Height <= level.Height && probe.BitRateKbps > 0 && probe.BitRateKbps <= level.TotalKbps);
        if (!fits) return Transcode($"{level!.Label}: {video.Height}p at {probe.BitRateKbps} kbit/s is more than it allows");

        bool h264 = IsWebH264(video), hevc = IsPlayableHevc(video, caps);
        // A browser plays the first audio track of a file; any other one needs HLS
        bool firstAudio = audio is null || audio.Index == probe.Audio.First().Index;
        if (firstAudio && Mp4Extensions.Contains(extension) && probe.FormatName.Contains("mp4") &&
            (h264 || hevc) && (audio is null || audio.Codec is "aac" or "mp3"))
            return Pass(PlaybackMode.Direct, $"MP4 with {(h264 ? "H.264" : "HEVC")}", SegmentFormat.Ts);
        if (firstAudio && extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) &&
            video.Codec is "vp8" or "vp9" or "av1" && (audio is null || audio.Codec is "opus" or "vorbis"))
            return Pass(PlaybackMode.Direct, "WebM", SegmentFormat.Ts);

        if (h264) return Pass(PlaybackMode.Remux, $"H.264 in {probe.FormatName}: video copied", SegmentFormat.Ts);
        if (hevc) return Pass(PlaybackMode.Remux, "HEVC copied: this device decodes it", SegmentFormat.Fmp4);
        return Transcode(video.Codec == "hevc"
            ? "HEVC this device can't decode: converting to H.264"
            : $"{video.Codec} video needs converting");
    }

    /// <summary>
    /// ffmpeg arguments that write an HLS event playlist (index.m3u8) and its segments, starting
    /// <paramref name="startSeconds"/> into the file. Seeking further restarts with a new start. Segments are
    /// written to a temporary name and renamed, so a listed segment is complete. The names are relative: ffmpeg
    /// runs in the session's folder (an absolute Windows path put fMP4's init.mp4 somewhere else).
    /// </summary>
    public static List<string> HlsArguments(PlaybackPlan plan, string input, double startSeconds, TranscodingOptions o)
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
                // Safari only plays HEVC tagged hvc1 (MKVs usually carry hev1)
                if (v.Codec == "hevc") a.AddRange(["-tag:v", "hvc1"]);
            }
            else
            {
                int height = Math.Min(plan.TargetHeight, o.MaxHeight);
                bool scale = v.Height > height;
                var chain = o.Encoder switch
                {
                    // VA-API scales on the GPU, after the frames are uploaded to it
                    VideoEncoder.Vaapi => "format=nv12,hwupload" + (scale ? $",scale_vaapi=w=-2:h={height}" : ""),
                    VideoEncoder.Qsv => (scale ? $"scale=-2:{height}," : "") + "format=nv12",
                    _ => (scale ? $"scale=-2:{height}," : "") + "format=yuv420p",
                };
                if (plan.BurnSubtitle is { } s)
                {
                    a.AddRange(["-filter_complex", $"[0:{v.Index}][0:{s.Index}]overlay,{chain}[v]", "-map", "[v]"]);
                }
                else
                {
                    a.AddRange(["-map", $"0:{v.Index}", "-vf", chain]);
                }
                a.AddRange(EncoderArguments(o, Math.Min(plan.TargetVideoKbps, o.MaxVideoKbps)));
                // A keyframe at every segment boundary, so each segment starts cleanly
                a.AddRange(["-force_key_frames", $"expr:gte(t,n_forced*{o.SegmentSeconds})"]);
            }
        }

        if (plan.Audio is { } au)
        {
            a.AddRange(["-map", $"0:{au.Index}"]);
            // Keep stereo AAC as it is unless a lower quality was picked and it's well over that level's audio rate
            bool lowRate = plan.Quality != Quality.Original && plan.Mode == PlaybackMode.Transcode;
            if (au.Codec == "aac" && au.Channels is > 0 and <= 2 && !lowRate) a.AddRange(["-c:a", "copy"]);
            else a.AddRange(["-c:a", "aac", "-b:a", $"{plan.AudioKbps}k", "-ac", "2"]);
        }

        bool fmp4 = plan.Segments == SegmentFormat.Fmp4;
        a.AddRange([
            "-sn", "-dn", "-map_metadata", "-1", "-max_muxing_queue_size", "4096",
            "-f", "hls",
            "-hls_time", o.SegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_list_size", "0",
            "-hls_playlist_type", "event",
            "-hls_segment_type", fmp4 ? "fmp4" : "mpegts",
        ]);
        if (fmp4) a.AddRange(["-hls_fmp4_init_filename", "init.mp4"]);
        a.AddRange([
            "-hls_flags", "independent_segments+temp_file",
            "-hls_segment_filename", fmp4 ? "seg%05d.m4s" : "seg%05d.ts",
            "index.m3u8",
        ]);
        return a;
    }

    private static IEnumerable<string> EncoderArguments(TranscodingOptions o, int kbps)
    {
        var max = $"{kbps}k";
        var buf = $"{kbps * 2}k";
        return o.Encoder switch
        {
            VideoEncoder.Nvenc => ["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "23", "-maxrate", max, "-bufsize", buf, "-profile:v", "high"],
            VideoEncoder.Qsv => ["-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "23", "-maxrate", max, "-bufsize", buf, "-profile:v", "high"],
            VideoEncoder.Vaapi => ["-c:v", "h264_vaapi", "-rc_mode", "VBR", "-b:v", $"{kbps * 3 / 4}k", "-maxrate", max, "-bufsize", buf, "-profile:v", "high"],
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
