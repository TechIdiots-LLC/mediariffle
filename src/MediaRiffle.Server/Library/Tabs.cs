using MediaRiffle.Server.Data;
using MediaRiffle.Server.Options;

namespace MediaRiffle.Server.Library;

/// <summary>
/// The top tabs the media is split into: Shows, Movies, Music and Games. Lists and categories can hold all of them;
/// each tab shows and plays only its own part.
/// </summary>
public static class Tabs
{
    public const string Shows = "shows", Movies = "movies", Music = "music", Games = "games";

    /// <summary>The tab shows and movies shared before they had one each: both (old links, and apps that still ask).</summary>
    public const string Videos = "videos";

    public static string Of(LibraryKind kind) => kind switch
    {
        LibraryKind.Movies => Movies,
        LibraryKind.Music => Music,
        LibraryKind.Games => Games,
        _ => Shows,
    };

    public static string Of(MediaKind kind) => kind switch
    {
        MediaKind.Track => Music,
        MediaKind.Movie => Movies,
        _ => Shows,
    };

    /// <summary>The tabs a library's kinds put it under, in order.</summary>
    public static IReadOnlyList<string> Of(LibraryInfo library) => library.Kinds.Select(Of).Distinct().ToList();

    /// <summary>The kinds of file a tab holds; null for a tab that isn't one (or none given: everything).</summary>
    public static MediaKind[]? Kinds(string? tab) => tab?.Trim().ToLowerInvariant() switch
    {
        Shows => [MediaKind.Episode],
        Movies => [MediaKind.Movie],
        Videos => [MediaKind.Episode, MediaKind.Movie],
        Music => [MediaKind.Track],
        Games => [],
        _ => null,
    };

    /// <summary>The tabs with an enabled library behind them, in order (none while there are no libraries).</summary>
    public static IReadOnlyList<string> For(IEnumerable<LibraryInfo> libraries)
    {
        var have = libraries.Where(l => l.Enabled).SelectMany(l => l.Kinds.Select(Of)).ToHashSet();
        return new[] { Shows, Movies, Music, Games }.Where(have.Contains).ToList();
    }
}
