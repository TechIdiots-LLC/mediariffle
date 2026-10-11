using MediaRiffle.Server.Data;

namespace MediaRiffle.Server.Library;

/// <summary>A subtitle file beside a video, e.g. <c>Movie.en.srt</c> next to <c>Movie.mkv</c>.</summary>
public sealed record SidecarSubtitle(int Number, string Path, string Label);

/// <summary>Turns database records back into files on disk, never outside their library's folder.</summary>
public sealed class LibraryFiles(LibraryCatalog catalog)
{
    private static readonly string[] SubtitleExtensions = [".srt", ".vtt", ".ass", ".ssa"];

    /// <summary>The file's full path, or null if its library is gone or disabled, or the path would leave the library folder.</summary>
    public string? FullPath(MediaFile file) => Resolve(file.LibraryId, file.RelativePath);

    public string? Resolve(int libraryId, string relativePath)
    {
        var lib = catalog.Get(libraryId);
        return lib is { Enabled: true } ? Resolve(lib.Path, relativePath) : null;
    }

    /// <summary><paramref name="relativePath"/> under <paramref name="root"/>, or null if it would leave it (../, a sibling folder).</summary>
    public static string? Resolve(string root, string relativePath)
    {
        root = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSep, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? full : null;
    }

    /// <summary>Subtitle files named after the video (<c>Name.srt</c>, <c>Name.en.srt</c>, <c>Name.English.forced.srt</c>).</summary>
    public static List<SidecarSubtitle> Sidecars(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath);
        if (dir is null || !Directory.Exists(dir)) return new();
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var found = new List<SidecarSubtitle>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (!SubtitleExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileNameWithoutExtension(path);
                if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
                var extra = name[stem.Length..].Trim('.', ' ', '_', '-');
                found.Add(new SidecarSubtitle(found.Count, path,
                    (extra.Length > 0 ? extra.Replace('.', ' ') : "External") + " · " + Path.GetExtension(path).TrimStart('.')));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return found;
    }
}
