using MediaRiffle.Server.Library;
using MediaRiffle.Server.Media;
using MediaRiffle.Server.Options;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Tests;

public class PlaybackPlannerTests
{
    private static ProbeStream V(string codec, int height = 1080, string pix = "yuv420p", string? profile = "High") =>
        new(0, StreamType.Video, codec, profile, pix, height * 16 / 9, height, 0, null, null, true, false);

    private static ProbeStream A(int index, string codec, int channels = 2, bool isDefault = false, string? lang = null) =>
        new(index, StreamType.Audio, codec, null, null, 0, 0, channels, lang, null, isDefault, false);

    private static ProbeStream S(int index, string codec) =>
        new(index, StreamType.Subtitle, codec, null, null, 0, 0, 0, "eng", null, false, false);

    private static ProbeResult Probe(string format, params ProbeStream[] streams) => new() { FormatName = format, DurationSeconds = 3600, Streams = streams.ToList() };

    [Fact]
    public void Mp4WithH264AndAacPlaysDirectly()
    {
        var plan = PlaybackPlanner.Decide(Probe("mov,mp4,m4a,3gp,3g2,mj2", V("h264"), A(1, "aac")), ".mp4", null, null);
        Assert.Equal(PlaybackMode.Direct, plan.Mode);
    }

    [Fact]
    public void MkvWithH264IsRemuxedNotTranscoded()
    {
        var plan = PlaybackPlanner.Decide(Probe("matroska,webm", V("h264"), A(1, "ac3", 6)), ".mkv", null, null);
        Assert.Equal(PlaybackMode.Remux, plan.Mode);
    }

    [Theory]
    [InlineData("hevc", "yuv420p10le", "Main 10")]
    [InlineData("h264", "yuv420p10le", "High 10")]
    [InlineData("mpeg4", "yuv420p", "Simple Profile")]
    public void OtherVideoIsTranscoded(string codec, string pix, string profile)
    {
        var plan = PlaybackPlanner.Decide(Probe("matroska,webm", V(codec, pix: pix, profile: profile), A(1, "aac")), ".mkv", null, null);
        Assert.Equal(PlaybackMode.Transcode, plan.Mode);
    }

    [Fact]
    public void PickingTheSecondAudioTrackOfAnMp4NeedsHls()
    {
        var probe = Probe("mov,mp4,m4a,3gp,3g2,mj2", V("h264"), A(1, "aac", isDefault: true, lang: "eng"), A(2, "aac", lang: "spa"));
        Assert.Equal(PlaybackMode.Direct, PlaybackPlanner.Decide(probe, ".mp4", 1, null).Mode);
        var second = PlaybackPlanner.Decide(probe, ".mp4", 2, null);
        Assert.Equal(PlaybackMode.Remux, second.Mode);
        Assert.Equal(2, second.Audio!.Index);
    }

    [Fact]
    public void BurningImageSubtitlesTranscodes_TextOnesAreIgnored()
    {
        var probe = Probe("matroska,webm", V("h264"), A(1, "aac"), S(2, "hdmv_pgs_subtitle"), S(3, "subrip"));
        var pgs = PlaybackPlanner.Decide(probe, ".mkv", null, 2);
        Assert.Equal(PlaybackMode.Transcode, pgs.Mode);
        Assert.Equal(2, pgs.BurnSubtitle!.Index);
        Assert.Equal(PlaybackMode.Remux, PlaybackPlanner.Decide(probe, ".mkv", null, 3).Mode);
    }

    [Fact]
    public void AnOddFileNameIsOneArgument()
    {
        // Tongue built a shell command and escaped by hand; here the path is a single argv entry, whatever it holds
        var evil = "/media/Show/Season 01/it's a \"test\" & $(rm -rf ~) ; `id`.mkv";
        var plan = PlaybackPlanner.Decide(Probe("matroska,webm", V("hevc", pix: "yuv420p10le", profile: "Main 10"), A(1, "dts", 6)), ".mkv", null, null);
        var args = PlaybackPlanner.HlsArguments(plan, evil, 120, new TranscodingOptions());
        Assert.Equal(evil, args[args.IndexOf("-i") + 1]);
        Assert.Contains("120", args);
        Assert.Contains("libx264", args);
        // DTS 5.1 becomes AAC stereo for the browser
        Assert.Equal("aac", args[args.LastIndexOf("-c:a") + 1]);
    }

    [Fact]
    public void RemuxCopiesVideoAndCompatibleAudio()
    {
        var plan = PlaybackPlanner.Decide(Probe("matroska,webm", V("h264"), A(1, "aac")), ".mkv", null, null);
        var args = PlaybackPlanner.HlsArguments(plan, "/m/a.mkv", 0, new TranscodingOptions());
        Assert.Equal("copy", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("copy", args[args.IndexOf("-c:a") + 1]);
        Assert.DoesNotContain("-ss", args);
    }

    [Fact]
    public void TranscodeScalesDownTallVideo()
    {
        var plan = PlaybackPlanner.Decide(Probe("matroska,webm", V("hevc", 2160, "yuv420p10le", "Main 10")), ".mkv", null, null);
        var args = PlaybackPlanner.HlsArguments(plan, "/m/a.mkv", 0, new TranscodingOptions { MaxHeight = 1080 });
        Assert.Contains(args, a => a.Contains("scale=-2:1080"));
    }
}

