using MediaRiffle.Server.Media;

namespace MediaRiffle.Tests;

public class WebVttTests
{
    private const string Vtt = "WEBVTT\n\n00:00:02.000 --> 00:00:06.000\nFirst\n\n00:00:28.000 --> 00:00:33.500 align:start\nAcross the start\n\n01:00.000 --> 01:05.000\nOne minute in\n";

    [Fact]
    public void ShiftsCuesToTheStreamsClock()
    {
        var shifted = WebVtt.Shift(Vtt, 30);
        Assert.DoesNotContain("First", shifted);                               // over before the stream starts
        Assert.Contains("00:00:00.000 --> 00:00:03.500 align:start\nAcross the start", shifted);
        Assert.Contains("00:00:30.000 --> 00:00:35.000\nOne minute in", shifted);
        Assert.StartsWith("WEBVTT", shifted);
    }

    [Fact]
    public void NoShiftAtTheStart() => Assert.Equal(Vtt, WebVtt.Shift(Vtt, 0));

    [Fact]
    public void HandlesWindowsLineEndingsAndHours()
    {
        var shifted = WebVtt.Shift("WEBVTT\r\n\r\n01:00:10.250 --> 01:00:12.000\r\nLate\r\n", 3600);
        Assert.Contains("00:00:10.250 --> 00:00:12.000", shifted);
    }
}
