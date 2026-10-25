using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaRiffle.Server.Api;
using MediaRiffle.Server.Options;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Updates;

/// <summary>
/// Admin → Updates: looks for newer releases (every Updates:CheckHours) and, when an admin asks, hands a version to
/// the updater, which runs with the rights the service hasn't got: on Linux mediariffle-update.service (root),
/// started by mediariffle-update.path when the request file appears; on Windows the "MediaRiffle Update" scheduled
/// task (SYSTEM), started by event 4100 in the Application log. The updater downloads that version's .deb or setup
/// from the release itself, checks it against the release's SHA256SUMS, installs it, waits for the new version to
/// start (it writes <see cref="RunningFile"/>) and puts the old one back if it doesn't. It writes how it went to
/// <see cref="StatusFile"/>. Only the .deb and the Windows installer's installs can update this way.
/// </summary>
public sealed class UpdateService(IOptions<MediaRiffleOptions> options, ILogger<UpdateService> log) : BackgroundService
{
    public const int RequestEventId = 4100;

    /// <summary>
    /// Fixed, whatever DataDirectory says, as the updaters look there: the service may write it (/var/lib/mediariffle
    /// is its ReadWritePaths; %ProgramData%\MediaRiffle its folder).
    /// </summary>
    public static string Folder => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MediaRiffle", "update")
        : "/var/lib/mediariffle/update";
    public static string RequestFile => Path.Combine(Folder, "request");
    public static string StatusFile => Path.Combine(Folder, "status.json");
    public static string RunningFile => Path.Combine(Folder, "running");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30), DefaultRequestHeaders = { { "User-Agent", $"MediaRiffle/{AuthEndpoints.Version}" } } };
    private readonly SemaphoreSlim checking = new(1, 1);

    public IReadOnlyList<ReleaseInfo> Newer { get; private set; } = [];
    public DateTime? CheckedUtc { get; private set; }
    public string? Error { get; private set; }

    /// <summary>How this copy was installed: the .deb (/opt/mediariffle with its updater), the Windows installer, or otherwise.</summary>
    public static Installer Installer
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            if (OperatingSystem.IsLinux() && dir.StartsWith("/opt/mediariffle", StringComparison.Ordinal) && File.Exists("/usr/lib/mediariffle/mediariffle-update"))
                return Installer.Deb;
            if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(dir, "Update-MediaRiffle.ps1")) && File.Exists(Path.Combine(dir, "uninstall.exe")))
                return Installer.WindowsSetup;
            return Installer.None;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Tells an updater waiting on this version that it started
        if (Installer != Installer.None)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                await File.WriteAllTextAsync(RunningFile, AuthEndpoints.Version + "\n", stoppingToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Couldn't write {File}, which an update waits for: {Error}", RunningFile, ex.Message);
            }
        }
        var hours = options.Value.Updates.CheckHours;
        if (hours <= 0) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromHours(hours), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Reads the release list now: the releases newer than this version (pre-releases only with Updates:Prereleases).</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        await checking.WaitAsync(ct);
        var o = options.Value.Updates;
        try
        {
            var json = await Http.GetStringAsync(Releases.ListUrl(o.Source), ct);
            var current = AuthEndpoints.Version;
            Newer = Releases.Parse(json, o.Source)
                .Where(r => (o.Prereleases || !r.Prerelease) && (!Releases.VersionPattern().IsMatch(current) || VersionOrder.Compare(r.Version, current) > 0))
                .OrderByDescending(r => r.Version, Comparer<string>.Create(VersionOrder.Compare)).ToList();
            Error = null;
            if (Newer.Count > 0) log.LogInformation("MediaRiffle {Version} is available (this is {Current}): Admin → Updates", Newer[0].Version, current);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            Error = $"Couldn't read the releases from {o.Source}: {ex.Message}";
            log.LogWarning("{Error}", Error);
        }
        finally
        {
            CheckedUtc = DateTime.UtcNow;
            checking.Release();
        }
    }

    /// <summary>Why this install can't update itself from Admin, or null when it can.</summary>
    public string? WhyNot() => Installer switch
    {
        Installer.Deb or Installer.WindowsSetup => null,
        _ when OperatingSystem.IsLinux() => "Only an install from the .deb package (or the Proxmox template) updates itself from here. Update this one as it was installed.",
        _ when OperatingSystem.IsWindows() => "Only an install from the Windows installer (setup.exe) updates itself from here, not the .zip.",
        _ => "This system can't update MediaRiffle from here.",
    };

    /// <summary>The file this install needs from a release, and whether the release has it and its checksums.</summary>
    public static (string? File, bool Ready) Readiness(ReleaseInfo r)
    {
        var file = Releases.FileFor(r.Version, Installer, RuntimeInformation.OSArchitecture);
        return (file, file is not null && r.Files.Contains(file) && r.Files.Contains(Releases.Checksums));
    }

    /// <summary>The updater's report (status.json), as it wrote it, or null before any update.</summary>
    public static JsonNode? Status()
    {
        try { return File.Exists(StatusFile) ? JsonNode.Parse(File.ReadAllText(StatusFile)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>A request is waiting for the updater to pick it up.</summary>
    public static bool Pending => File.Exists(RequestFile);

    /// <summary>Asks the updater for <paramref name="version"/>; the error to show, or null once asked.</summary>
    public string? Request(string version)
    {
        if (WhyNot() is { } why) return why;
        if (Newer.FirstOrDefault(r => r.Version == version) is not { } release) return $"{version} isn't a newer release here; check again.";
        var (file, ready) = Readiness(release);
        if (!ready) return $"Release {version} has no {file} with checksums ({Releases.Checksums}) to update from.";
        if (Pending) return "An update is already waiting to start.";
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = RequestFile + ".tmp";
            File.WriteAllText(temp, $"id={Guid.NewGuid():N}\nversion={version}\nsource={options.Value.Updates.Source.ToString().ToLowerInvariant()}\n");
            File.Move(temp, RequestFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't write the update request ({RequestFile}): {ex.Message}";
        }
        if (OperatingSystem.IsWindows()) StartWindowsUpdater(version);
        log.LogWarning("Update to MediaRiffle {Version} requested from Admin", version);
        return null;
    }

    // The scheduled task starts on this event (Update-MediaRiffle.ps1 -Register); the installer made the source
    [SupportedOSPlatform("windows")]
    private void StartWindowsUpdater(string version)
    {
        try { System.Diagnostics.EventLog.WriteEntry("MediaRiffle", $"Update to MediaRiffle {version} requested from Admin.", System.Diagnostics.EventLogEntryType.Information, RequestEventId); }
        catch (Exception ex) { log.LogWarning("Couldn't write the update request to the event log: {Error}", ex.Message); }
    }
}
