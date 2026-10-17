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
    public void ARemuxFromPartWayStartsTheSoundWithThePicture()
    {
        // Copied video starts at a keyframe before the time; without -noaccurate_seek the sound would start at the
        // time itself, and in fMP4 browsers play it that much early
        var plan = PlaybackPlanner.Decide(Probe("matroska,webm", V("h264"), A(1, "ac3", 6)), ".mkv", null, null);
        var args = PlaybackPlanner.HlsArguments(plan, "/m/a.mkv", 600, new TranscodingOptions());
        Assert.Equal(PlaybackMode.Remux, plan.Mode);
        Assert.True(args.IndexOf("-noaccurate_seek") is >= 0 and var i && i < args.IndexOf("-ss") && args.IndexOf("-ss") < args.IndexOf("-i"));
        Assert.Equal("600", args[args.IndexOf("-ss") + 1]);
    }

    [Theory]
    [InlineData("#tb 0: 1/1000\n#media_type 0: video\n0,     598806,     598973,       41,   270874, 0xef7dc90e\n", 598.973)]
    [InlineData("#extradata 0: 149, 0xad312720\n#tb 0: 1/24000\n0,   35924889,   35925890,     1001,   694585, 0xd90dac62\n", 1496.912083)]
    public void WhereARemuxReallyStarts(string framecrc, double seconds) =>
        Assert.Equal(seconds, Ffmpeg.FirstPacketSeconds(framecrc)!.Value, 5);

    [Fact]
    public void NoPacketNoStart() => Assert.Null(Ffmpeg.FirstPacketSeconds("#tb 0: 1/1000\n"));

    [Fact]
    public void TranscodeScalesDownTallVideo()
    {
        var plan = PlaybackPlanner.Decide(Probe("matroska,webm", V("hevc", 2160, "yuv420p10le", "Main 10")), ".mkv", null, null);
        var args = PlaybackPlanner.HlsArguments(plan, "/m/a.mkv", 0, new TranscodingOptions { MaxHeight = 1080 });
        Assert.Contains(args, a => a.Contains("scale=-2:1080"));
    }
}

