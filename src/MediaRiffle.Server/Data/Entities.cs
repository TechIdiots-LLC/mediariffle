using Microsoft.AspNetCore.Identity;

namespace MediaRiffle.Server.Data;

public sealed class AppUser : IdentityUser
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>The best quality this user may stream (a Quality.Ladder id), or null for no limit.</summary>
    public string? MaxQuality { get; set; }
    /// <summary>
    /// How the user likes Auto to pick (their own choice, within <see cref="MaxQuality"/>): "high", "medium", "low", or
    /// null for by the connection. See Media.Quality.Preferences.
    /// </summary>
    public string? PreferredQuality { get; set; }
    /// <summary>
    /// An authenticator app is set up (its key is Identity's authenticator key). Not Identity's TwoFactorEnabled, which
    /// would make every sign-in ask for a code: two-step verification guards Admin, not signing in.
    /// </summary>
    public bool AuthenticatorEnabled { get; set; }
    /// <summary>
    /// A PIN others on a shared device must enter to switch into this account (Switch user), hashed like a password;
    /// null for none.
    /// </summary>
    public string? SwitchPinHash { get; set; }
    /// <summary>
    /// The picture beside the user's name: "emoji:    public string? SwitchPinHash { get; set; }lt;n    public string? SwitchPinHash { get; set; }gt;" (one of the built-in ones), "photo:    public string? SwitchPinHash { get; set; }lt;version    public string? SwitchPinHash { get; set; }gt;" (their own, in the
    /// data folder's avatars), or null for the built-in one their id picks. See Security.Avatars.
    /// </summary>
    public string? Avatar { get; set; }
}

/// <summary>A one-time invitation to create an account. Only the token's SHA-256 is stored.</summary>
public sealed class Invite
{
    public int Id { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string CreatedByUserId { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
}

/// <summary>
/// A folder of media, added and changed by admins (Admin → Libraries). Like MediaLibraryManager's monitored folders:
/// a comma-separated list of tags applies to everything in it, and the tags are the categories viewers browse and
/// shuffle by.
/// </summary>
public sealed class MediaLibrary
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Options.LibraryKind Kind { get; set; }
    /// <summary>Absolute folder: a local path, a mount point, or a UNC path on Windows.</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>Comma-separated, e.g. "Cartoons, Kids". See <see cref="TagList"/>.</summary>
    public string? Tags { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public IReadOnlyList<string> TagList => ParseTags(Tags);

    public static IReadOnlyList<string> ParseTags(string? raw) =>
        raw is null ? [] : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Trimmed, without duplicates (ignoring case), joined with ", "; null when there are none.</summary>
    public static string? NormalizeTags(string? raw)
    {
        var tags = ParseTags(raw);
        return tags.Count == 0 ? null : string.Join(", ", tags);
    }
}

public sealed class Show
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public string Title { get; set; } = string.Empty;
    /// <summary>The show's folder, relative to the library.</summary>
    public string Folder { get; set; } = string.Empty;
    public List<Season> Seasons { get; set; } = new();
}

public sealed class Season
{
    public int Id { get; set; }
    public int ShowId { get; set; }
    public Show? Show { get; set; }
    /// <summary>0 for specials or a folder without a season number.</summary>
    public int Number { get; set; }
    /// <summary>Another series of the show ("UK", "All Stars": a spin-off or a version abroad), or "" for the show's own.</summary>
    public string Series { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>"Season 2", "Specials", "UK · Season 2".</summary>
    public static string TitleFor(string series, int number)
    {
        var season = number == 0 ? "Specials" : $"Season {number}";
        return series.Length == 0 ? season : $"{series} · {season}";
    }
}

public sealed class Movie
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    /// <summary>A key that groups the parts of one movie (cd1, cd2, …): its folder, or its file name without the part.</summary>
    public string GroupKey { get; set; } = string.Empty;
    public List<MediaFile> Parts { get; set; } = new();
}

/// <summary>An artist in a Music library: an album's artist ("Various Artists" for a compilation).</summary>
public sealed class Artist
{
    public const string Various = "Various Artists";

    public int Id { get; set; }
    public int LibraryId { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// An album in a Music library: the tracks in one folder with the same album tag (or, untagged, the folder itself).
/// </summary>
public sealed class Album
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public int ArtistId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    /// <summary>Groups its tracks: the folder, and the album tag lowercased ("Rock/Abbey Road|abbey road").</summary>
    public string GroupKey { get; set; } = string.Empty;
    /// <summary>The folder its tracks are in, relative to the library, for its cover art.</summary>
    public string Folder { get; set; } = string.Empty;
    /// <summary>Its artist came from album artist tags (or folders), not guessed from the first track's artist.</summary>
    public bool ArtistFromAlbumTag { get; set; }
}

public enum MediaKind { Episode, Movie, Track }

/// <summary>One video file in a library, with what ffprobe found in it.</summary>
public sealed class MediaFile
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    /// <summary>Path relative to the library folder, with '/' separators.</summary>
    public string RelativePath { get; set; } = string.Empty;
    public MediaKind Kind { get; set; }

    public int? ShowId { get; set; }
    public int? SeasonId { get; set; }
    public int? EpisodeNumber { get; set; }
    public int? MovieId { get; set; }
    public int PartNumber { get; set; } = 1;

    /// <summary>A music track's album, disc and track number, and its own artist (on a compilation, not the album's).</summary>
    public int? AlbumId { get; set; }
    public int? DiscNumber { get; set; }
    public int? TrackNumber { get; set; }
    public string? TrackArtist { get; set; }

    public string Title { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }

    public double DurationSeconds { get; set; }
    public string Container { get; set; } = string.Empty;
    public string VideoCodec { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>ffprobe's JSON for the file, parsed again when playing (streams, languages, dispositions).</summary>
    public string ProbeJson { get; set; } = string.Empty;
    /// <summary>Set when ffprobe couldn't read the file; it is listed but not playable.</summary>
    public string? ProbeError { get; set; }
    public DateTime ScannedUtc { get; set; }
    /// <summary>The file was gone at the last scan.</summary>
    public bool Missing { get; set; }
}

/// <summary>
/// A user's own tag on a show, a movie, an artist, an album or a single track (exactly one of the five ids):
/// everything a user tagged the same way is one of their lists, as the admins' library tags are everyone's categories;
/// a list of tracks is a playlist. Favorites is the list named <see cref="Favorites"/>. Goes with what it tags, and with
/// the user.
/// </summary>
public sealed class UserTag
{
    public const string Favorites = "Favorites";
    public const int MaxLength = 50;

    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public int? ShowId { get; set; }
    public int? MovieId { get; set; }
    public int? ArtistId { get; set; }
    public int? AlbumId { get; set; }
    /// <summary>A single track (music only; an episode is listed with its show).</summary>
    public int? MediaFileId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Something a user doesn't want to see: a show, a movie, an artist, an album, or one episode or track (exactly one of
/// the ids). It's still listed, greyed out, but left out of Play all, Shuffle, Continue watching and the next episode.
/// Only that user is affected.
/// </summary>
public sealed class HiddenItem
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int? ShowId { get; set; }
    public int? MovieId { get; set; }
    public int? ArtistId { get; set; }
    public int? AlbumId { get; set; }
    public int? MediaFileId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Where a user stopped in a file.</summary>
public sealed class PlaybackProgress
{
    public string UserId { get; set; } = string.Empty;
    public int MediaFileId { get; set; }
    public double PositionSeconds { get; set; }
    public bool Finished { get; set; }
    /// <summary>Taken off Continue watching (its ×); playing it again puts it back. The place is kept either way.</summary>
    public bool LeftContinue { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
