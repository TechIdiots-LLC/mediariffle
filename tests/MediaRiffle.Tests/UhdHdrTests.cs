using MediaRiffle.Server.Media;
using MediaRiffle.Server.Options;

namespace MediaRiffle.Tests;

public class UhdHdrTests
{
    private static ProbeStream Hevc4k(string? transfer) =>
        new(0, StreamType.Video, "hevc", "Main 10", "yuv420p10le", 3840, 2160, 0, null, null, true, false, transfer, transfer is null ? null : "bt2020");

    private static ProbeStream A() => new(1, StreamType.Audio, "eac3", null, null, 0, 0, 6, null, null, true, false);

    private static PlaybackPlan Plan(ProbeStream v, bool toneMap, params string[] levels) =>
        new(PlaybackMode.Transcode, v, A(), null, "test", Levels: levels.Select(id => Quality.Level(id)!).ToList(), ToneMap: toneMap);

    private static readonly TranscodingOptions Uhd = new() { MaxHeight = 2160, MaxVideoKbps = 30000, Encoder = VideoEncoder.Nvenc };

    [Theory]
    [InlineData(2160, 2160, "2160,1440,1080hq,1080,720,480,360")]   // 4K allowed, a 4K file: every level
    [InlineData(2160, 1440, "1440,1080hq,1080,720,480,360")]        // up to 1440p: a 4K file gets it, not 4K
    [InlineData(1080, 2160, "1080hq,1080,720,480,360")]      // a 1080p file never gets a 4K level
    [InlineData(2160, 1080, "1080hq,1080,720,480,360")]      // 4K not allowed (the default): as before
    [InlineData(1600, 2160, "2160,1440,1080hq,1080,720,480,360")]   // between levels: 4K keeps the picture's own size
    [InlineData(1440, 2160, "1440,1080hq,1080,720,480,360")]        // a 1440p file: no 4K level
    public void FourKIsALevelWhenTheServerAllowsIt(int source, int max, string expected) =>
        Assert.Equal(expected, string.Join(",", Quality.AdaptiveLevels(source, max, Uhd.MaxVideoKbps, null).Select(l => l.Id)));

    [Fact]
    public void FourKLevelsSayTheirH264Level()
    {
        var text = PlaybackPlanner.MasterPlaylist(Plan(Hevc4k(null), false, "2160", "1080"), 0);
        Assert.Contains("RESOLUTION=3840x2160,CODECS=\"avc1.640033,mp4a.40.2\"", text);   // level 5.1
        Assert.Contains("RESOLUTION=1920x1080,CODECS=\"avc1.640029,mp4a.40.2\"", text);   // level 4.1
        Assert.Equal("5.1", PlaybackPlanner.H264Level(2160));
        Assert.Equal("4.1", PlaybackPlanner.H264Level(1080));
    }

    [Theory]
    [InlineData("smpte2084", true)]      // HDR10 / Dolby Vision base layer
    [InlineData("arib-std-b67", true)]   // HLG
    [InlineData("bt709", false)]
    [InlineData(null, false)]
    public void HdrIsTheTransfer(string? transfer, bool hdr) => Assert.Equal(hdr, Hevc4k(transfer).IsHdr);

    [Fact]
    public void HdrIsScaledThenToneMappedThenTaggedBt709()
    {
        var args = PlaybackPlanner.SegmentArguments(Plan(Hevc4k("smpte2084"), true, "1080"), Quality.Level("1080")!, "/m/a.mkv", 0, Uhd);
        var vf = args[args.IndexOf("-vf") + 1];
        Assert.StartsWith("scale=-2:1080,zscale=tin=smpte2084:min=bt2020nc:pin=bt2020", vf);
        Assert.Contains("tonemap=tonemap=hable", vf);
        Assert.EndsWith("format=yuv420p", vf);
        Assert.Equal("bt709", args[args.IndexOf("-color_trc") + 1]);
    }

    [Fact]
    public void HlgUsesItsOwnTransfer()
    {
        var args = PlaybackPlanner.SegmentArguments(Plan(Hevc4k("arib-std-b67"), true, "2160"), Quality.Level("2160")!, "/m/a.mkv", 0, Uhd);
        var vf = args[args.IndexOf("-vf") + 1];
        Assert.StartsWith("zscale=tin=arib-std-b67", vf);   // 4K to 4K: nothing to scale first
    }

    [Fact]
    public void WithoutToneMappingNothingChanges()
    {
        var args = PlaybackPlanner.SegmentArguments(Plan(Hevc4k("smpte2084"), false, "1080"), Quality.Level("1080")!, "/m/a.mkv", 0, Uhd);
        Assert.DoesNotContain(args, a => a.Contains("zscale"));
        Assert.DoesNotContain("-color_trc", args);
    }

    [Fact]
    public void VaapiToneMapsBeforeUploading()
    {
        var o = new TranscodingOptions { MaxHeight = 2160, MaxVideoKbps = 30000, Encoder = VideoEncoder.Vaapi };
        var args = PlaybackPlanner.SegmentArguments(Plan(Hevc4k("smpte2084"), true, "1080"), Quality.Level("1080")!, "/m/a.mkv", 0, o);
        var vf = args[args.IndexOf("-vf") + 1];
        Assert.EndsWith("zscale=t=bt709:m=bt709:r=tv,format=nv12,hwupload", vf);
    }

    [Fact]
    public void ToneMappingWantsZscaleAndTonemapInThisFfmpeg()
    {
        const string list = " ... zoompan           V->V       Apply Zoom & Pan effect.\n" +
                            " ... zscale            V->V       Apply resizing, colorspace and bit depth conversion.\n" +
                            " .S. tonemap           V->V       Conversion to/from different dynamic ranges.\n";
        Assert.True(Ffmpeg.HasFilter(list, "zscale"));
        Assert.True(Ffmpeg.HasFilter(list, "tonemap"));
        Assert.False(Ffmpeg.HasFilter(list, "tonemap_opencl"));
        Assert.False(Ffmpeg.HasFilter(" ... scale  V->V  Scale the input video size.\n", "zscale"));
    }

    [Fact]
    public void FfprobesColourFieldsAreRead()
    {
        var probe = ProbeResult.Parse("""
            { "format": {}, "streams": [ { "index": 0, "codec_type": "video", "codec_name": "hevc", "width": 3840, "height": 2160,
              "pix_fmt": "yuv420p10le", "color_transfer": "smpte2084", "color_primaries": "bt2020" } ] }
            """);
        Assert.True(probe.Video!.IsHdr);
        Assert.Equal("bt2020", probe.Video.ColorPrimaries);
    }
}

public class DolbyVisionTests
{
    private static ProbeResult Probe(int? profile, int? compat, string transfer = "smpte2084") => ProbeResult.Parse($$"""
        { "format": { "format_name": "matroska,webm", "duration": "7200", "bit_rate": "40000000" },
          "streams": [ { "index": 0, "codec_type": "video", "codec_name": "hevc", "profile": "Main 10", "pix_fmt": "yuv420p10le",
                         "width": 3840, "height": 2160, "color_transfer": "{{transfer}}", "color_primaries": "bt2020"
                         {{(profile is null ? "" : $$""", "side_data_list": [ { "side_data_type": "DOVI configuration record", "dv_profile": {{profile}}, "dv_bl_signal_compatibility_id": {{compat}} } ]""")}} },
                       { "index": 1, "codec_type": "audio", "codec_name": "aac", "channels": 2 } ] }
        """);

    [Fact]
    public void TheProfileIsReadFromFfprobe()
    {
        var p8 = Probe(8, 1).Video!;
        Assert.Equal(8, p8.DolbyVisionProfile);
        Assert.Equal("Dolby Vision 8.1", p8.DolbyVisionLabel);
        Assert.False(p8.IsDolbyVisionOnly);   // HDR10 underneath
        var p5 = Probe(5, 0, transfer: "unknown").Video!;
        Assert.True(p5.IsDolbyVisionOnly);
        Assert.True(p5.IsHdr);                // even with no PQ transfer said
        Assert.Null(Probe(null, null).Video!.DolbyVisionLabel);
    }

    [Fact]
    public void Profile5IsNeverSentAsItIs()
    {
        var hevcDevice = new ClientCaps(Hevc: true, Hevc10: true);
        Assert.Equal(PlaybackMode.Transcode, PlaybackPlanner.Decide(Probe(5, 0), ".mkv", null, null, null, hevcDevice).Mode);
        // Profile 8.1 plays as the HDR10 it carries
        Assert.Equal(PlaybackMode.Remux, PlaybackPlanner.Decide(Probe(8, 1), ".mkv", null, null, null, hevcDevice).Mode);
    }

    [Fact]
    public void LibplaceboTonemapsAndScalesInOneOnVulkan()
    {
        var plan = new PlaybackPlan(PlaybackMode.Transcode, Probe(5, 0).Video, null, null, "test",
            Levels: [Quality.Level("1080")!], Libplacebo: true);
        var args = PlaybackPlanner.SegmentArguments(plan, Quality.Level("1080")!, "/m/a.mkv", 0, new TranscodingOptions { Encoder = VideoEncoder.Nvenc });
        Assert.Equal("vulkan", args[args.IndexOf("-init_hw_device") + 1]);
        Assert.True(args.IndexOf("-init_hw_device") < args.IndexOf("-i"));
        var vf = args[args.IndexOf("-vf") + 1];
        Assert.StartsWith("libplacebo=w=-2:h=1080:", vf);
        Assert.Contains("apply_dolbyvision=1", vf);
        Assert.DoesNotContain("zscale", vf);
        Assert.Equal("bt709", args[args.IndexOf("-color_trc") + 1]);
    }

    [Fact]
    public void WithoutLibplaceboNoVulkanDeviceIsAsked()
    {
        var plan = new PlaybackPlan(PlaybackMode.Transcode, Probe(8, 1).Video, null, null, "test", Levels: [Quality.Level("1080")!], ToneMap: true);
        var args = PlaybackPlanner.SegmentArguments(plan, Quality.Level("1080")!, "/m/a.mkv", 0, new TranscodingOptions());
        Assert.DoesNotContain("-init_hw_device", args);
        Assert.Contains("zscale", args[args.IndexOf("-vf") + 1]);
    }
}
