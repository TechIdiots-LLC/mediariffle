namespace MediaRiffle.Server.Options;

public enum LibraryKind { Shows, Movies, Music }

/// <summary>
/// A folder of media: a "Shows" library holds Show/Season NN/episode files, a "Movies" library holds movie files, a
/// "Music" library holds Artist/Album/track files (read from their tags when they have them).
/// </summary>
public sealed class LibraryOptions
{
    /// <summary>Shown in the UI and used in URLs; must be unique.</summary>
    public string Name { get; set; } = string.Empty;
    public LibraryKind Kind { get; set; }
    /// <summary>Absolute folder path. Only files under it are ever read or streamed.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>The video encoder used when a file has to be transcoded.</summary>
public enum VideoEncoder { Libx264, Nvenc, Qsv, Vaapi }

public sealed class TranscodingOptions
{
    /// <summary>Folder of the ffmpeg and ffprobe executables, or empty to find them on PATH.</summary>
    public string FfmpegFolder { get; set; } = string.Empty;
    public VideoEncoder Encoder { get; set; } = VideoEncoder.Libx264;
    /// <summary>For Vaapi: the render device, e.g. /dev/dri/renderD128.</summary>
    public string VaapiDevice { get; set; } = "/dev/dri/renderD128";
    /// <summary>Transcoded video is scaled down to at most this height.</summary>
    public int MaxHeight { get; set; } = 1080;
    /// <summary>Peak video bitrate of a transcode, in kbit/s.</summary>
    public int MaxVideoKbps { get; set; } = 8000;
    /// <summary>Streams transcoding or remuxing at once, across all users; more are refused until one ends.</summary>
    public int MaxSessions { get; set; } = 4;
    public int MaxSessionsPerUser { get; set; } = 2;
    /// <summary>Length of each HLS segment, in seconds.</summary>
    public int SegmentSeconds { get; set; } = 4;
    /// <summary>A stream nobody has fetched from for this long is stopped and its files deleted.</summary>
    public int IdleTimeoutSeconds { get; set; } = 90;
}

/// <summary>
/// Sign-in with Google (OpenID Connect), for accounts that have linked a Google account. Off unless both are set;
/// they come from an OAuth client of type "Web application" in the Google Cloud console.
/// </summary>
public sealed class GoogleOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed class MediaRiffleOptions
{
    public const string Section = "MediaRiffle";

    public List<LibraryOptions> Libraries { get; set; } = new();
    public TranscodingOptions Transcoding { get; set; } = new();
    public GoogleOptions Google { get; set; } = new();

    /// <summary>
    /// Where the database, keys, thumbnails and transcodes live. Defaults to /var/lib/mediariffle on Linux and
    /// %ProgramData%\MediaRiffle on Windows; the service account needs read/write access to it.
    /// </summary>
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>How often the libraries are rescanned for new or changed files, in minutes (0 = only at start and on request).</summary>
    public int ScanIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Proxies trusted to set X-Forwarded-For/-Proto (e.g. nginx on 127.0.0.1). Needed behind a reverse proxy so
    /// rate limiting sees the real client and secure cookies know the request was HTTPS.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = new() { "127.0.0.1", "::1" };

    /// <summary>
    /// How many proxies sit in front of the app: 1 for nginx or Apache alone, 2 for HAProxy → Apache. Each
    /// one's address must be in <see cref="TrustedProxies"/>.
    /// </summary>
    public int ForwardLimit { get; set; } = 1;

    public string ResolvedDataDirectory => string.IsNullOrWhiteSpace(DataDirectory)
        ? (OperatingSystem.IsWindows()
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MediaRiffle")
            : "/var/lib/mediariffle")
        : DataDirectory;

    public string TranscodeDirectory => System.IO.Path.Combine(ResolvedDataDirectory, "transcode");
    public string ImageDirectory => System.IO.Path.Combine(ResolvedDataDirectory, "images");
    /// <summary>Music converted for browsers that can't play the file itself (kept, oldest dropped past a size).</summary>
    public string AudioDirectory => System.IO.Path.Combine(ResolvedDataDirectory, "audio");
    public string KeysDirectory => System.IO.Path.Combine(ResolvedDataDirectory, "keys");
    public string DatabasePath => System.IO.Path.Combine(ResolvedDataDirectory, "mediariffle.db");
}
