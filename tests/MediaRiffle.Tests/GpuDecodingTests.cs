using MediaRiffle.Server.Media;
using MediaRiffle.Server.Options;

namespace MediaRiffle.Tests;

public class GpuDecodingTests
{
    private static ProbeStream Video(string codec, string pix, int height = 2160, string? transfer = null, int? dv = null, int? dvCompat = null) =>
        new(0, StreamType.Video, codec, null, pix, height * 16 / 9, height, 0, null, null, true, false, transfer,
            transfer is null ? null : "bt2020", dv, dvCompat);

    private static readonly ProbeStream Subtitle = new(2, StreamType.Subtitle, "hdmv_pgs_subtitle", null, null, 0, 0, 0, null, null, false, false);
    private static ProbeStream A() => new(1, StreamType.Audio, "eac3", null, null, 0, 0, 6, null, null, true, false);

    private static readonly PlaybackPlanner.GpuDecoding Intel = new(new HashSet<string> { "h264", "hevc", "hevc10", "av1" }, ToneMap: true);

    private static TranscodingOptions O(VideoEncoder encoder) => new() { Encoder = encoder, MaxHeight = 1080 };

    [Theory]
    [InlineData("hevc", "yuv420p10le", "hevc10")]
    [InlineData("hevc", "yuv420p", "hevc")]
    [InlineData("h264", "yuv420p", "h264")]
    [InlineData("h264", "yuv422p", null)]        // 4:2:2: no GPU decodes it
    [InlineData("hevc", "yuv420p12le", null)]
    [InlineData("av1", "yuv420p10le", "av1")]
    [InlineData("mpeg2video", "yuv420p", "mpeg2")]
    [InlineData("vc1", "yuv420p", null)]
    public void TheKindOfVideo(string codec, string pix, string? expected) =>
        Assert.Equal(expected, PlaybackPlanner.GpuCodec(Video(codec, pix)));

    [Fact]
    public void DecodedOnTheGpuOnlyWhenNothingNeedsTheCpu()
    {
        var sdr10 = Video("hevc", "yuv420p10le");
        var hdr10 = Video("hevc", "yuv420p10le", transfer: "smpte2084");
        Assert.True(PlaybackPlanner.DecodesOnGpu(sdr10, null, Intel, O(VideoEncoder.Qsv)));
        Assert.True(PlaybackPlanner.DecodesOnGpu(sdr10, null, Intel, O(VideoEncoder.Vaapi)));
        Assert.True(PlaybackPlanner.DecodesOnGpu(Video("h264", "yuv420p"), null, Intel, O(VideoEncoder.Nvenc)));
        // Software encoding, the setting off, a codec it didn't decode, a burned-in subtitle
        Assert.False(PlaybackPlanner.DecodesOnGpu(sdr10, null, Intel, O(VideoEncoder.Libx264)));
        Assert.False(PlaybackPlanner.DecodesOnGpu(sdr10, null, Intel, new() { Encoder = VideoEncoder.Qsv, HardwareDecoding = false }));
        Assert.False(PlaybackPlanner.DecodesOnGpu(Video("vp9", "yuv420p"), null, Intel, O(VideoEncoder.Qsv)));
        Assert.False(PlaybackPlanner.DecodesOnGpu(sdr10, Subtitle, Intel, O(VideoEncoder.Qsv)));
        // HDR: only VA-API with libplacebo on its frames (Quick Sync's own tone mapping is too dark)
        Assert.True(PlaybackPlanner.DecodesOnGpu(hdr10, null, Intel, O(VideoEncoder.Vaapi)));
        Assert.False(PlaybackPlanner.DecodesOnGpu(hdr10, null, Intel with { ToneMap = false }, O(VideoEncoder.Vaapi)));
        Assert.False(PlaybackPlanner.DecodesOnGpu(hdr10, null, Intel, O(VideoEncoder.Qsv)));
        Assert.False(PlaybackPlanner.DecodesOnGpu(hdr10, null, Intel, O(VideoEncoder.Nvenc)));
        Assert.False(PlaybackPlanner.DecodesOnGpu(hdr10, null, Intel, new() { Encoder = VideoEncoder.Vaapi, UseLibplacebo = false }));
        // Dolby Vision 5 too, as libplacebo applies its metadata there
        Assert.True(PlaybackPlanner.DecodesOnGpu(Video("hevc", "yuv420p10le", dv: 5, dvCompat: 0), null, Intel, O(VideoEncoder.Vaapi)));
        Assert.False(PlaybackPlanner.DecodesOnGpu(Video("hevc", "yuv420p10le", dv: 5, dvCompat: 0), null, Intel, O(VideoEncoder.Qsv)));
    }

    private static PlaybackPlan Plan(ProbeStream v, bool gpu) =>
        new(PlaybackMode.Transcode, v, A(), null, "test", Levels: [Quality.Level("1080")!], GpuDecode: gpu, Libplacebo: v.IsHdr);

    [Fact]
    public void QuickSyncDecodesAndScalesOnTheGpu()
    {
        var args = PlaybackPlanner.SegmentArguments(Plan(Video("hevc", "yuv420p10le"), gpu: true), Quality.Level("1080")!, "/m/a.mkv", 10, O(VideoEncoder.Qsv));
        int input = args.IndexOf("-i");
        Assert.True(args.IndexOf("-hwaccel") is >= 0 and var h && h < input);
        Assert.Equal("qsv", args[args.IndexOf("-hwaccel_output_format") + 1]);
        Assert.Equal("hevc_qsv", args[args.IndexOf("-c:v") + 1]);          // the decoder, before -i
        Assert.True(args.IndexOf("-c:v") < input);
        Assert.Equal("vpp_qsv=w=1920:h=1080:format=nv12", args[args.IndexOf("-vf") + 1]);
        Assert.Equal("h264_qsv", args[args.LastIndexOf("-c:v") + 1]);
        Assert.DoesNotContain("-color_trc", args);   // SDR stays as it was
    }

    [Fact]
    public void VaapiToneMapsWithLibplaceboOnItsFrames()
    {
        var hdr10 = Video("hevc", "yuv420p10le", transfer: "smpte2084");
        var args = PlaybackPlanner.SegmentArguments(Plan(hdr10, gpu: true), Quality.Level("1080")!, "/m/a.mkv", 0, O(VideoEncoder.Vaapi));
        Assert.Equal("vaapi=va:/dev/dri/renderD128", args[args.IndexOf("-init_hw_device") + 1]);
        Assert.Equal("vulkan=vk@va", args[args.LastIndexOf("-init_hw_device") + 1]);
        Assert.Equal("va", args[args.IndexOf("-hwaccel_device") + 1]);
        Assert.DoesNotContain("-vaapi_device", args);   // the decoder's device carries through
        var vf = args[args.IndexOf("-vf") + 1];
        Assert.StartsWith("hwmap=derive_device=vulkan,format=vulkan,libplacebo=w=1920:h=1080:", vf);
        Assert.Contains("apply_dolbyvision=1", vf);
        Assert.EndsWith("hwmap=derive_device=vaapi,format=vaapi", vf);
        Assert.Equal("bt709", args[args.IndexOf("-color_trc") + 1]);
        var sdr = PlaybackPlanner.SegmentArguments(Plan(Video("h264", "yuv420p", 1080), gpu: true), Quality.Level("720")!, "/m/a.mkv", 0, O(VideoEncoder.Vaapi));
        Assert.Equal("scale_vaapi=w=1280:h=720:format=nv12", sdr[sdr.IndexOf("-vf") + 1]);
        Assert.DoesNotContain("-init_hw_device", sdr);
    }

    [Fact]
    public void WithoutItTheCpuDecodesAsBefore()
    {
        var hdr10 = Video("hevc", "yuv420p10le", transfer: "smpte2084");
        var args = PlaybackPlanner.SegmentArguments(Plan(hdr10, gpu: false), Quality.Level("1080")!, "/m/a.mkv", 0, O(VideoEncoder.Qsv));
        Assert.DoesNotContain("-hwaccel", args);
        Assert.Contains("vulkan", args);
        Assert.StartsWith("libplacebo=", args[args.IndexOf("-vf") + 1]);
        // VA-API without GPU decoding uploads CPU frames to its device
        var vaapi = PlaybackPlanner.SegmentArguments(Plan(Video("h264", "yuv420p", 1080), gpu: false), Quality.Level("720")!, "/m/a.mkv", 0, O(VideoEncoder.Vaapi));
        Assert.Equal("/dev/dri/renderD128", vaapi[vaapi.IndexOf("-vaapi_device") + 1]);
        Assert.DoesNotContain("-hwaccel", vaapi);
    }

    [Fact]
    public void TheStartupCheckUsesTheSamePipeline()
    {
        var args = PlaybackPlanner.GpuTestArguments("hevc10", "/tmp/hevc10.mkv", O(VideoEncoder.Qsv), toneMap: false);
        Assert.Equal("hevc_qsv", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("vpp_qsv=w=160:h=90:format=nv12", args[args.IndexOf("-vf") + 1]);
        var hdr = PlaybackPlanner.GpuTestArguments("hevc10", "/tmp/hevc10.mkv", O(VideoEncoder.Vaapi), toneMap: true);
        Assert.Contains("vulkan=vk@va", hdr);
        Assert.Contains("libplacebo=", hdr[hdr.IndexOf("-vf") + 1]);
        Assert.Equal("h264_qsv", args[args.LastIndexOf("-c:v") + 1]);
        Assert.Equal("mpeg2_qsv", PlaybackPlanner.GpuDecoder("mpeg2", O(VideoEncoder.Qsv)).Last());
    }
}
