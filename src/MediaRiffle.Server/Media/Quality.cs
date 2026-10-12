namespace MediaRiffle.Server.Media;

/// <summary>One step of the quality ladder: video scaled to at most <see cref="Height"/> lines at about <see cref="VideoKbps"/>.</summary>
public sealed record QualityLevel(string Id, string Label, int Height, int VideoKbps, int AudioKbps)
{
    public int TotalKbps => VideoKbps + AudioKbps;
}

/// <summary>
/// The qualities a viewer can pick (or Auto picks for them), best first. "original" isn't a level: it's the file
/// itself, sent as it is or remuxed, when the device can decode it; otherwise the best level that fits. "auto" is an
/// adaptive stream of all the levels the user may have, which the player switches between as the connection allows.
/// </summary>
public static class Quality
{
    public const string Original = "original";
    public const string Auto = "auto";

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
    /// better level than allowed) gets the cap. Auto stays Auto (its levels are capped, see <see cref="AdaptiveLevels"/>).
    /// </summary>
    public static string Clamp(string? requested, string? userCap)
    {
        var req = string.IsNullOrEmpty(requested) ? Original : requested;
        if (req == Auto) return Auto;
        if (Level(userCap) is not { } cap) return req == Original || Level(req) is not null ? req : Original;
        if (req == Original) return cap.Id;
        return Level(req) is { } lvl && lvl.Height <= cap.Height ? lvl.Id : cap.Id;
    }

    /// <summary>
    /// The levels of an adaptive stream of a picture <paramref name="sourceHeight"/> lines high (0: not known), best
    /// first: those within the server's maximum and the user's cap, none bigger than the picture except one level
    /// above it when the picture falls between levels (that level then keeps the picture's own size). Never empty.
    /// </summary>
    public static IReadOnlyList<QualityLevel> AdaptiveLevels(int sourceHeight, int maxHeight, string? userCap)
    {
        var cap = Level(userCap);
        var allowed = Ladder.Where(l => l.Height <= maxHeight && (cap is null || l.Height <= cap.Height)).ToList();
        if (allowed.Count == 0) allowed = [Ladder[^1]];
        if (sourceHeight <= 0) return allowed;
        var levels = allowed.Where(l => l.Height <= sourceHeight).ToList();
        var above = allowed.LastOrDefault(l => l.Height > sourceHeight);
        if (above is not null && (levels.Count == 0 || levels[0].Height < sourceHeight)) levels.Insert(0, above);
        return levels;
    }
}

/// <summary>What the viewer's browser says it can decode, beyond H.264 (which every browser does).</summary>
public sealed record ClientCaps(bool Hevc, bool Hevc10)
{
    public static readonly ClientCaps H264Only = new(false, false);
}
