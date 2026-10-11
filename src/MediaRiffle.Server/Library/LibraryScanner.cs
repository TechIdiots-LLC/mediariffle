using System.Text;
using System.Threading.Channels;
using MediaRiffle.Server.Data;
using MediaRiffle.Server.Media;
using MediaRiffle.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaRiffle.Server.Library;

/// <summary>
/// Keeps the database in step with the library folders: new and changed video files are probed with ffprobe and
/// filed under their show/season or movie, files that are gone are dropped. Runs at start, every
/// ScanIntervalMinutes, and when an admin asks. Replaces TongueD's prep_sql_shows/prep_sql_movies.
/// </summary>
public sealed class LibraryScanner : BackgroundService
{
    private readonly IServiceScopeFactory scopes;
    private readonly IOptions<MediaRiffleOptions> options;
    private readonly Ffmpeg ffmpeg;
    private readonly LibraryCatalog catalog;
    private readonly ILogger<LibraryScanner> log;
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public LibraryScanner(IServiceScopeFactory scopes, IOptions<MediaRiffleOptions> options, Ffmpeg ffmpeg, LibraryCatalog catalog, ILogger<LibraryScanner> log)
    {
        this.scopes = scopes;
        this.options = options;
        this.ffmpeg = ffmpeg;
        this.catalog = catalog;
        this.log = log;
        // A library added, re-pathed or enabled is scanned straight away
        catalog.Changed += (_, _) => RequestScan();
    }

    public bool IsScanning { get; private set; }
    public DateTime? LastScanUtc { get; private set; }
    public string LastResult { get; private set; } = "Not scanned yet";
    /// <summary>What the running scan is doing, e.g. "Movies: 340 of 1601 files read"; null when idle.</summary>
    public string? Progress { get; private set; }

    /// <summary>How each library's last (or current) scan went, by library id, for Admin → Libraries.</summary>
    public IReadOnlyDictionary<int, LibraryScanStatus> Status => _status;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, LibraryScanStatus> _status = new();

    /// <summary>Asks for a scan as soon as the current one (if any) is done.</summary>
    public void RequestScan() => _requests.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await ffmpeg.CheckAsync())
            LastResult = "ffprobe isn't available, so files can't be scanned";
        RequestScan();
        var interval = options.Value.ScanIntervalMinutes;
        while (!stoppingToken.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            if (interval > 0) wait.CancelAfter(TimeSpan.FromMinutes(interval));
            try { await _requests.Reader.ReadAsync(wait.Token); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { /* interval reached */ }
            catch (OperationCanceledException) { break; }

            IsScanning = true;
            try { LastResult = await ScanAllAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Library scan failed");
                LastResult = "Scan failed: " + ex.Message;
            }
            finally
            {
                IsScanning = false;
                Progress = null;
                LastScanUtc = DateTime.UtcNow;
            }
        }
    }

    private async Task<string> ScanAllAsync(CancellationToken ct)
    {
        var summary = new StringBuilder();
        foreach (var lib in catalog.Enabled)
        {
            Progress = $"{lib.Name}: looking for files…";
            if (!Directory.Exists(lib.Path))
            {
                // A share that isn't mounted (or a path the service account can't see) must not empty the library
                log.LogWarning("Library {Name}: folder {Path} isn't available; skipped, nothing removed", lib.Name, lib.Path);
                summary.Append($"{lib.Name}: folder not available. ");
                _status[lib.Id] = new("unavailable", "The folder isn't available (not mounted, or this server's account can't see it); nothing was removed.", DateTime.UtcNow);
                continue;
            }
            _status[lib.Id] = new("scanning", "Scanning…", DateTime.UtcNow);
            try
            {
                var (added, updated, removed, failed) = await ScanLibraryAsync(lib, ct);
                log.LogInformation("Library {Name}: {Added} added, {Updated} updated, {Removed} removed, {Failed} unreadable", lib.Name, added, updated, removed, failed);
                var result = $"{added} added, {updated} updated, {removed} removed, {failed} unreadable";
                summary.Append($"{lib.Name}: {result}. ");
                _status[lib.Id] = new(failed > 0 ? "warning" : "ok", result, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _status[lib.Id] = new("error", "Scan failed: " + ex.Message, DateTime.UtcNow);
                throw;
            }
        }
        return summary.Length == 0 ? "No libraries configured" : summary.ToString().Trim();
    }

    private async Task<(int Added, int Updated, int Removed, int Failed)> ScanLibraryAsync(LibraryInfo lib, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var root = Path.GetFullPath(lib.Path);

        var onDisk = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        foreach (var path in Directory.EnumerateFiles(root, "*", enumeration))
        {
            if (!MediaNameParser.IsVideo(path)) continue;
            var info = new FileInfo(path);
            // Release "sample" clips aren't the episode or movie
            if (info.Name.Contains("sample", StringComparison.OrdinalIgnoreCase) && info.Length < 200L * 1024 * 1024) continue;
            onDisk[Path.GetRelativePath(root, path).Replace('\\', '/')] = info;
        }

        var known = await db.MediaFiles.Where(f => f.LibraryId == lib.Id).ToDictionaryAsync(f => f.RelativePath, StringComparer.Ordinal, ct);
        var toProbe = onDisk.Where(kv => !known.TryGetValue(kv.Key, out var f) ||
                                         f.Size != kv.Value.Length || f.ModifiedUtc != kv.Value.LastWriteTimeUtc || f.ProbeError is not null)
                            .ToList();

        // Probing reads the start of every file, slow over a network share; a few at a time
        var probes = new Dictionary<string, (string? Json, string? Error)>();
        int read = 0;
        Progress = toProbe.Count == 0 ? $"{lib.Name}: {onDisk.Count} files, none new or changed" : $"{lib.Name}: 0 of {toProbe.Count} files read";
        await Parallel.ForEachAsync(toProbe, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, async (kv, token) =>
        {
            var result = await ffmpeg.ProbeAsync(kv.Value.FullName, token);
            lock (probes) probes[kv.Key] = result;
            Progress = $"{lib.Name}: {Interlocked.Increment(ref read)} of {toProbe.Count} files read";
        });
        if (toProbe.Count > 0) Progress = $"{lib.Name}: saving…";

        int added = 0, updated = 0, failed = 0;
        foreach (var (rel, info) in toProbe)
        {
            var (json, error) = probes[rel];
            var isNew = !known.TryGetValue(rel, out var file);
            file ??= new MediaFile { LibraryId = lib.Id, RelativePath = rel };
            file.Size = info.Length;
            file.ModifiedUtc = info.LastWriteTimeUtc;
            file.ScannedUtc = DateTime.UtcNow;
            file.Missing = false;
            file.ProbeError = error;
            if (json is not null)
            {
                file.ProbeJson = json;
                var probe = ProbeResult.Parse(json);
                file.DurationSeconds = probe.DurationSeconds;
                file.Container = probe.FormatName;
                file.VideoCodec = probe.Video?.Codec ?? string.Empty;
                file.Width = probe.Video?.Width ?? 0;
                file.Height = probe.Video?.Height ?? 0;
            }
            else failed++;

            if (lib.Kind == LibraryKind.Shows) await FileEpisodeAsync(db, lib, file, ct);
            else await FileMovieAsync(db, lib, file, ct);

            if (isNew) { db.MediaFiles.Add(file); added++; } else updated++;
            await db.SaveChangesAsync(ct);   // per file, so the shows and movies it created are found by the next
        }

        var gone = known.Values.Where(f => !onDisk.ContainsKey(f.RelativePath)).ToList();
        db.MediaFiles.RemoveRange(gone);
        await db.SaveChangesAsync(ct);

        // Shows, seasons and movies left without any file
        await db.Seasons.Where(s => s.Show!.LibraryId == lib.Id && !db.MediaFiles.Any(f => f.SeasonId == s.Id)).ExecuteDeleteAsync(ct);
        await db.Shows.Where(s => s.LibraryId == lib.Id && !db.MediaFiles.Any(f => f.ShowId == s.Id)).ExecuteDeleteAsync(ct);
        await db.Movies.Where(m => m.LibraryId == lib.Id && !db.MediaFiles.Any(f => f.MovieId == m.Id)).ExecuteDeleteAsync(ct);
        return (added, updated, gone.Count, failed);
    }

    private static async Task FileEpisodeAsync(AppDbContext db, LibraryInfo lib, MediaFile file, CancellationToken ct)
    {
        file.Kind = MediaKind.Episode;
        var ep = MediaNameParser.ParseEpisode(file.RelativePath);
        if (ep is null)
        {
            // A video directly in the Shows folder: its own one-episode show
            ep = new EpisodeInfo(MediaNameParser.CleanTitle(Path.GetFileNameWithoutExtension(file.RelativePath)), 1, null,
                MediaNameParser.CleanTitle(Path.GetFileNameWithoutExtension(file.RelativePath)));
        }
        var folder = file.RelativePath.Split('/')[0];
        var show = await db.Shows.FirstOrDefaultAsync(s => s.LibraryId == lib.Id && s.Folder == folder, ct);
        if (show is null)
        {
            show = new Show { LibraryId = lib.Id, Folder = folder, Title = MediaNameParser.CleanTitle(ep.Show) };
            db.Shows.Add(show);
            await db.SaveChangesAsync(ct);
        }
        var season = await db.Seasons.FirstOrDefaultAsync(s => s.ShowId == show.Id && s.Number == ep.Season, ct);
        if (season is null)
        {
            season = new Season { ShowId = show.Id, Number = ep.Season, Title = ep.Season == 0 ? "Specials" : $"Season {ep.Season}" };
            db.Seasons.Add(season);
            await db.SaveChangesAsync(ct);
        }
        file.ShowId = show.Id;
        file.SeasonId = season.Id;
        file.EpisodeNumber = ep.Episode;
        file.Title = ep.Title;
        file.MovieId = null;
    }

    private static async Task FileMovieAsync(AppDbContext db, LibraryInfo lib, MediaFile file, CancellationToken ct)
    {
        file.Kind = MediaKind.Movie;
        var mv = MediaNameParser.ParseMovie(file.RelativePath);
        var movie = await db.Movies.FirstOrDefaultAsync(m => m.LibraryId == lib.Id && m.GroupKey == mv.GroupKey, ct);
        if (movie is null)
        {
            movie = new Movie { LibraryId = lib.Id, GroupKey = mv.GroupKey, Title = mv.Title, Year = mv.Year };
            db.Movies.Add(movie);
            await db.SaveChangesAsync(ct);
        }
        file.MovieId = movie.Id;
        file.PartNumber = mv.Part;
        file.Title = mv.Title;
        file.ShowId = null;
        file.SeasonId = null;
    }
}

/// <param name="State">ok, warning (some files unreadable), scanning, unavailable (folder missing) or error.</param>
public sealed record LibraryScanStatus(string State, string Message, DateTime Utc);
