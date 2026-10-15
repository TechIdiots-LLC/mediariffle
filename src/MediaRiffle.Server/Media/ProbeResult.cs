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
    /// <summary>When the first stream starts on the file's clock (ffprobe's start_time); 0 if unknown.</summary>
    public double StartSeconds { get; init; }

    /// <summary>
    /// How long the picture and sound last, counted from where they start: what an adaptive stream (on a clock that
    /// starts at the first frame) covers. Matroska counts its duration from 0 (a file starting at 2.5 s with 120 s in
    /// it says 122.5); MPEG-TS and MP4 say how long what's in them lasts.
    /// </summary>
    public double ContentSeconds => FormatName.Contains("matroska") && StartSeconds > 0 ? Math.Max(0, DurationSeconds - StartSeconds) : DurationSeconds;
    /// <summary>The whole file's average bitrate (video and all audio), in kbit/s; 0 if unknown.</summary>
    public int BitRateKbps { get; init; }
    public List<ProbeStream> Streams { get; init; } = new();

    /// <summary>
    /// The file's tags (title, artist, album, track, …), any case: the container's, and the first audio stream's
    /// where the container has none of its own (Ogg and Opus keep them there).
    /// </summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The first of these tags that's set, trimmed; null when none is.</summary>
    public string? Tag(params string[] names) =>
        names.Select(n => Tags.TryGetValue(n, out var v) ? v.Trim() : null).FirstOrDefault(v => !string.IsNullOrEmpty(v));

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
        var fileTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddTags(fileTags, format);
        if (root.TryGetProperty("streams", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in list.EnumerateArray())
            {
                if (Str(s, "codec_type") == "audio" && !streams.Any(x => x.Type == StreamType.Audio)) AddTags(fileTags, s);
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
            StartSeconds = double.TryParse(Str(format, "start_time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var st) && st > 0 ? st : 0,
            BitRateKbps = long.TryParse(Str(format, "bit_rate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var br) ? (int)(br / 1000) : 0,
            Streams = streams,
            Tags = fileTags,
        };
    }

    // A tag already set (from the container) is kept
    private static void AddTags(Dictionary<string, string> into, JsonElement owner)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object) return;
        foreach (var t in tags.EnumerateObject())
            if (t.Value.ValueKind == JsonValueKind.String && !into.ContainsKey(t.Name)) into[t.Name] = t.Value.GetString()!;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.Number ? v.TryGetInt32(out var n) : int.TryParse(v.ToString(), out n)) ? n : 0;
}
