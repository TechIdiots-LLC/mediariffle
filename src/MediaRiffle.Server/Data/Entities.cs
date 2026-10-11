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

public sealed class Show
{
    public int Id { get; set; }
    public string Library { get; set; } = string.Empty;
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
    public string Library { get; set; } = string.Empty;
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
    public string Library { get; set; } = string.Empty;
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

/// <summary>Where a user stopped in a file.</summary>
public sealed class PlaybackProgress
{
    public string UserId { get; set; } = string.Empty;
    public int MediaFileId { get; set; }
    public double PositionSeconds { get; set; }
    public bool Finished { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
