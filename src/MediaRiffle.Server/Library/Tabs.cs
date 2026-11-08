using MediaRiffle.Server.Data;
using MediaRiffle.Server.Options;

namespace MediaRiffle.Server.Library;

/// <summary>
/// The top tabs the media is split into: Videos (show and movie libraries) and Music. Lists and categories can hold
/// both; each tab shows and plays only its own part of them.
/// </summary>
public static class Tabs
{
    public const string Videos = "videos", Music = "music";

    public static string Of(LibraryKind kind) => kind == LibraryKind.Music ? Music : Videos;

    public static string Of(MediaKind kind) => kind == MediaKind.Track ? Music : Videos;

    /// <summary>The kinds of file a tab holds; null for a tab that isn't one (or none given: everything).</summary>
    public static MediaKind[]? Kinds(string? tab) => tab?.Trim().ToLowerInvariant() switch
    {
        Videos => [MediaKind.Episode, MediaKind.Movie],
        Music => [MediaKind.Track],
        _ => null,
    };

    /// <summary>The tabs with a library behind them, in order; Videos alone when there are no libraries yet.</summary>
    public static IReadOnlyList<string> For(IEnumerable<LibraryInfo> libraries)
    {
        var have = libraries.Select(l => Of(l.Kind)).ToHashSet();
        var tabs = new[] { Videos, Music }.Where(have.Contains).ToList();
        return tabs.Count > 0 ? tabs : [Videos];
    }
}
