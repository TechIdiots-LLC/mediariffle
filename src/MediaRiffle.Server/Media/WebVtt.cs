using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaRiffle.Server.Media;

/// <summary>
/// Moves WebVTT cues to a stream's clock. A stream that starts 30 s into a file shows the file's 1:00 cue at 0:30.
/// Done here rather than with ffmpeg's -ss, which subtitle files ignore (they can't seek, and ffmpeg then moves the
/// first cue to zero instead).
/// </summary>
public static partial class WebVtt
{
    [GeneratedRegex(@"^(?<a>(?:\d+:)?\d{1,2}:\d{2}\.\d{3})\s+-->\s+(?<b>(?:\d+:)?\d{1,2}:\d{2}\.\d{3})(?<rest>.*)$")]
    private static partial Regex Timing();

    public static string Shift(string vtt, double seconds)
    {
        if (seconds <= 0) return vtt;
        var blocks = vtt.Replace("\r\n", "\n").Split("\n\n");
        var output = new List<string>();
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            int timingLine = Array.FindIndex(lines, l => Timing().IsMatch(l));
            if (timingLine < 0)
            {
                output.Add(block);   // the header, NOTE and STYLE blocks
                continue;
            }
            var m = Timing().Match(lines[timingLine]);
            var start = Parse(m.Groups["a"].Value) - seconds;
            var end = Parse(m.Groups["b"].Value) - seconds;
            if (end <= 0) continue;   // over before the stream starts
            lines[timingLine] = $"{Format(Math.Max(0, start))} --> {Format(end)}{m.Groups["rest"].Value}";
            output.Add(string.Join('\n', lines));
        }
        return string.Join("\n\n", output);
    }

    private static double Parse(string t)
    {
        var parts = t.Split(':');
        double s = double.Parse(parts[^1], CultureInfo.InvariantCulture);
        if (parts.Length >= 2) s += int.Parse(parts[^2], CultureInfo.InvariantCulture) * 60;
        if (parts.Length >= 3) s += int.Parse(parts[^3], CultureInfo.InvariantCulture) * 3600;
        return s;
    }

    private static string Format(double s)
    {
        var t = TimeSpan.FromMilliseconds(Math.Round(s * 1000));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
