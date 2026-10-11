using System.Globalization;
using System.Text.Json;

namespace MediaRiffle.Server.Media;

public enum StreamType { Video, Audio, Subtitle, Other }

/// <summary>One stream of a file, from ffprobe.</summary>
public sealed record ProbeStream(
    int Index, StreamType Type, string Codec, string? Profile, string? PixelFormat,
    int Width, int Height, int Channels, string? Language, string? Title, bool IsDefault, bool IsForced)
{
    /// <summary>Text subtitles can be turned into WebVTT; image ones (PGS, DVD) can only be burned into the video.</summary>
    public bool IsTextSubtitle => Type == StreamType.Subtitle &&
        Codec is "subrip" or "srt" or "ass" or "ssa" or "mov_text" or "webvtt" or "text";

    public string Label
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title)) parts.Add(Title!);
            if (!string.IsNullOrWhiteSpace(Language) && Language != "und") parts.Add(Language!);
            if (Type == StreamType.Audio) parts.Add(Channels switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => $"{Channels} ch" });
            parts.Add(Codec);
            if (IsForced) parts.Add("forced");
            return string.Join(" · ", parts.Distinct());
        }
    }
}

/// <summary>What ffprobe says about a file: its container, duration and streams.</summary>
public sealed class ProbeResult
{
    public string FormatName { get; init; } = string.Empty;
    public double DurationSeconds { get; init; }
    public List<ProbeStream> Streams { get; init; } = new();

    public ProbeStream? Video => Streams.FirstOrDefault(s => s.Type == StreamType.Video);
    public IEnumerable<ProbeStream> Audio => Streams.Where(s => s.Type == StreamType.Audio);
    public IEnumerable<ProbeStream> Subtitles => Streams.Where(s => s.Type == StreamType.Subtitle);

    /// <summary>The audio stream a player uses when none is picked: the default one, else the first.</summary>
    public ProbeStream? DefaultAudio => Audio.FirstOrDefault(a => a.IsDefault) ?? Audio.FirstOrDefault();

    /// <summary>Parses ffprobe's <c>-print_format json -show_format -show_streams</c> output.</summary>
    public static ProbeResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : default;
        var streams = new List<ProbeStream>();
        if (root.TryGetProperty("streams", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in list.EnumerateArray())
            {
                var type = Str(s, "codec_type") switch
                {
                    "video" => StreamType.Video,
                    "audio" => StreamType.Audio,
                    "subtitle" => StreamType.Subtitle,
                    _ => StreamType.Other,
                };
                var disposition = s.TryGetProperty("disposition", out var d) ? d : default;
                var tags = s.TryGetProperty("tags", out var t) ? t : default;
                // Cover art is stored as a video stream with attached_pic; it isn't the video
                if (type == StreamType.Video && Int(disposition, "attached_pic") == 1) type = StreamType.Other;
                streams.Add(new ProbeStream(
                    Int(s, "index"), type, Str(s, "codec_name") ?? "unknown", Str(s, "profile"), Str(s, "pix_fmt"),
                    Int(s, "width"), Int(s, "height"), Int(s, "channels"),
                    Str(tags, "language"), Str(tags, "title"),
                    Int(disposition, "default") == 1, Int(disposition, "forced") == 1));
            }
        }
        return new ProbeResult
        {
            FormatName = Str(format, "format_name") ?? string.Empty,
            DurationSeconds = double.TryParse(Str(format, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur) ? dur : 0,
            Streams = streams,
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.Number ? v.TryGetInt32(out var n) : int.TryParse(v.ToString(), out n)) ? n : 0;
}
