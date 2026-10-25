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
/// <param name="Levels">
/// Set for an adaptive transcode: the levels of its master playlist, best first, each a full-length playlist whose
/// segments are made when asked for (see <see cref="PlaybackPlanner.SegmentArguments"/>). Null for a single stream.
/// </param>
/// <param name="ToneMap">
/// Transcoded HDR video is tone-mapped to SDR (BT.709) first, so it isn't washed out; set when the video is HDR and
/// the server's ffmpeg can (see <see cref="Ffmpeg.CanToneMap"/>).
/// </param>
/// <param name="GpuDecode">
/// Decoded on the hardware encoder's GPU, and scaled and (HDR) tone-mapped there, the picture never leaving it (see
/// <see cref="PlaybackPlanner.DecodesOnGpu"/>). ToneMap and Libplacebo are still set for HDR: they're what a stream
/// falls back to if the GPU fails on it.
/// </param>
public sealed record PlaybackPlan(
    PlaybackMode Mode, ProbeStream? Video, ProbeStream? Audio, ProbeStream? BurnSubtitle, string Reason,
    string Quality = Media.Quality.Original, int TargetHeight = 1080, int TargetVideoKbps = 8000, int AudioKbps = 192,
    SegmentFormat Segments = SegmentFormat.Ts, int StreamKbps = 0, IReadOnlyList<QualityLevel>? Levels = null,
    bool ToneMap = false, bool Libplacebo = false, bool GpuDecode = false)
{
    public bool IsAdaptive => Levels is { Count: > 0 };
}

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
        // Dolby Vision 5 has no ordinary picture to send: only converting it (with its Dolby Vision metadata) looks right
        if (video.IsDolbyVisionOnly) return Transcode($"{video.DolbyVisionLabel} only shows right through a Dolby Vision decoder: converting");
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
        a.AddRange(DeviceArguments(plan, o, transcode));
        // Copied video starts at the keyframe ffmpeg's seek lands on, before the time; -noaccurate_seek starts the
        // audio there too. Otherwise it's cut at the time itself, a second or more later, and in fMP4 that gap is
        // only an edit list, which browsers ignore: the sound plays that much early (see Ffmpeg.RemuxStartAsync)
        if (startSeconds > 0) a.AddRange([.. transcode ? [] : new[] { "-noaccurate_seek" }, "-ss", Seconds(startSeconds)]);
        if (transcode && plan.GpuDecode && plan.Video is { } gv) a.AddRange(GpuDecoder(gv, o));
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
                a.AddRange(VideoTranscode(v, plan.BurnSubtitle, plan.TargetHeight, plan.TargetVideoKbps, o, fixedKeyframes: false, plan.ToneMap, plan.Libplacebo, plan.GpuDecode));
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

    /// <summary>
    /// Mapping, filters and encoder for H.264 at most <paramref name="targetHeight"/> lines high (never scaled up) and
    /// <paramref name="kbps"/>, with an image subtitle burned in when given. A keyframe at every segment boundary, so
    /// each segment starts cleanly (<paramref name="fixedKeyframes"/>: for an adaptive stream, where it must).
    /// </summary>
    private static List<string> VideoTranscode(ProbeStream v, ProbeStream? burn, int targetHeight, int kbps, TranscodingOptions o,
        bool fixedKeyframes, bool toneMap = false, bool libplacebo = false, bool gpuDecode = false)
    {
        var a = new List<string>();
        int height = Math.Min(targetHeight, o.MaxHeight);
        bool scale = v.Height > height;
        // HDR: libplacebo scales and tone-maps in one (on the GPU, applying Dolby Vision's metadata); else it's scaled
        // down first (less to tone-map), then tone-mapped with zscale on the CPU, whatever the encoder
        var hdr = !v.IsHdr ? null
            : libplacebo ? LibplaceboFilter(scale ? height : null) + ","
            : toneMap ? (scale ? $"scale=-2:{height}," : "") + ToneMapFilter(v) + ","
            : null;
        var chain = gpuDecode ? GpuFilter(o, OutputSize(v, height), v.IsHdr)
            : o.Encoder switch
            {
                // VA-API scales on the GPU, after the frames are uploaded to it
                VideoEncoder.Vaapi => hdr is not null ? hdr + "format=nv12,hwupload"
                    : "format=nv12,hwupload" + (scale ? $",scale_vaapi=w=-2:h={height}" : ""),
                VideoEncoder.Qsv => (hdr ?? (scale ? $"scale=-2:{height}," : "")) + "format=nv12",
                _ => (hdr ?? (scale ? $"scale=-2:{height}," : "")) + "format=yuv420p",
            };
        if (burn is { } s) a.AddRange(["-filter_complex", $"[0:{v.Index}][0:{s.Index}]overlay,{chain}[v]", "-map", "[v]"]);
        else a.AddRange(["-map", $"0:{v.Index}", "-vf", chain]);
        a.AddRange(EncoderArguments(o, Math.Min(kbps, o.MaxVideoKbps), Math.Min(height, v.Height > 0 ? v.Height : height)));
        // Tone-mapped (or ordinary) output is BT.709: say so, or players may take it for the source's BT.2020
        if (hdr is not null || (gpuDecode && v.IsHdr)) a.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"]);
        // A player switching level starts a segment with no picture before it: NVENC's forced keyframes must be IDR
        if (fixedKeyframes && o.Encoder == VideoEncoder.Nvenc) a.AddRange(["-forced-idr", "1"]);
        // Quick Sync ignores forced keyframes altogether without this, keeping its own GOP of 256 frames: segments then
        // come out 10 or 21 s long against a playlist of 4 s ones, and the stream's clock drifts from the file's
        if (o.Encoder == VideoEncoder.Qsv) a.AddRange(["-forced_idr", "1"]);
        // t counts from the first frame of this run, which starts on a segment boundary
        a.AddRange(["-force_key_frames", $"expr:gte(t,n_forced*{o.SegmentSeconds})"]);
        return a;
    }

    /// <summary>
    /// The GPU devices a transcode uses, before its input: VA-API's for uploading frames decoded on the CPU, and Vulkan
    /// for libplacebo. Decoding on the GPU brings its own device (<see cref="GpuDecoder"/>).
    /// </summary>
    private static List<string> DeviceArguments(PlaybackPlan plan, TranscodingOptions o, bool transcode)
    {
        if (!transcode || plan.GpuDecode) return [];
        var a = new List<string>();
        if (o.Encoder == VideoEncoder.Vaapi) a.AddRange(["-vaapi_device", o.VaapiDevice]);
        if (plan.Libplacebo) a.AddRange(["-init_hw_device", "vulkan"]);
        return a;
    }

    // ── Decoding on the GPU ──
    //
    // With a GPU encoder, the video can be decoded on that GPU and scaled and tone-mapped there too (VA-API's
    // scale_vaapi and tonemap_vaapi, Quick Sync's vpp_qsv, CUDA's scale_cuda), so the picture never comes back to the
    // CPU: copying 4K frames back costs about what decoding them in software does. What the GPU decodes is found out
    // at start (Ffmpeg.CheckGpuDecodingAsync), by putting a tiny sample of each codec through this same pipeline.

    /// <summary>The codecs a GPU was found to decode (<see cref="GpuCodec"/>'s names), and whether it tone-maps HDR.</summary>
    public sealed record GpuDecoding(IReadOnlySet<string> Codecs, bool ToneMap)
    {
        public static readonly GpuDecoding None = new(new HashSet<string>(), false);
    }

    /// <summary>The kind of video a GPU might decode: h264, hevc, hevc10, av1, vp9 or mpeg2 (4:2:0 only); null for anything else.</summary>
    public static string? GpuCodec(ProbeStream v) => (v.Codec, v.PixelFormat) switch
    {
        ("h264", null or "yuv420p" or "yuvj420p") => "h264",
        ("hevc", null or "yuv420p" or "yuvj420p") => "hevc",
        ("hevc", "yuv420p10le") => "hevc10",
        ("av1", null or "yuv420p" or "yuv420p10le") => "av1",
        ("vp9", null or "yuv420p") => "vp9",
        ("mpeg2video", null or "yuv420p") => "mpeg2",
        _ => null,
    };

    /// <summary>
    /// Whether this video is decoded on the GPU: the encoder's GPU decodes its kind, and nothing needs the picture on
    /// the CPU. An image subtitle burned in does; so does HDR (Dolby Vision 5 included) unless the GPU path can tone-map
    /// it with libplacebo, which is VA-API's (see <see cref="GpuFilter"/>; on Quick Sync's integrated GPUs the copy back
    /// costs what it saves). Quick Sync's and VA-API's own tone mapping isn't used: it comes out much darker than
    /// libplacebo's.
    /// </summary>
    public static bool DecodesOnGpu(ProbeStream v, ProbeStream? burn, GpuDecoding gpu, TranscodingOptions o) =>
        o.HardwareDecoding && o.Encoder != VideoEncoder.Libx264 && burn is null &&
        GpuCodec(v) is { } codec && gpu.Codecs.Contains(codec) &&
        (!v.IsHdr || (gpu.ToneMap && o.Encoder == VideoEncoder.Vaapi && o.ToneMapHdr && o.UseLibplacebo));

    /// <summary>The input options that decode on the encoder's GPU and keep the frames there (before -i).</summary>
    public static List<string> GpuDecoder(ProbeStream v, TranscodingOptions o) => GpuDecoder(GpuCodec(v) ?? "h264", o, v.IsHdr);

    public static List<string> GpuDecoder(string codec, TranscodingOptions o, bool hdr = false) => o.Encoder switch
    {
        // HDR: the VA-API device named, so the frames tone-mapped on the CPU side can be uploaded back to it for encoding
        VideoEncoder.Vaapi when hdr => ["-init_hw_device", $"vaapi=va:{o.VaapiDevice}", "-filter_hw_device", "va",
            "-hwaccel", "vaapi", "-hwaccel_device", "va", "-hwaccel_output_format", "vaapi"],
        VideoEncoder.Vaapi => ["-hwaccel", "vaapi", "-hwaccel_device", o.VaapiDevice, "-hwaccel_output_format", "vaapi"],
        // Quick Sync decodes with its own decoders (on Linux through the render node)
        VideoEncoder.Qsv => [
            .. OperatingSystem.IsWindows() ? [] : new[] { "-qsv_device", o.VaapiDevice },
            "-hwaccel", "qsv", "-hwaccel_output_format", "qsv",
            "-c:v", (codec == "hevc10" ? "hevc" : codec) + "_qsv"],
        VideoEncoder.Nvenc => ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"],
        _ => [],
    };

    /// <summary>
    /// The filters for frames on the GPU: scaled to <paramref name="size"/> (exactly: vpp_qsv takes no -2) as 8-bit.
    /// HDR (VA-API only, see <see cref="DecodesOnGpu"/>): scaled on the GPU still 10-bit, then only those frames (a
    /// quarter of 4K's at 1080p) copied back, tone-mapped by libplacebo on its own Vulkan device (Dolby Vision's metadata
    /// applied) and uploaded again for the encoder. Mapping VA-API's 10-bit frames straight into Vulkan instead fails on
    /// Intel Arc with Ubuntu 24.04's Mesa (VK_ERROR_DEVICE_LOST); this way an Arc A380 converted 4K HDR at 1.7x.
    /// </summary>
    public static string GpuFilter(TranscodingOptions o, (int Width, int Height) size, bool hdr)
    {
        var (w, h) = size;
        return o.Encoder switch
        {
            VideoEncoder.Vaapi when hdr => $"scale_vaapi=w={w}:h={h}:format=p010,hwdownload,format=p010le," +
                "libplacebo=colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv:tonemapping=bt.2390:" +
                "apply_dolbyvision=1:format=nv12,hwupload",
            VideoEncoder.Vaapi => $"scale_vaapi=w={w}:h={h}:format=nv12",
            VideoEncoder.Qsv => $"vpp_qsv=w={w}:h={h}:format=nv12",
            _ => $"scale_cuda=w={w}:h={h}:format=nv12",
        };
    }

    /// <summary>
    /// How Ffmpeg.CheckGpuDecodingAsync makes its tiny sample of each kind of video: alternative ffmpeg arguments
    /// (encoders this ffmpeg may not have), the output file last. hevc10 is HDR10, for the tone-mapping check too.
    /// </summary>
    public static readonly IReadOnlyList<(string Codec, string[][] Encodes)> GpuSamples =
    [
        ("h264", [["-pix_fmt", "yuv420p", "-c:v", "libx264"]]),
        ("hevc", [["-pix_fmt", "yuv420p", "-c:v", "libx265", "-x265-params", "log-level=error"]]),
        ("hevc10", [["-pix_fmt", "yuv420p10le", "-c:v", "libx265", "-x265-params",
            "log-level=error:hdr10=1:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400",
            "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc"]]),
        ("av1", [["-pix_fmt", "yuv420p10le", "-c:v", "libsvtav1", "-preset", "12"],
                 ["-pix_fmt", "yuv420p10le", "-c:v", "libaom-av1", "-cpu-used", "8", "-usage", "realtime"],
                 ["-pix_fmt", "yuv420p10le", "-c:v", "librav1e", "-speed", "10"]]),
        ("vp9", [["-pix_fmt", "yuv420p", "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8"]]),
        ("mpeg2", [["-pix_fmt", "yuv420p", "-c:v", "mpeg2video"]]),
    ];

    /// <summary>A GPU sample through the transcode pipeline (decoded, scaled, maybe tone-mapped, encoded) to nowhere.</summary>
    public static List<string> GpuTestArguments(string codec, string sample, TranscodingOptions o, bool toneMap) =>
    [
        "-hide_banner", "-v", "error", .. GpuDecoder(codec, o, toneMap), "-i", sample,
        "-vf", GpuFilter(o, (160, 90), toneMap), .. EncoderArguments(o, 1000, 90), "-f", "null", "-",
    ];

    private static IEnumerable<string> EncoderArguments(TranscodingOptions o, int kbps, int height)
    {
        var max = $"{kbps}k";
        var buf = $"{kbps * 2}k";
        var level = H264Level(height);
        return o.Encoder switch
        {
            VideoEncoder.Nvenc => ["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "23", "-maxrate", max, "-bufsize", buf, "-profile:v", "high"],
            VideoEncoder.Qsv => ["-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "23", "-maxrate", max, "-bufsize", buf, "-profile:v", "high"],
            VideoEncoder.Vaapi => ["-c:v", "h264_vaapi", "-rc_mode", "VBR", "-b:v", $"{kbps * 3 / 4}k", "-maxrate", max, "-bufsize", buf, "-profile:v", "high"],
            _ => ["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-maxrate", max, "-bufsize", buf, "-profile:v", "high", "-level:v", level],
        };
    }

    /// <summary>
    /// HDR (and Dolby Vision, whose metadata it applies) to SDR BT.709 with libplacebo, scaled to <paramref name="height"/>
    /// on the way when given; software frames in and out, on the Vulkan device (-init_hw_device vulkan).
    /// </summary>
    public static string LibplaceboFilter(int? height) =>
        "libplacebo=" + (height is { } h ? $"w=-2:h={h}:" : "") +
        "colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv:tonemapping=bt.2390:apply_dolbyvision=1:format=yuv420p";

    /// <summary>The H.264 level a picture this tall needs: 4.1 up to 1080p, 5.1 for 4K.</summary>
    public static string H264Level(int height) => height > 1088 ? "5.1" : "4.1";

    /// <summary>The CODECS of an H.264 High profile stream this tall (with AAC), for a master playlist.</summary>
    public static string HlsVideoCodec(int height) => height > 1088 ? "avc1.640033" : "avc1.640029";

    /// <summary>
    /// HDR to SDR: into linear light (the source's transfer, PQ or HLG, and BT.2020 colours, taken from the probe as
    /// files don't always say so in the stream), tone-mapped with Hable, then BT.709 for an ordinary screen. zscale
    /// is libzimg's; <see cref="Ffmpeg.CanToneMap"/> checks this ffmpeg has it.
    /// </summary>
    public static string ToneMapFilter(ProbeStream v)
    {
        var transfer = v.ColorTransfer == "arib-std-b67" ? "arib-std-b67" : "smpte2084";
        return $"zscale=tin={transfer}:min=bt2020nc:pin=bt2020:rin=tv:t=linear:npl=100,format=gbrpf32le," +
               "zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv";
    }

    // ── Adaptive streams ──
    //
    // An adaptive transcode is a master playlist of levels, each a full-length VOD playlist of fixed segments:
    // segment k is [k·D, (k+1)·D) of the file. Nothing is made until a segment is asked for; then one ffmpeg makes
    // that level from that segment on, and keeps going while the player keeps asking for the next ones. When the
    // player switches level or seeks elsewhere, that ffmpeg is replaced by one for the new level or place. The
    // timestamps are the file's own (-copyts), so segments from different runs and levels line up, and the player's
    // clock is the file's.

    /// <summary>The number of segments of an adaptive stream (a last sliver under a quarter second is dropped).</summary>
    public static int SegmentCount(double durationSeconds, int segmentSeconds) =>
        Math.Max(1, (int)Math.Ceiling(Math.Max(0, durationSeconds - 0.25) / segmentSeconds));

    /// <summary>
    /// The master playlist: a variant per level, at "v{i}/index.m3u8". <paramref name="first"/> is listed first (a
    /// player without its own pick, such as Safari's, starts there); the levels keep their numbers whatever the order.
    /// </summary>
    public static string MasterPlaylist(PlaybackPlan plan, int first)
    {
        var levels = plan.Levels ?? [];
        var sb = new System.Text.StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        var order = Enumerable.Range(0, levels.Count).OrderBy(i => i == first ? 0 : 1).ThenBy(i => i);
        foreach (var i in order)
        {
            var l = levels[i];
            var (w, h) = OutputSize(plan.Video, l.Height);
            int kbps = l.VideoKbps + (plan.Audio is null ? 0 : l.AudioKbps);
            var codecs = plan.Audio is null ? HlsVideoCodec(h) : $"{HlsVideoCodec(h)},mp4a.40.2";
            sb.Append(CultureInfo.InvariantCulture,
                $"#EXT-X-STREAM-INF:BANDWIDTH={kbps * 1000},RESOLUTION={w}x{h},CODECS=\"{codecs}\",NAME=\"{l.Id}\"\n");
            sb.Append(CultureInfo.InvariantCulture, $"v{i}/index.m3u8\n");
        }
        return sb.ToString();
    }

    /// <summary>A level's playlist: every segment of the file, D seconds each (the last one what's left).</summary>
    public static string VariantPlaylist(double durationSeconds, int segmentSeconds)
    {
        int n = SegmentCount(durationSeconds, segmentSeconds);
        var sb = new System.Text.StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:{segmentSeconds + 1}\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        for (int k = 0; k < n; k++)
        {
            double len = k < n - 1 ? segmentSeconds : Math.Max(0.1, durationSeconds - (double)k * segmentSeconds);
            sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:{len:0.000000},\n{SegmentName(k)}\n");
        }
        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    public static string SegmentName(int k) => $"seg{k:00000}.ts";

    /// <summary>
    /// ffmpeg arguments that make level <paramref name="level"/> of an adaptive stream from segment
    /// <paramref name="startSegment"/> to the end: MPEG-TS segments named by number in the working directory, and
    /// list.csv, which gets a line when a segment is complete. The segment muxer cuts on the file's clock at every
    /// multiple of D (-copyts), so a segment's number always means the same stretch of the file.
    /// </summary>
    public static List<string> SegmentArguments(PlaybackPlan plan, QualityLevel level, string input, int startSegment, TranscodingOptions o)
    {
        var d = o.SegmentSeconds;
        // -progress: how far it has got, twice a second on stdout, for its speed (see SegmentEncoder)
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-y" };
        a.AddRange(DeviceArguments(plan, o, transcode: true));
        // -ss even at 0: the first frame of a run is then always the first one on or after the boundary
        a.AddRange(["-copyts", "-start_at_zero", "-ss", Seconds((double)startSegment * d)]);
        if (plan.GpuDecode && plan.Video is { } gv) a.AddRange(GpuDecoder(gv, o));
        a.AddRange(["-i", input]);
        if (plan.Video is { } v) a.AddRange(VideoTranscode(v, plan.BurnSubtitle, level.Height, level.VideoKbps, o, fixedKeyframes: true, plan.ToneMap, plan.Libplacebo, plan.GpuDecode));
        if (plan.Audio is { } au) a.AddRange(["-map", $"0:{au.Index}", "-c:a", "aac", "-b:a", $"{level.AudioKbps}k", "-ac", "2"]);
        a.AddRange([
            "-sn", "-dn", "-map_metadata", "-1", "-max_muxing_queue_size", "4096",
            // Not shifted to keep the first B-frame's timestamp from going negative, which only a run from 0 would be
            "-avoid_negative_ts", "disabled",
            "-f", "segment", "-segment_format", "mpegts",
            "-segment_time", d.ToString(CultureInfo.InvariantCulture),
            "-segment_start_number", startSegment.ToString(CultureInfo.InvariantCulture),
            "-segment_list", "list.csv", "-segment_list_type", "csv",
            "seg%05d.ts",
        ]);
        return a;
    }

    /// <summary>The picture size a level comes out at: scaled to its height (even width), or the picture's own when smaller.</summary>
    public static (int Width, int Height) OutputSize(ProbeStream? v, int levelHeight)
    {
        if (v is null || v.Width <= 0 || v.Height <= 0) return (levelHeight * 16 / 9 / 2 * 2, levelHeight);
        if (v.Height <= levelHeight) return (v.Width, v.Height);
        int w = (int)Math.Round(v.Width * (double)levelHeight / v.Height / 2) * 2;
        return (w, levelHeight);
    }

    /// <summary>
    /// ffmpeg arguments that write one subtitle stream (or a side-car file) as WebVTT to standard output, on the
    /// file's own clock (-copyts: never moved to start at zero). <see cref="WebVtt.Shift"/> fits it to a stream.
    /// </summary>
    public static List<string> WebVttArguments(string input, int? streamIndex) =>
        ["-hide_banner", "-loglevel", "error", "-copyts", "-i", input,
         "-map", streamIndex is { } i ? $"0:{i}" : "0:s:0", "-c:s", "webvtt", "-f", "webvtt", "pipe:1"];

    /// <summary>ffmpeg arguments for a 480 px wide JPEG of the frame at <paramref name="atSeconds"/>.</summary>
    /// The thumbnail filter looks over the next 50 frames (about 2 s) and keeps the most typical one, so a fade, a cut
    /// to black or a blurred frame at that exact moment doesn't become the picture.
    public static List<string> ThumbnailArguments(string input, double atSeconds, string output) =>
        ["-hide_banner", "-loglevel", "error", "-y", "-ss", Seconds(atSeconds), "-i", input,
         "-map", "0:v:0", "-frames:v", "1", "-vf", "thumbnail=50,scale=480:-2", "-q:v", "4", output];

    private static string Seconds(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);
}
