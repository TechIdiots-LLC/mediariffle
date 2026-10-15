using System.Text.RegularExpressions;

namespace MediaRiffle.Server.Library;

/// <param name="Series">Another series of the show (a spin-off, a version abroad), or "" for the show's own.</param>
public sealed record EpisodeInfo(string Show, int Season, int? Episode, string Title, string Series = "");

public sealed record MovieInfo(string Title, int? Year, string GroupKey, int Part);

/// <summary>What a music file's path says; any part can be missing.</summary>
/// <param name="Folder">The album's folder, relative to the library ("" at its root); a CD1/Disc 2 folder is part of it.</param>
public sealed record TrackInfo(string? Artist, string? Album, int? Disc, int? Track, string Title, string Folder);

/// <summary>
/// Reads shows, seasons, episodes and movies from paths laid out as the original Tongue expected:
/// <c>Show/Season 01/Show - S01E02 - Title.mkv</c>, and <c>Movie (1998).mkv</c> or <c>Movie/cd1.avi</c>.
/// </summary>
public static partial class MediaNameParser
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts", ".webm", ".flv", ".ogv", ".vob", ".divx", ".3gp",
    };

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".ogg", ".oga", ".opus", ".wav", ".wma", ".alac", ".aiff", ".aif", ".ape", ".wv", ".mka",
    };

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    /// <summary>Whether a library of this kind takes the file: audio for Music, video for the others.</summary>
    public static bool Belongs(string path, Options.LibraryKind kind) =>
        kind == Options.LibraryKind.Music ? IsAudio(path) : IsVideo(path);

    // "01 - Title", "01. Title", "1-03 Title" (disc 1, track 3), "Artist - 01 - Title"
    [GeneratedRegex(@"^(?:(?<pre>.+?)\s+-\s+)?(?:(?<d>\d)-)?(?<t>\d{1,3})(?:\s*[-.]\s*|\s+)(?<title>.+)$")]
    private static partial Regex TrackFile();

    /// <summary>
    /// A track at <paramref name="relativePath"/> in a Music library, from its folders and file name, for files
    /// without tags: <c>Artist/Album/01 - Title.mp3</c>. Tags, when a file has them, win over all of this.
    /// </summary>
    public static TrackInfo ParseTrack(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = Path.GetFileNameWithoutExtension(parts[^1]);
        string? artist = parts.Length >= 3 ? parts[^3] : null;
        string? album = parts.Length >= 2 ? parts[^2] : null;
        int? disc = null, track = null;
        var title = fileName;
        var folderParts = parts[..^1];
        // A "CD1" / "Disc 2" folder inside the album folder is a disc, not the album
        if (album is not null && PartMarker().Match(album) is { Success: true } dm && CleanTitle(album.Remove(dm.Index, dm.Length)).Length == 0)
        {
            disc = int.Parse(dm.Groups["n"].Value);
            album = parts.Length >= 3 ? parts[^3] : null;
            artist = parts.Length >= 4 ? parts[^4] : null;
            folderParts = folderParts[..^1];
        }
        var m = TrackFile().Match(fileName);
        if (m.Success)
        {
            track = int.Parse(m.Groups["t"].Value);
            if (m.Groups["d"].Success) disc ??= int.Parse(m.Groups["d"].Value);
            title = m.Groups["title"].Value;
            if (artist is null && m.Groups["pre"].Success) artist = m.Groups["pre"].Value;
        }
        title = title.Replace('_', ' ').Trim(' ', '-', '.');
        return new TrackInfo(artist?.Trim(), album?.Trim(), disc, track, title.Length == 0 ? fileName : title, string.Join('/', folderParts));
    }

    [GeneratedRegex(@"[Ss](?<s>\d{1,3})[ ._-]*[Ee](?<e>\d{1,4})")]
    private static partial Regex SxxEyy();

    [GeneratedRegex(@"\b(?<s>\d{1,2})x(?<e>\d{2,3})\b")]
    private static partial Regex NxNN();

    [GeneratedRegex(@"^(?:season|series|staffel|saison|temporada)?[ ._-]*(?<n>\d{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"^(?:specials?|extras?)$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialsFolder();

    // Another series of the show in one of its folders: "UK - Season 01", "All Stars Season 2", "Down Under - S01"
    [GeneratedRegex(@"^(?<v>.*?[^ ._-])[ ._]*-?[ ._]*(?:(?:season|series|staffel|saison|temporada)[ ._-]*|s(?=\d))(?<n>\d{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesSeasonFolder();

    [GeneratedRegex(@"(?:^|[ ._\-\[(])(?:cd|disc|disk|part|pt)[ ._-]*(?<n>\d{1,2})(?=$|[ ._\-\])])", RegexOptions.IgnoreCase)]
    private static partial Regex PartMarker();

    [GeneratedRegex(@"[\[(]?\b(?<y>(?:19|20)\d{2})\b[\])]?")]
    private static partial Regex Year();

    [GeneratedRegex(@"\b(?:2160p|1080p|720p|480p|x264|x265|h\.?264|h\.?265|hevc|bluray|blu-ray|brrip|bdrip|webrip|web-dl|hdtv|dvdrip|xvid|divx|remux|proper|repack)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseJunk();

    /// <summary>
    /// An episode at <paramref name="relativePath"/> in a Shows library (<c>Show/[Season]/file</c>), or null for a
    /// file directly in the library root. A show's other series (its spin-offs and versions abroad) are told apart
    /// by their season folders, <c>Show/UK - Season 01/…</c>, or for files loose in the show's folder by a name that
    /// isn't the show's, <c>Show/drag.race.espana.s01e01.mkv</c>.
    /// </summary>
    public static EpisodeInfo? ParseEpisode(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var show = parts[0];
        var fileName = Path.GetFileNameWithoutExtension(parts[^1]);

        int? season = null;
        string series = string.Empty;
        if (parts.Length >= 3)
        {
            var folder = parts[^2];
            var m = SeasonFolder().Match(folder);
            if (m.Success) season = int.Parse(m.Groups["n"].Value);
            else if (SpecialsFolder().IsMatch(folder)) season = 0;
            else if (SeriesSeasonFolder().Match(folder) is { Success: true } sm)
            {
                season = int.Parse(sm.Groups["n"].Value);
                series = SeriesName(sm.Groups["v"].Value, show);
            }
        }

        int? episode = null;
        string title = fileName;
        var se = SxxEyy().Match(fileName);
        if (!se.Success) se = NxNN().Match(fileName);
        if (se.Success)
        {
            season ??= int.Parse(se.Groups["s"].Value);
            episode = int.Parse(se.Groups["e"].Value);
            title = CleanTitle(fileName[(se.Index + se.Length)..]);
            if (title.Length == 0) title = $"Episode {episode}";
            // Loose in the show's folder: named for another series of it ("drag.race.espana…" in "Ru Pauls Drag Race")
            if (parts.Length == 2) series = SeriesName(CleanTitle(fileName[..se.Index]), show);
        }
        else
        {
            title = CleanTitle(fileName);
        }
        return new EpisodeInfo(show, season ?? 1, episode, title, series);
    }

    /// <summary>
    /// A series' name, or "" for the show's own: a name that's the show's, or part of it, or has it in it
    /// ("Office" or "The Office US" in "The Office") is the show itself. A name as a folder spells it is kept; an
    /// all lower case one (from a file name) gets capitals.
    /// </summary>
    public static string SeriesName(string name, string show)
    {
        var cleaned = CleanTitle(name);
        string Letters(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var n = Letters(cleaned);
        var s = Letters(show);
        if (n.Length == 0 || s.Contains(n) || n.Contains(s)) return string.Empty;
        if (cleaned.Any(char.IsUpper)) return cleaned;
        // "drag race espana" → "Drag Race Espana"
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>
    /// A movie at <paramref name="relativePath"/> in a Movies library. A file in its own folder is named by the
    /// folder (so <c>Fargo/cd1.avi</c> is "Fargo", part 1); cd/disc/part markers group the parts of one movie.
    /// </summary>
    public static MovieInfo ParseMovie(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = Path.GetFileNameWithoutExtension(parts[^1]);
        var folder = parts.Length >= 2 ? string.Join('/', parts[..^1]) : string.Empty;

        int part = 1;
        var pm = PartMarker().Match(fileName);
        string nameWithoutPart = fileName;
        if (pm.Success)
        {
            part = int.Parse(pm.Groups["n"].Value);
            nameWithoutPart = fileName.Remove(pm.Index, pm.Length);
        }

        // In its own folder the folder is the movie; at the library root (or a shared folder like HD/SD) the file is
        var source = parts.Length >= 2 && (pm.Success || IsGenericName(nameWithoutPart)) ? parts[^2] : nameWithoutPart;
        int? year = null;
        // The last year after the start: "2001 A Space Odyssey (1968)" is from 1968
        var ym = Year().Matches(source).LastOrDefault(m => m.Index > 0);
        string title = source;
        if (ym is not null)
        {
            year = int.Parse(ym.Groups["y"].Value);
            title = source[..ym.Index];
        }
        title = CleanTitle(title);
        if (title.Length == 0) title = CleanTitle(source);

        var groupKey = parts.Length >= 2 && (pm.Success || IsGenericName(nameWithoutPart))
            ? folder
            : (folder.Length > 0 ? folder + "/" : "") + CleanTitle(nameWithoutPart).ToLowerInvariant();
        return new MovieInfo(title, year, groupKey, part);
    }

    private static bool IsGenericName(string name) => CleanTitle(name).Length == 0;

    /// <summary>Dots and underscores to spaces, release tags dropped, separators trimmed.</summary>
    public static string CleanTitle(string text)
    {
        var t = text.Replace('.', ' ').Replace('_', ' ');
        t = ReleaseJunk().Replace(t, "");
        t = Regex.Replace(t, @"\s{2,}", " ");
        return t.Trim(' ', '-', '[', ']', '(', ')');
    }
}
