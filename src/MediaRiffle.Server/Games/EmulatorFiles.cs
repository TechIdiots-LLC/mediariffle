using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaRiffle.Server.Library;
using MediaRiffle.Server.Options;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Games;

/// <summary>A package or file an admin gave that MediaRiffle won't use, with why, in words for them.</summary>
public sealed class PackageException(string message) : Exception(message);

/// <summary>
/// The browser emulator: EmulatorJS (GPL-3.0) and its RetroArch cores, which MediaRiffle doesn't ship. An admin adds
/// them under Admin → Games, downloaded from npm (each package checked against the registry's SHA-512) or uploaded as
/// the npm .tgz files, into the data folder: data/ is EmulatorJS's data folder, served at /emulator/; bios/ holds the
/// BIOS files an admin added, per system, sent only to signed-in users.
/// </summary>
public sealed class EmulatorFiles(IOptions<MediaRiffleOptions> options, ILogger<EmulatorFiles> log)
{
    /// <summary>The EmulatorJS release MediaRiffle was tested with; downloads get this one.</summary>
    public const string Version = "4.2.3";
    private const string Registry = "https://registry.npmjs.org/";
    private const long MaxPackage = 200L * 1024 * 1024;

    // EmulatorJS's scripts in the order its loader.js loads them, bundled into the emulator.min.js it looks for first
    // (npm has only the separate files; without the bundle the loader goes into debug mode, which asks EmulatorJS's
    // CDN for updates)
    private static readonly string[] Scripts =
        ["emulator.js", "nipplejs.js", "shaders.js", "storage.js", "gamepad.js", "GameManager.js", "socket.io.min.js", "compression.js"];

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
        DefaultRequestHeaders = { { "User-Agent", $"MediaRiffle/{Api.AuthEndpoints.Version}" } },
    };

    private readonly SemaphoreSlim _busy = new(1, 1);

    public string Root => options.Value.EmulatorDirectory;
    public string DataDirectory => Path.Combine(Root, "data");
    public string BiosDirectory => Path.Combine(Root, "bios");
    private string CoresDirectory => Path.Combine(DataDirectory, "cores");

    /// <summary>What a download or upload is doing now; null when idle.</summary>
    public string? Progress { get; private set; }
    /// <summary>How the last one went.</summary>
    public string? LastResult { get; private set; }

    /// <summary>The EmulatorJS version in place, or null when there's none.</summary>
    public string? Installed
    {
        get
        {
            var loader = Path.Combine(DataDirectory, "loader.js");
            var bundle = Path.Combine(DataDirectory, "emulator.min.js");
            if (!File.Exists(loader) || !File.Exists(bundle)) return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataDirectory, "version.json")));
                return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : "?";
            }
            catch (Exception ex) when (ex is IOException or JsonException) { return "?"; }
        }
    }

    /// <summary>Cores in place: those with a report and at least one build.</summary>
    public HashSet<string> Cores()
    {
        var reports = Path.Combine(CoresDirectory, "reports");
        if (!Directory.Exists(reports)) return [];
        return Directory.EnumerateFiles(reports, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Where(core => Directory.EnumerateFiles(CoresDirectory, core + "-*wasm.data").Any())
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Whether a game of this system can start: the emulator, its core, and a BIOS it can't do without.</summary>
    public bool CanPlay(GameSystem system) =>
        system.Core is { } core && Installed is not null && Cores().Contains(core) && (system.Bios is null || Bios(system.Id).Count > 0);

    // ── BIOS ──

    /// <summary>The BIOS files added for a system, by name.</summary>
    public List<string> Bios(string system)
    {
        var dir = BiosFolder(system);
        return dir is not null && Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
    }

    public string? BiosFolder(string system) => GameSystems.Get(system) is { } s ? Path.Combine(BiosDirectory, s.Id) : null;

    public async Task<string?> AddBiosAsync(string system, string name, Stream content, CancellationToken ct)
    {
        if (BiosFolder(system) is not { } dir) return "No such system.";
        var file = SafeName(name);
        if (file is null) return "Give the file its BIOS name, e.g. scph5501.bin.";
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, file);
        await using (var to = File.Create(path + ".part"))
            await CopyLimitedAsync(content, to, 64L * 1024 * 1024, ct);
        File.Move(path + ".part", path, overwrite: true);
        return null;
    }

    public bool RemoveBios(string system, string name)
    {
        if (BiosFolder(system) is not { } dir || SafeName(name) is not { } file) return false;
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    // ── Installing ──

    /// <summary>
    /// Downloads EmulatorJS (when it isn't in place yet, or is another version) and the given cores from npm, in the
    /// background; <see cref="Progress"/> and <see cref="LastResult"/> tell how it goes. False when one is running.
    /// </summary>
    public bool StartDownload(IReadOnlyCollection<string> cores)
    {
        if (!_busy.Wait(0)) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                var done = new List<string>();
                if (Installed != Version)
                {
                    Progress = $"Downloading EmulatorJS {Version}…";
                    await InstallPackageAsync(await DownloadAsync("@emulatorjs/emulatorjs", CancellationToken.None), CancellationToken.None);
                    done.Add($"EmulatorJS {Version}");
                }
                var have = Cores();
                foreach (var core in cores.Where(c => !have.Contains(c)))
                {
                    Progress = $"Downloading the {core} core ({done.Count + 1} of {cores.Count})…";
                    await InstallPackageAsync(await DownloadAsync("@emulatorjs/core-" + core, CancellationToken.None), CancellationToken.None);
                    done.Add(core);
                }
                LastResult = done.Count == 0 ? "Everything was in place already." : "Added " + string.Join(", ", done) + ".";
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Emulator download failed");
                LastResult = "Download failed: " + ex.Message;
            }
            finally
            {
                Progress = null;
                _busy.Release();
            }
        });
        return true;
    }

    /// <summary>An uploaded npm package (.tgz) of EmulatorJS or one of its cores. Returns what it was, or throws.</summary>
    public async Task<string> AddUploadAsync(Stream upload, CancellationToken ct)
    {
        if (!await _busy.WaitAsync(TimeSpan.FromSeconds(1), ct)) throw new InvalidOperationException("A download is running; try again when it's done.");
        try
        {
            var temp = Path.Combine(Root, "upload.tgz.part");
            Directory.CreateDirectory(Root);
            try
            {
                await using (var to = File.Create(temp)) await CopyLimitedAsync(upload, to, MaxPackage, ct);
                return LastResult = "Added " + await InstallPackageAsync(temp, ct) + ".";
            }
            finally { File.Delete(temp); }
        }
        finally { _busy.Release(); }
    }

    /// <summary>Downloads a package's <see cref="Version"/> to a temporary file, checked against the registry's hash.</summary>
    private async Task<string> DownloadAsync(string package, CancellationToken ct)
    {
        var meta = await Http.GetStringAsync(Registry + package.Replace("/", "%2f") + "/" + Version, ct);
        using var doc = JsonDocument.Parse(meta);
        var dist = doc.RootElement.GetProperty("dist");
        var tarball = new Uri(dist.GetProperty("tarball").GetString()!);
        var integrity = dist.GetProperty("integrity").GetString() ?? "";
        if (tarball.Scheme != "https" || tarball.Host != "registry.npmjs.org" || !integrity.StartsWith("sha512-", StringComparison.Ordinal))
            throw new PackageException($"{package}: the registry gave an unexpected download.");

        Directory.CreateDirectory(Root);
        var temp = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".tgz.part");
        using var response = await Http.GetAsync(tarball, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using (var to = File.Create(temp))
        await using (var from = await response.Content.ReadAsStreamAsync(ct))
            await CopyLimitedAsync(from, to, MaxPackage, ct);
        await using (var check = File.OpenRead(temp))
        {
            var hash = Convert.ToBase64String(await SHA512.HashDataAsync(check, ct));
            if (hash != integrity["sha512-".Length..])
            {
                File.Delete(temp);
                throw new PackageException($"{package}: the download doesn't match the registry's checksum.");
            }
        }
        return temp;
    }

    /// <summary>
    /// Puts an npm package of EmulatorJS (its data folder, plus the bundle built from it) or of a core (its builds and
    /// report) in place, and deletes the package file. Returns what it was.
    /// </summary>
    private async Task<string> InstallPackageAsync(string tgz, CancellationToken ct)
    {
        try
        {
            var files = await ReadPackageAsync(tgz, ct);
            if (!files.TryGetValue("package.json", out var manifest)) throw new PackageException("That isn't an npm package (no package.json).");
            using var doc = JsonDocument.Parse(manifest);
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";

            if (name == "@emulatorjs/emulatorjs")
            {
                if (!files.ContainsKey("data/loader.js") || Scripts.Any(s => !files.ContainsKey("data/src/" + s)))
                    throw new PackageException("The EmulatorJS package is missing files.");
                var staging = DataDirectory + ".new";
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                foreach (var (path, data) in files.Where(f => f.Key.StartsWith("data/", StringComparison.Ordinal) && !f.Key.StartsWith("data/cores/", StringComparison.Ordinal)))
                    await WriteAsync(Path.Combine(staging, path["data/".Length..]), data, ct);
                // The bundle the loader looks for first, as EmulatorJS's own build makes it (unminified)
                var bundle = new StringBuilder();
                foreach (var script in Scripts)
                    bundle.Append(Encoding.UTF8.GetString(files["data/src/" + script])).Append("\n;\n");
                await File.WriteAllTextAsync(Path.Combine(staging, "emulator.min.js"), bundle.ToString(), ct);
                File.Copy(Path.Combine(staging, "emulator.css"), Path.Combine(staging, "emulator.min.css"), true);
                // The cores stay: they're added one by one
                if (Directory.Exists(CoresDirectory)) Directory.Move(CoresDirectory, Path.Combine(staging, "cores"));
                if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, true);
                Directory.Move(staging, DataDirectory);
                log.LogInformation("EmulatorJS {Version} added", version);
                return $"EmulatorJS {version}";
            }

            if (name.StartsWith("@emulatorjs/core-", StringComparison.Ordinal))
            {
                var core = name["@emulatorjs/core-".Length..];
                var builds = files.Where(f => f.Key.EndsWith("-wasm.data", StringComparison.Ordinal) && !f.Key.Contains('/')).ToList();
                if (builds.Count == 0 || !files.TryGetValue($"reports/{core}.json", out var report))
                    throw new PackageException($"The {core} package is missing its builds.");
                foreach (var (path, data) in builds) await WriteAsync(Path.Combine(CoresDirectory, path), data, ct);
                await WriteAsync(Path.Combine(CoresDirectory, "reports", core + ".json"), report, ct);
                log.LogInformation("EmulatorJS core {Core} {Version} added", core, version);
                return $"the {core} core";
            }
            throw new PackageException($"{name} isn't EmulatorJS or one of its cores (@emulatorjs/…).");
        }
        finally { File.Delete(tgz); }
    }

    /// <summary>The files of an npm .tgz, by their path inside its package/ folder; anything else is refused.</summary>
    private static async Task<Dictionary<string, byte[]>> ReadPackageAsync(string tgz, CancellationToken ct)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        await using var file = File.OpenRead(tgz);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null) continue;
            var name = entry.Name.Replace('\\', '/');
            if (!name.StartsWith("package/", StringComparison.Ordinal)) continue;
            name = name["package/".Length..];
            if (name.Length == 0 || name.StartsWith('/') || name.Split('/').Any(p => p is "" or "." or ".."))
                throw new PackageException("The package has a file outside its folder.");
            total += entry.Length;
            if (total > MaxPackage) throw new PackageException("The package is too large.");
            using var buffer = new MemoryStream();
            await entry.DataStream.CopyToAsync(buffer, ct);
            files[name] = buffer.ToArray();
        }
        return files;
    }

    private static async Task WriteAsync(string path, byte[] data, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, data, ct);
    }

    private static async Task CopyLimitedAsync(Stream from, Stream to, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await from.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > limit) throw new PackageException("The file is too large.");
            await to.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    /// <summary>A file name as given, if it's a plain one (no folders, nothing hidden); null otherwise.</summary>
    public static string? SafeName(string? name)
    {
        var file = Path.GetFileName((name ?? "").Replace('\\', '/').Trim());
        return file.Length is > 0 and <= 120 && !file.StartsWith('.') && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ? file : null;
    }
}
