using MediaRiffle.Server.Media;

namespace MediaRiffle.Tests;

public class QualityTests
{
    private static ProbeStream Video(string codec, int height, string pix) =>
        new(0, StreamType.Video, codec, codec == "hevc" ? (pix.Contains("10") ? "Main 10" : "Main") : "High", pix,
            height * 16 / 9, height, 0, null, null, true, false);

    private static readonly ProbeStream Aac = new(1, StreamType.Audio, "aac", null, null, 0, 0, 2, null, null, true, false);

    private static ProbeResult Mkv(ProbeStream v, int kbps) => new() { FormatName = "matroska,webm", DurationSeconds = 3600, BitRateKbps = kbps, Streams = [v, Aac] };

    [Fact]
    public void X265IsCopiedForADeviceThatDecodesIt()
    {
        var plan = PlaybackPlanner.Decide(Mkv(Video("hevc", 1080, "yuv420p10le"), 4000), ".mkv", null, null,
            Quality.Original, new ClientCaps(Hevc: true, Hevc10: true));
        Assert.Equal(PlaybackMode.Remux, plan.Mode);
        Assert.Equal(SegmentFormat.Fmp4, plan.Segments);
        var args = PlaybackPlanner.HlsArguments(plan, "/m/a.mkv", 0, new());
        Assert.Equal("hvc1", args[args.IndexOf("-tag:v") + 1]);
        Assert.Equal("fmp4", args[args.IndexOf("-hls_segment_type") + 1]);
        Assert.Contains(args, a => a.EndsWith("seg%05d.m4s"));
    }

    [Fact]
    public void TenBitX265IsTranscodedForADeviceWithOnly8BitHevc()
    {
        var plan = PlaybackPlanner.Decide(Mkv(Video("hevc", 1080, "yuv420p10le"), 4000), ".mkv", null, null,
            Quality.Original, new ClientCaps(Hevc: true, Hevc10: false));
        Assert.Equal(PlaybackMode.Transcode, plan.Mode);
        Assert.Equal(SegmentFormat.Ts, plan.Segments);
    }

    [Fact]
    public void ALowerQualityTranscodesToThatLevel()
    {
        var plan = PlaybackPlanner.Decide(Mkv(Video("h264", 1080, "yuv420p"), 6000), ".mkv", null, null, "480");
        Assert.Equal(PlaybackMode.Transcode, plan.Mode);
        Assert.Equal(1500 + 128, plan.StreamKbps);
        var args = PlaybackPlanner.HlsArguments(plan, "/m/a.mkv", 0, new());
        Assert.Contains(args, a => a.Contains("scale=-2:480"));
        Assert.Equal("1500k", args[args.IndexOf("-maxrate") + 1]);
        Assert.Equal("128k", args[args.IndexOf("-b:a") + 1]);
    }

    [Fact]
    public void AFileThatAlreadyFitsALevelIsntTranscoded()
    {
        // A 360p file at 900 kbit/s is within 480p's 1.6 Mbit/s: no point re-encoding it
        var plan = PlaybackPlanner.Decide(Mkv(Video("h264", 360, "yuv420p"), 900), ".mkv", null, null, "480");
        Assert.Equal(PlaybackMode.Remux, plan.Mode);
        Assert.Equal(900, plan.StreamKbps);
    }

    [Fact]
    public void ServerMaximumStillApplies()
    {
        var plan = PlaybackPlanner.Decide(Mkv(Video("hevc", 2160, "yuv420p10le"), 20000), ".mkv", null, null, Quality.Original,
            ClientCaps.H264Only, new() { MaxHeight = 720, MaxVideoKbps = 3000 });
        var args = PlaybackPlanner.HlsArguments(plan, "/m/a.mkv", 0, new() { MaxHeight = 720, MaxVideoKbps = 3000 });
        Assert.Contains(args, a => a.Contains("scale=-2:720"));
        Assert.Equal("3000k", args[args.IndexOf("-maxrate") + 1]);
    }

    [Theory]
    [InlineData(null, null, "original")]
    [InlineData("720", null, "720")]
    [InlineData("original", "720", "720")]   // a capped user can't have the original
    [InlineData("1080", "720", "720")]
    [InlineData("480", "720", "480")]
    [InlineData("bogus", null, "original")]
    public void UserCap(string? requested, string? cap, string expected) => Assert.Equal(expected, Quality.Clamp(requested, cap));
}
