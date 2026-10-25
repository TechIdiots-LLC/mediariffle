using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaRiffle.Server.Updates;

/// <summary>Where releases are published: the same files on both.</summary>
public enum UpdateSource { GitLab, GitHub }

/// <summary>A published release: its version, notes (Markdown, from CHANGELOG.md) and the files attached to it.</summary>
public sealed record ReleaseInfo(string Version, string Name, string Notes, string Url, DateTime? PublishedUtc,
    bool Prerelease, IReadOnlyList<string> Files);

/// <summary>
/// Reads the release lists of MediaRiffle's GitLab project and GitHub mirror, and says which file of a release this
/// install would update from. The addresses are fixed here and in the updaters (deploy/linux/mediariffle-update,
/// deploy/windows/Update-MediaRiffle.ps1), which download from them themselves: the server only names a version.
/// </summary>
public static partial class Releases
{
    public const string GitLabProject = "https://gitlab.techidiots.net/techidiots-llc/mediariffle";
    public const string GitHubProject = "https://github.com/TechIdiots-LLC/mediariffle";
    public const string Checksums = "SHA256SUMS";

    public static string ListUrl(UpdateSource source) => source == UpdateSource.GitHub
        ? "https://api.github.com/repos/TechIdiots-LLC/mediariffle/releases?per_page=30"
        : "https://gitlab.techidiots.net/api/v4/projects/techidiots-llc%2Fmediariffle/releases?per_page=30";

    public static string Page(UpdateSource source) => source == UpdateSource.GitHub ? $"{GitHubProject}/releases" : $"{GitLabProject}/-/releases";

    /// <summary>A release version as the updaters accept it: 1.2.3 or 1.2.3-rc.1.</summary>
    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$")]
    public static partial Regex VersionPattern();

    /// <summary>A list of releases from either site's API, skipping drafts, upcoming releases and tags that aren't versions.</summary>
    public static List<ReleaseInfo> Parse(string json, UpdateSource source)
    {
        var list = new List<ReleaseInfo>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            var tag = Str(r, "tag_name") ?? "";
            var version = tag.StartsWith('v') ? tag[1..] : tag;
            if (!VersionPattern().IsMatch(version)) continue;
            if (Bool(r, "draft") || Bool(r, "upcoming_release")) continue;
            var files = new List<string>();
            if (source == UpdateSource.GitHub)
            {
                if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    files.AddRange(assets.EnumerateArray().Select(a => Str(a, "name")).OfType<string>());
            }
            else if (r.TryGetProperty("assets", out var assets) && assets.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
                files.AddRange(links.EnumerateArray().Select(a => Str(a, "name")).OfType<string>());
            var date = Str(r, source == UpdateSource.GitHub ? "published_at" : "released_at");
            list.Add(new ReleaseInfo(version, Str(r, "name") ?? tag, Str(r, source == UpdateSource.GitHub ? "body" : "description") ?? "",
                source == UpdateSource.GitHub ? Str(r, "html_url") ?? Page(source) : $"{GitLabProject}/-/releases/{Uri.EscapeDataString(tag)}",
                DateTime.TryParse(date, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null,
                version.Contains('-') || Bool(r, "prerelease"), files));
        }
        return list;
    }

    /// <summary>The file of <paramref name="version"/> this install updates from, or null where it can't update itself.</summary>
    public static string? FileFor(string version, Installer installer, Architecture arch) => installer switch
    {
        // The .deb's version: a pre-release sorts before its release as 0.2.0~rc.1 (build/tools/LinuxPackages.cs)
        Installer.Deb => $"mediariffle_{version.Replace('-', '~')}-1_{(arch == Architecture.Arm64 ? "arm64" : "amd64")}.deb",
        Installer.WindowsSetup => $"MediaRiffle-v{version}-win-{(arch == Architecture.Arm64 ? "arm64" : "x64")}-setup.exe",
        _ => null,
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>How this copy was installed, which says whether and how it can update itself.</summary>
public enum Installer { None, Deb, WindowsSetup }

/// <summary>Release versions in order: 1.2.3-rc.1 before 1.2.3, numeric pre-release parts by number (SemVer).</summary>
public static class VersionOrder
{
    public static int Compare(string a, string b)
    {
        static (int[] Core, string[] Pre) Split(string v)
        {
            v = v.Split('+')[0];
            var dash = v.IndexOf('-');
            var core = (dash < 0 ? v : v[..dash]).Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            return (core, dash < 0 ? [] : v[(dash + 1)..].Split('.'));
        }
        var (ca, pa) = Split(a);
        var (cb, pb) = Split(b);
        for (var i = 0; i < Math.Max(ca.Length, cb.Length); i++)
        {
            var c = (i < ca.Length ? ca[i] : 0).CompareTo(i < cb.Length ? cb[i] : 0);
            if (c != 0) return c;
        }
        if (pa.Length == 0 || pb.Length == 0) return pb.Length.CompareTo(pa.Length);   // a release after its pre-releases
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var na = int.TryParse(pa[i], out var x);
            var nb = int.TryParse(pb[i], out var y);
            var c = na && nb ? x.CompareTo(y) : na ? -1 : nb ? 1 : string.CompareOrdinal(pa[i], pb[i]);
            if (c != 0) return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }
}
