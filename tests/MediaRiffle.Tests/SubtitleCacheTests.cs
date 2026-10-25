using MediaRiffle.Server.Media;

namespace MediaRiffle.Tests;

public class SubtitleCacheTests
{
    private static ProbeResult Probe() => ProbeResult.Parse("""
        { "format": { "format_name": "matroska,webm", "duration": "5800" },
          "streams": [ { "index": 0, "codec_type": "video", "codec_name": "hevc", "width": 3840, "height": 2160 },
                       { "index": 1, "codec_type": "audio", "codec_name": "dts", "channels": 6 },
                       { "index": 2, "codec_type": "subtitle", "codec_name": "hdmv_pgs_subtitle" },
                       { "index": 3, "codec_type": "subtitle", "codec_name": "subrip" },
                       { "index": 4, "codec_type": "subtitle", "codec_name": "dvd_subtitle" } ] }
        """);

    [Fact]
    public void PgsAndTextTracksAreKeptDvdOnesAreNot()
    {
        var tracks = SubtitleTrack.Of(Probe());
        Assert.Equal([new SubtitleTrack(2, true), new SubtitleTrack(3, false)], tracks);
        Assert.True(Probe().Subtitles.Single(s => s.Index == 2).IsPgs);
        Assert.False(Probe().Subtitles.Single(s => s.Index == 4).IsPgs);   // DVD: still burned in
    }

    [Fact]
    public void EveryTrackComesOutOfOneRead()
    {
        var args = SubtitleCache.ExtractArguments("/m/a.mkv",
            [(new SubtitleTrack(2, true), "/c/a-2.sup.part"), (new SubtitleTrack(3, false), "/c/a-3.vtt.part")]);
        Assert.Single(args, a => a == "-i");
        Assert.Equal("0:2", args[args.IndexOf("-map") + 1]);
        Assert.Equal("copy", args[args.IndexOf("-c:s") + 1]);   // PGS as it is
        Assert.Equal("sup", args[args.IndexOf("-f") + 1]);
        Assert.Equal("0:3", args[args.LastIndexOf("-map") + 1]);
        Assert.Equal("webvtt", args[args.LastIndexOf("-c:s") + 1]);
        Assert.Equal("/c/a-3.vtt.part", args[^1]);
    }

    [Fact]
    public void AWindowReadsFromWhereItStartsOnTheFilesClock()
    {
        var args = SubtitleCache.WindowArguments("/m/a.mkv", 2, 1234.5);
        Assert.True(args.IndexOf("-ss") < args.IndexOf("-i"));   // reads from there, not from the start
        Assert.Equal("1234.5", args[args.IndexOf("-ss") + 1]);
        Assert.Equal("300", args[args.IndexOf("-t") + 1]);
        Assert.Equal("1234.5", args[args.IndexOf("-output_ts_offset") + 1]);   // back on the file's clock
        Assert.Equal("pipe:1", args[^1]);
    }
}
