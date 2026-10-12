using MediaRiffle.Server.Media;
using MediaRiffle.Server.Options;

namespace MediaRiffle.Tests;

public class AdaptiveTests
{
    private static ProbeStream V(int width, int height) =>
        new(0, StreamType.Video, "h264", "High", "yuv420p", width, height, 0, null, null, true, false);

    private static ProbeStream A() => new(1, StreamType.Audio, "ac3", null, null, 0, 0, 6, null, null, true, false);

    private static PlaybackPlan Plan(ProbeStream? v, ProbeStream? a, params string[] levels) =>
        new(PlaybackMode.Transcode, v, a, null, "test", Levels: levels.Select(id => Quality.Level(id)!).ToList());

    [Theory]
    [InlineData(1080, 1080, null, "1080,720,480,360")]
    [InlineData(2160, 1080, null, "1080,720,480,360")]   // never above the server's maximum
    [InlineData(720, 1080, null, "720,480,360")]         // nothing bigger than the picture
    [InlineData(576, 1080, null, "720,480,360")]         // between levels: the one above keeps the picture's size
    [InlineData(1080, 1080, "480", "480,360")]           // the user's cap
    [InlineData(240, 1080, null, "360")]                 // tiny: one level, at its own size
    [InlineData(0, 720, null, "720,480,360")]            // not known: all that's allowed
    public void Levels(int source, int max, string? cap, string expected) =>
        Assert.Equal(expected, string.Join(",", Quality.AdaptiveLevels(source, max, cap).Select(l => l.Id)));

    [Fact]
    public void AutoIsNotClampedAway() => Assert.Equal(Quality.Auto, Quality.Clamp(Quality.Auto, "480"));

    [Fact]
    public void VariantPlaylistCoversTheFile()
    {
        var text = PlaybackPlanner.VariantPlaylist(10.5, 4);
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:VOD", text);
        Assert.Contains("#EXT-X-TARGETDURATION:5", text);
        Assert.Contains("#EXTINF:4.000000,\nseg00000.ts", text);
        Assert.Contains("#EXTINF:4.000000,\nseg00001.ts", text);
        Assert.Contains("#EXTINF:2.500000,\nseg00002.ts", text);
        Assert.DoesNotContain("seg00003.ts", text);
        Assert.EndsWith("#EXT-X-ENDLIST\n", text);
    }

    [Theory]
    [InlineData(8.0, 2)]
    [InlineData(8.2, 2)]    // a last sliver under a quarter second isn't a segment of its own
    [InlineData(8.3, 3)]
    [InlineData(0, 1)]
    public void SegmentCount(double duration, int expected) => Assert.Equal(expected, PlaybackPlanner.SegmentCount(duration, 4));

    [Fact]
    public void MasterListsTheStartLevelFirstAndKeepsNumbers()
    {
        var text = PlaybackPlanner.MasterPlaylist(Plan(V(1920, 1080), A(), "1080", "720", "480"), first: 1);
        var lines = text.Split('\n');
        Assert.Contains("RESOLUTION=1280x720", lines[3]);
        Assert.Contains("NAME=\"720\"", lines[3]);
        Assert.Equal("v1/index.m3u8", lines[4]);
        Assert.Equal("v0/index.m3u8", lines[6]);
        Assert.Equal("v2/index.m3u8", lines[8]);
        Assert.Contains("BANDWIDTH=8192000", lines[5]);   // video and audio
        Assert.Contains("mp4a.40.2", lines[3]);
    }

    [Fact]
    public void MasterWithoutAudio() =>
        Assert.DoesNotContain("mp4a", PlaybackPlanner.MasterPlaylist(Plan(V(1920, 1080), null, "720"), 0));

    [Theory]
    [InlineData(1920, 1080, 720, 1280, 720)]
    [InlineData(1920, 800, 720, 1728, 720)]    // wide picture
    [InlineData(1440, 1080, 480, 640, 480)]    // 4:3
    [InlineData(640, 360, 720, 640, 360)]      // smaller than the level: its own size
    public void OutputSize(int w, int h, int level, int ew, int eh) =>
        Assert.Equal((ew, eh), PlaybackPlanner.OutputSize(V(w, h), level));

    [Fact]
    public void SegmentArgumentsCutOnTheFilesClock()
    {
        var o = new TranscodingOptions { SegmentSeconds = 4 };
        var args = PlaybackPlanner.SegmentArguments(Plan(V(1920, 1080), A(), "720"), Quality.Level("720")!, "/m/a.mkv", 15, o);
        Assert.Equal("60", args[args.IndexOf("-ss") + 1]);
        Assert.True(args.IndexOf("-copyts") < args.IndexOf("-i"));
        Assert.Contains("-start_at_zero", args);
        Assert.Equal("disabled", args[args.IndexOf("-avoid_negative_ts") + 1]);
        Assert.Equal("segment", args[args.IndexOf("-f") + 1]);
        Assert.Equal("4", args[args.IndexOf("-segment_time") + 1]);
        Assert.Equal("15", args[args.IndexOf("-segment_start_number") + 1]);
        Assert.Equal("expr:gte(t,n_forced*4)", args[args.IndexOf("-force_key_frames") + 1]);
        Assert.Contains(args, a => a.Contains("scale=-2:720"));
        Assert.Equal("4000k", args[args.IndexOf("-maxrate") + 1]);
        Assert.Equal("160k", args[args.IndexOf("-b:a") + 1]);
        Assert.Equal("seg%05d.ts", args[^1]);
    }

    [Fact]
    public void SegmentArgumentsAtZeroStillSeek()
    {
        var args = PlaybackPlanner.SegmentArguments(Plan(V(1920, 1080), A(), "720"), Quality.Level("720")!, "/m/a.mkv", 0, new TranscodingOptions());
        Assert.Equal("0", args[args.IndexOf("-ss") + 1]);
    }

    [Fact]
    public void NvencKeyframesAreIdr()
    {
        var args = PlaybackPlanner.SegmentArguments(Plan(V(1920, 1080), A(), "720"), Quality.Level("720")!, "/m/a.mkv", 0,
            new TranscodingOptions { Encoder = VideoEncoder.Nvenc });
        Assert.Equal("1", args[args.IndexOf("-forced-idr") + 1]);
    }

    [Theory]
    [InlineData("matroska,webm", 2.5, 122.5, 120.0)]   // Matroska counts from 0
    [InlineData("mpegts", 5.0, 60.0, 60.0)]            // MPEG-TS says how long it is
    [InlineData("matroska,webm", 0, 90.0, 90.0)]
    public void ContentLength(string format, double start, double duration, double expected) =>
        Assert.Equal(expected, new ProbeResult { FormatName = format, StartSeconds = start, DurationSeconds = duration }.ContentSeconds, 3);
}
