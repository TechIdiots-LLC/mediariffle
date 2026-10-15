using Microsoft.AspNetCore.Identity;

namespace MediaRiffle.Server.Data;

public sealed class AppUser : IdentityUser
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>The best quality this user may stream (a Quality.Ladder id), or null for no limit.</summary>
    public string? MaxQuality { get; set; }
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
    public string Title { get; set; } = string.Empty;
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

public enum MediaKind { Episode, Movie }

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
/// A user's own tag on a show or a movie (exactly one of <see cref="ShowId"/> and <see cref="MovieId"/>): everything a
/// user tagged the same way is one of their lists, as the admins' library tags are everyone's categories. Favorites is
/// the list named <see cref="Favorites"/>. Goes with the show or movie, and with the user.
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
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Something a user doesn't want to see: a show, a movie, or one episode (exactly one of <see cref="ShowId"/>,
/// <see cref="MovieId"/> and <see cref="MediaFileId"/>). It's still listed, greyed out, but left out of Play all,
/// Shuffle, Continue watching and the next episode. Only that user is affected.
/// </summary>
public sealed class HiddenItem
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int? ShowId { get; set; }
    public int? MovieId { get; set; }
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
    public DateTime UpdatedUtc { get; set; }
}
