namespace MediaRiffle.Server.Media;

/// <summary>One step of the quality ladder: video scaled to at most <see cref="Height"/> lines at about <see cref="VideoKbps"/>.</summary>
public sealed record QualityLevel(string Id, string Label, int Height, int VideoKbps, int AudioKbps)
{
    public int TotalKbps => VideoKbps + AudioKbps;
}

/// <summary>
/// The qualities a viewer can pick (or Auto picks for them), best first. "original" isn't a level: it's the file
/// itself, sent as it is or remuxed, when the device can decode it; otherwise the best level that fits.
/// </summary>
public static class Quality
{
    public const string Original = "original";

    public static readonly IReadOnlyList<QualityLevel> Ladder =
    [
        new("1080", "1080p", 1080, 8000, 192),
        new("720", "720p", 720, 4000, 160),
        new("480", "480p", 480, 1500, 128),
        new("360", "360p", 360, 800, 96),
    ];

    public static QualityLevel? Level(string? id) => Ladder.FirstOrDefault(l => l.Id == id);

    /// <summary>
    /// The quality to use for a request, kept within the user's cap: a capped user asking for Original (or a
    /// better level than allowed) gets the cap.
    /// </summary>
    public static string Clamp(string? requested, string? userCap)
    {
        var req = string.IsNullOrEmpty(requested) ? Original : requested;
        if (Level(userCap) is not { } cap) return req == Original || Level(req) is not null ? req : Original;
        if (req == Original) return cap.Id;
        return Level(req) is { } lvl && lvl.Height <= cap.Height ? lvl.Id : cap.Id;
    }
}

/// <summary>What the viewer's browser says it can decode, beyond H.264 (which every browser does).</summary>
public sealed record ClientCaps(bool Hevc, bool Hevc10)
{
    public static readonly ClientCaps H264Only = new(false, false);
}
