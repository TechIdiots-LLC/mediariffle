using MediaRiffle.Server.Data;
using MediaRiffle.Server.Options;

namespace MediaRiffle.Server.Library;

/// <summary>
/// The top tabs the media is split into: Videos (show and movie libraries), Music and Games. Lists and categories can
/// hold all three; each tab shows and plays only its own part of them.
/// </summary>
public static class Tabs
{
    public const string Videos = "videos", Music = "music", Games = "games";

    public static string Of(LibraryKind kind) => kind switch
    {
        LibraryKind.Music => Music,
        LibraryKind.Games => Games,
        _ => Videos,
    };

    public static string Of(MediaKind kind) => kind == MediaKind.Track ? Music : Videos;

    /// <summary>The kinds of file a tab holds; null for a tab that isn't one (or none given: everything).</summary>
    public static MediaKind[]? Kinds(string? tab) => tab?.Trim().ToLowerInvariant() switch
    {
        Videos => [MediaKind.Episode, MediaKind.Movie],
        Music => [MediaKind.Track],
        Games => [],
        _ => null,
    };

    /// <summary>The tabs with an enabled library behind them, in order (none while there are no libraries).</summary>
    public static IReadOnlyList<string> For(IEnumerable<LibraryInfo> libraries)
    {
        var have = libraries.Where(l => l.Enabled).Select(l => Of(l.Kind)).ToHashSet();
        return new[] { Videos, Music, Games }.Where(have.Contains).ToList();
    }
}
